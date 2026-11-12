using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PotatoVN.Backup.Core.Engine;

/// <summary>只把已完成的数据包发布为备份版本；软件历史与存档历史分别维护。</summary>
public sealed partial class BackupCoordinator
{
    private readonly string _root;
    private readonly IBackupHost _host;
    private readonly Func<ProviderProfile, IBackupProvider> _providers;
    private readonly SettingsStore _settingsStore;
    public BackupSettings Settings { get; }
    public PendingRestoreStore Pending { get; }
    public event Action<string>? Progress;
    public event Action<int, string>? BackupProgress;
    public event Action<int, string>? RestoreProgress;
    private void ReportRestore(int percent, string text)
    {
        RestoreProgress?.Invoke(percent, text);
        Report(text);
    }
    private void ReportBackup(int percent, string text)
    {
        BackupProgress?.Invoke(percent, text);
        Report(text);
    }

    public BackupCoordinator(string localRoot, IBackupHost host, BackupSettings settings,
        SettingsStore settingsStore, PendingRestoreStore pending, Func<ProviderProfile, IBackupProvider> providers)
    {
        _root = localRoot;
        _host = host;
        Settings = settings;
        _settingsStore = settingsStore;
        Pending = pending;
        _providers = providers;
    }

    public ProviderProfile ActiveProfile => Settings.Profiles.FirstOrDefault(p => p.Id == Settings.ActiveProfileId)
        ?? throw new InvalidOperationException("请先添加并选择一个备份渠道。");
    private void Report(string text) => Progress?.Invoke(text);
    private static string NewId() => DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "_" + Guid.NewGuid().ToString("N");
    private string Work()
    {
        try { LocalWorkCleanup.RemoveExpired(_root, DateTimeOffset.UtcNow); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Report("本机临时文件清理未完成：" + error.Message); }
        string path = Path.Combine(_root, "work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public Task<SnapshotManifest> BackupAsync(CancellationToken ct = default, bool automatic = false) => BackupPackedAsync(ct, automatic);

    private async Task<SnapshotManifest> BackupLegacyAsync(CancellationToken ct = default)
    {
        if (Settings.Content == BackupContent.None) throw new InvalidOperationException("请至少选择一种备份内容。");
        ProviderProfile profile = ActiveProfile;
        using IBackupProvider provider = _providers(profile);
        string work = Work();
        try
        {
            Report("正在连接备份渠道…");
            await provider.ProbeAsync(ct);
            if (!await provider.ExistsAsync("v1/repository.json", ct))
                await PutJsonAsync(provider, "v1/repository.json", new { formatVersion = 1, id = Guid.NewGuid(), createdUtc = DateTimeOffset.UtcNow }, work, ct);
            else
            {
                JsonObject repository = await GetJsonAsync<JsonObject>(provider, "v1/repository.json", work, ct);
                if ((repository["formatVersion"] ?? repository["FormatVersion"])?.GetValue<int>() != 1)
                    throw new InvalidDataException("云端仓库格式不受支持，请使用匹配版本的插件。");
            }
            IReadOnlyList<GameIdentity> all = _host.GetGames();
            List<GameIdentity> selected = all.Where(Settings.IncludesGame).ToList();
            if (Settings.Content.HasFlag(BackupContent.Saves) && selected.Any(g => g.IsRunning))
                throw new InvalidOperationException("所选游戏仍在运行，已延后本次备份，请在游戏退出后重试。");
            SnapshotManifest snapshot = new()
            {
                Id = NewId(), DeviceId = Settings.DeviceId, DeviceName = Settings.DeviceName,
                CreatedUtc = DateTimeOffset.UtcNow, Content = Settings.Content, Games = selected.Select(CanonicalIdentity).ToList()
            };
            string remote = $"v1/snapshots/{Settings.DeviceId:N}/{snapshot.Id}";
            snapshot.ManifestPath = remote + "/manifest.json";
            BackupContent softwareTypes = Settings.Content & ~BackupContent.Saves;
            if (softwareTypes != BackupContent.None)
            {
                Report("正在生成所选软件资料…");
                HostExport exported = await _host.ExportAsync(Path.Combine(work, "export"), ct);
                SoftwarePackage package = await SoftwareBackupCodec.CreateAsync(exported, Settings, all, Path.Combine(work, "software"), ct);
                snapshot.Games = package.Games.ToList();
                if (package.DataPath != null)
                    snapshot.Data = await UploadFileAsync(provider, package.DataPath, remote + "/data.zip", ct);
                if (package.MediaPath != null)
                    snapshot.Media = await UploadFileAsync(provider, package.MediaPath, remote + "/media.zip", ct);
            }
            if (Settings.Content.HasFlag(BackupContent.Saves))
            {
                foreach (GameIdentity game in selected)
                {
                    ct.ThrowIfCancellationRequested();
                    Report($"正在备份存档：{game.Name}");
                    Guid canonicalId = Settings.GameMappings.LastOrDefault(m => m.LocalGameId == game.Id)?.BackupGameId ?? game.Id;
                    string saveRoot = $"v1/saves/{canonicalId:N}/{Settings.DeviceId:N}";
                    List<SaveManifest> previous = await ListSaveVersionsAsync(provider, saveRoot, work, ct);
                    SaveManifest? latest = await LatestReadableAsync(provider, previous, ct);
                    if (latest == null)
                    {
                        List<SaveManifest> otherDevices = await ListSaveVersionsAsync(provider, $"v1/saves/{canonicalId:N}", work, ct);
                        latest = await LatestReadableAsync(provider, otherDevices.Where(v => v.DeviceId != Settings.DeviceId), ct);
                        if (latest != null) snapshot.Notices.Add($"{game.Name}：可引用其他设备 {latest.DeviceId:N} 在 {latest.CapturedUtc:O} 备份的存档。");
                    }
                    GameSaveConfig? config = Settings.SaveConfigs.FirstOrDefault(c => c.GameId == game.Id);
                    SaveManifest? created = null;
                    if (config?.Locations.Count > 0)
                    {
                        List<SaveLocation> locations = config.Locations.Select(CloneLocation).ToList();
                        foreach (SaveLocation location in locations)
                            if (Settings.CustomSavePaths.TryGetValue(location.Id, out string? custom)) location.CustomPath = custom;
                        GameIdentity identity = new() { Id = canonicalId, Name = game.Name, SourceIds = game.SourceIds, LocalDirectory = game.LocalDirectory };
                        created = await SaveArchiveService.CreateAsync(identity, locations, Path.Combine(work, canonicalId.ToString("N") + ".zip"), Settings.DeviceId, ct);
                    }
                    if (created == null)
                    {
                        if (latest != null)
                        {
                            snapshot.SaveManifestPaths.Add(latest.ManifestPath);
                            snapshot.Notices.Add($"{game.Name}：本次没有可完整读取的存档，保留 {latest.CapturedUtc:O} 的已有版本。");
                        }
                        else snapshot.Notices.Add($"{game.Name}：尚无可备份的存档位置或文件。");
                    }
                    else if (latest != null && created.ContentHash == latest.ContentHash && SameLocations(created.Locations, latest.Locations))
                        snapshot.SaveManifestPaths.Add(latest.ManifestPath);
                    else
                    {
                        created.Id = NewId();
                        created.ManifestPath = saveRoot + "/" + created.Id + "/manifest.json";
                        created.ArchivePath = saveRoot + "/" + created.Id + "/save.zip";
                        string localSave = Path.Combine(work, canonicalId.ToString("N") + ".zip");
                        FileRecord uploaded = await UploadFileAsync(provider, localSave, created.ArchivePath, ct);
                        created.Sha256 = uploaded.Sha256;
                        created.Length = uploaded.Length;
                        await PutJsonAsync(provider, created.ManifestPath, created, work, ct);
                        snapshot.SaveManifestPaths.Add(created.ManifestPath);
                    }
                }
            }
            Report("正在发布完整备份清单…");
            await PutJsonAsync(provider, snapshot.ManifestPath, snapshot, work, ct);
            Settings.LastSuccessfulBackupUtc = DateTimeOffset.UtcNow;
            await _settingsStore.SaveAsync(Settings);
            Report("备份已完成，正在整理历史版本…");
            try { await PruneAsync(provider, work, ct); }
            catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException or OperationCanceledException or JsonException)
            { snapshot.Notices.Add("备份已完成，历史清理未完成：" + e.Message); }
            Report(snapshot.Notices.Count == 0 ? "备份完成。" : "备份完成；" + string.Join("；", snapshot.Notices));
            return snapshot;
        }
        finally { RemoveWork(work); }
    }

    private GameIdentity CanonicalIdentity(GameIdentity game) => new()
    {
        Id = Settings.GameMappings.LastOrDefault(m => m.LocalGameId == game.Id)?.BackupGameId ?? game.Id,
        Name = game.Name, SourceIds = game.SourceIds
    };

    private static async Task<SaveManifest?> LatestReadableAsync(IBackupProvider provider, IEnumerable<SaveManifest> saves, CancellationToken ct)
    {
        foreach (SaveManifest save in saves.OrderByDescending(s => s.CapturedUtc))
            if (await provider.ExistsAsync(save.ArchivePath, ct)) return save;
        return null;
    }

    private static bool SameLocations(IReadOnlyList<SaveLocation> left, IReadOnlyList<SaveLocation> right)
    {
        static string Rule(SaveLocation location) => $"{location.Id}|{location.BaseKind}|{location.IsFile}|{location.RelativePath.Replace('\\', '/').TrimEnd('/')}";
        return left.OrderBy(l => l.Id, StringComparer.Ordinal).Select(Rule)
            .SequenceEqual(right.OrderBy(l => l.Id, StringComparer.Ordinal).Select(Rule), StringComparer.OrdinalIgnoreCase);
    }

    private static SaveLocation CloneLocation(SaveLocation value) => new()
    { Id = value.Id, BaseKind = value.BaseKind, RelativePath = value.RelativePath, IsFile = value.IsFile, CustomPath = value.CustomPath };

    public Task<IReadOnlyList<SnapshotManifest>> ListSnapshotsAsync(CancellationToken ct = default) => ListPackedSnapshotsAsync(ct);

    private async Task<IReadOnlyList<SnapshotManifest>> ListLegacySnapshotsAsync(CancellationToken ct = default)
    {
        using IBackupProvider provider = _providers(ActiveProfile);
        string work = Work();
        try { return (await ListSnapshotsInternalAsync(provider, work, ct)).OrderByDescending(s => s.CreatedUtc).ToList(); }
        finally { RemoveWork(work); }
    }

    private static async Task<List<SnapshotManifest>> ListSnapshotsInternalAsync(IBackupProvider provider, string work, CancellationToken ct)
    {
        List<SnapshotManifest> snapshots = [];
        foreach (RemoteItem file in await WalkAsync(provider, "v1/snapshots", ct))
        {
            if (!file.Path.EndsWith("/manifest.json", StringComparison.Ordinal)) continue;
            SnapshotManifest snapshot = await GetJsonAsync<SnapshotManifest>(provider, file.Path, work, ct);
            ValidateSnapshot(snapshot, file.Path);
            snapshots.Add(snapshot);
        }
        return snapshots;
    }

    private static async Task<List<SaveManifest>> ListSaveVersionsAsync(IBackupProvider provider, string root, string work, CancellationToken ct)
    {
        List<SaveManifest> saves = [];
        foreach (RemoteItem file in await WalkAsync(provider, root, ct))
        {
            if (!file.Path.EndsWith("/manifest.json", StringComparison.Ordinal)) continue;
            SaveManifest save = await GetJsonAsync<SaveManifest>(provider, file.Path, work, ct);
            ValidateSave(save, file.Path);
            saves.Add(save);
        }
        return saves;
    }

    public Task StageRestoreAsync(SnapshotManifest snapshot, BackupContent content, CancellationToken ct = default,
        RestoreMatchingDecisions? matching = null) => StageIncrementalRestoreAsync(snapshot, content, ct, matching);

    private async Task StageLegacyRestoreAsync(SnapshotManifest snapshot, BackupContent content, CancellationToken ct = default,
        RestoreMatchingDecisions? matching = null)
    {
        ValidateSnapshot(snapshot, snapshot.ManifestPath);
        if (content == BackupContent.None || (content & snapshot.Content) != content)
            throw new InvalidOperationException("请选择该版本实际包含的恢复类型。");
        if (!RestoreMatchingPreview.RequiresMatching(content)) matching = null;
        IReadOnlyList<GameIdentity> localGames = _host.GetGames();
        if (matching != null) RestoreMatchingPreview.Validate(matching, snapshot.Games, localGames);
        // 预览选择和兼容层学习到的映射只在下载、校验及合并成功后提交。
        BackupSettings working = JsonSerializer.Deserialize<BackupSettings>(JsonSerializer.Serialize(Settings, BackupJson.Options), BackupJson.Options)!;
        working.ActiveRestoreMatching = matching;
        if (matching != null) RestoreMatchingPreview.ApplyMappings(working, matching);
        ProviderProfile profile = ActiveProfile;
        using IBackupProvider provider = _providers(profile);
        string work = Work();
        try
        {
            Report("正在下载并校验备份…");
            string? data = null, media = null;
            if ((content & ~BackupContent.Saves) != BackupContent.None)
            {
                if (snapshot.Data == null) throw new InvalidDataException("这个版本缺少软件资料包。");
                data = Path.Combine(work, "data.zip");
                await DownloadCheckedAsync(provider, snapshot.Data, data, ct);
                if (snapshot.Media != null)
                {
                    media = Path.Combine(work, "media.zip");
                    await DownloadCheckedAsync(provider, snapshot.Media, media, ct);
                }
            }
            var downloadedSaves = new List<(SaveManifest Save, string Archive)>();
            if (content.HasFlag(BackupContent.Saves))
            {
                foreach (string manifestPath in snapshot.SaveManifestPaths)
                {
                    SaveManifest save = await GetJsonAsync<SaveManifest>(provider, manifestPath, work, ct);
                    ValidateSave(save, manifestPath);
                    if (matching != null && !matching.Targets.ContainsKey(save.Game.Id))
                        throw new InvalidDataException("存档清单中的游戏不属于当前快照，请检查备份完整性。");
                    string archive = Path.Combine(work, save.Sha256 + ".zip");
                    if (!File.Exists(archive))
                        await DownloadCheckedAsync(provider, new FileRecord { Path = save.ArchivePath, Length = save.Length, Sha256 = save.Sha256 }, archive, ct);
                    downloadedSaves.Add((save, archive));
                }
            }
            SoftwareMergeResult? merged = null;
            if (data != null || matching?.Merges.Count > 0)
            {
                HostExport current = await _host.ExportAsync(Path.Combine(work, "current"), ct);
                merged = data != null
                    ? await SoftwareBackupCodec.MergeAsync(current, data, media, content, working, localGames, Path.Combine(work, "merged"), ct)
                    : await SoftwareBackupCodec.MergeLocalGamesAsync(current, working, localGames, Path.Combine(work, "merged"), ct);
                if (merged.Conflicts.Count > 0)
                    throw new InvalidOperationException("请先确定以下游戏的对应关系，再恢复：" + string.Join("；", merged.Conflicts));
            }
            foreach (var (save, archive) in downloadedSaves)
            {
                Guid? target = matching != null ? matching.Targets[save.Game.Id] : null;
                IReadOnlyList<Guid>? excluded = matching == null ? null
                    : target == null ? localGames.Select(g => g.Id).ToList() : Array.Empty<Guid>();
                await Pending.StageAsync(profile, save, archive, ct, excluded, target);
            }
            if (matching != null)
            {
                foreach (PendingRestore item in Pending.Items.Where(i => !i.IsBusy))
                {
                    bool hasChoice = matching.Targets.TryGetValue(item.Manifest.Game.Id, out Guid? target);
                    if (!hasChoice)
                    {
                        Guid? previous = Settings.GameMappings.LastOrDefault(m => m.BackupGameId == item.Manifest.Game.Id)?.LocalGameId;
                        target = working.GameMappings.LastOrDefault(m => m.BackupGameId == item.Manifest.Game.Id)?.LocalGameId;
                        if (target == null || previous == target) continue;
                    }
                    item.ExcludedLocalGameIds = target == null ? localGames.Select(g => g.Id).ToList() : [];
                    if (target != null && item.LocalGameId == target) continue;
                    item.LocalGameId = target;
                    item.TargetOverride = null;
                    item.TargetPath = null;
                    item.Error = null;
                    item.Status = RestoreStatus.PendingLink;
                }
            }
            Pending.Refresh(localGames, working.GameMappings);
            HashSet<string> restoredSavePaths = downloadedSaves.Select(s => s.Save.ManifestPath).ToHashSet(StringComparer.Ordinal);
            foreach (PendingRestore item in Pending.Items.Where(i => i.ProfileId == profile.Id && restoredSavePaths.Contains(i.Manifest.ManifestPath) && i.LocalGameId != null))
                if (!(matching?.Targets.TryGetValue(item.Manifest.Game.Id, out Guid? chosen) == true && chosen == null) &&
                    item.Manifest.Game.Id != item.LocalGameId && !working.GameMappings.Any(m => m.BackupGameId == item.Manifest.Game.Id))
                    working.GameMappings.Add(new GameMatchMapping { BackupGameId = item.Manifest.Game.Id, LocalGameId = item.LocalGameId!.Value });
            if (merged != null) _host.ValidateImport();
            await _settingsStore.SaveAsync(working, ct);
            Settings.GameMappings = working.GameMappings;
            Settings.SaveConfigs = working.SaveConfigs;
            Settings.SelectedGameIds = working.SelectedGameIds;
            Settings.CustomSavePaths = working.CustomSavePaths;
            Settings.LibraryMappings = working.LibraryMappings;
            await Pending.SaveAsync(ct);
            if (downloadedSaves.Count > 0) Report("存档已下载到待恢复区；关联游戏后请手动恢复。");
            if (merged != null)
            {
                Report((merged.Notices.Count == 0 ? "" : string.Join("；", merged.Notices) + "；") + "软件资料已准备，正在重启 PotatoVN 完成导入…");
                await _host.StageImportAsync(merged.ArchivePath, merged.Settings, ct);
            }
        }
        finally
        {
            // 下载失败或取消时，不在内存中留下尚未提交的临时对应关系。
            Pending.Refresh(_host.GetGames(), Settings.GameMappings);
            RemoveWork(work);
        }
    }

    public async Task ApplyLibraryMappingsAsync(CancellationToken ct = default)
    {
        string work = Work();
        try
        {
            HostExport current = await _host.ExportAsync(Path.Combine(work, "current"), ct);
            SoftwareMergeResult merged = await SoftwareBackupCodec.ApplyLibraryMappingsAsync(current, Settings,
                _host.GetGames(), Path.Combine(work, "merged"), ct);
            if (merged.Conflicts.Count > 0) throw new InvalidOperationException(string.Join("；", merged.Conflicts));
            await _settingsStore.SaveAsync(Settings);
            await _host.StageImportAsync(merged.ArchivePath, merged.Settings, ct);
        }
        finally { RemoveWork(work); }
    }

    private async Task PruneAsync(IBackupProvider provider, string work, CancellationToken ct)
    {
        List<SnapshotManifest> all = await ListSnapshotsInternalAsync(provider, work, ct);
        List<SnapshotManifest> expired = all.Where(s => s.DeviceId == Settings.DeviceId).OrderByDescending(s => s.CreatedUtc)
            .Skip(Math.Max(1, Settings.KeepSnapshots)).ToList();
        HashSet<string> protectedSaves = all.Except(expired).SelectMany(s => s.SaveManifestPaths).ToHashSet(StringComparer.Ordinal);
        foreach (SnapshotManifest snapshot in expired)
        {
            await provider.DeleteAsync(snapshot.ManifestPath, ct);
            if (snapshot.Data != null) await provider.DeleteAsync(snapshot.Data.Path, ct);
            if (snapshot.Media != null) await provider.DeleteAsync(snapshot.Media.Path, ct);
        }
        foreach (RemoteItem gameDirectory in await provider.ListAsync("v1/saves", ct))
        {
            if (!gameDirectory.IsDirectory) continue;
            List<SaveManifest> versions = await ListSaveVersionsAsync(provider, gameDirectory.Path + "/" + Settings.DeviceId.ToString("N"), work, ct);
            foreach (SaveManifest old in versions.OrderByDescending(v => v.CapturedUtc).Skip(Math.Max(1, Settings.KeepSaves)))
            {
                if (protectedSaves.Contains(old.ManifestPath)) continue;
                await provider.DeleteAsync(old.ManifestPath, ct);
                await provider.DeleteAsync(old.ArchivePath, ct);
            }
        }
    }

    private static async Task<FileRecord> UploadFileAsync(IBackupProvider provider, string local, string remote, CancellationToken ct)
    {
        FileRecord record = new() { Path = remote, Length = new FileInfo(local).Length, Sha256 = await HashAsync(local, ct) };
        await provider.UploadAsync(remote, local, ct);
        return record;
    }

    private static async Task DownloadCheckedAsync(IBackupProvider provider, FileRecord record, string local, CancellationToken ct)
    {
        ValidateFile(record);
        await provider.DownloadAsync(record.Path, local, ct);
        if (new FileInfo(local).Length != record.Length || !string.Equals(await HashAsync(local, ct), record.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("备份文件校验失败，未写入游戏存档或软件数据。");
    }

    private static async Task<string> HashAsync(string file, CancellationToken ct)
    {
        await using FileStream stream = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }

    private static async Task PutJsonAsync<T>(IBackupProvider provider, string path, T value, string work, CancellationToken ct)
    {
        string local = Path.Combine(work, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(local, JsonSerializer.Serialize(value, BackupJson.Options), ct);
        await provider.UploadAsync(path, local, ct);
    }

    private static async Task<T> GetJsonAsync<T>(IBackupProvider provider, string path, string work, CancellationToken ct)
    {
        ValidateRemotePath(path);
        string local = Path.Combine(work, Guid.NewGuid().ToString("N") + ".json");
        await provider.DownloadAsync(path, local, ct);
        if (new FileInfo(local).Length > 32 * 1024 * 1024) throw new InvalidDataException("备份清单过大。");
        return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(local, ct), BackupJson.Options)
            ?? throw new InvalidDataException("备份清单为空。");
    }

    private static async Task<List<RemoteItem>> WalkAsync(IBackupProvider provider, string root, CancellationToken ct)
    {
        List<RemoteItem> files = [];
        Queue<string> directories = new();
        HashSet<string> visited = new(StringComparer.Ordinal);
        directories.Enqueue(root);
        while (directories.TryDequeue(out string? directory))
        {
            if (!visited.Add(directory)) continue;
            foreach (RemoteItem item in await provider.ListAsync(directory, ct))
            {
                ValidateRemotePath(item.Path);
                if (!item.Path.StartsWith(directory.TrimEnd('/') + "/", StringComparison.Ordinal))
                    throw new InvalidDataException("云端目录返回了不属于该目录的文件。");
                if (item.IsDirectory) directories.Enqueue(item.Path);
                else files.Add(item);
            }
        }
        return files;
    }

    private static void ValidateRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("v1/", StringComparison.Ordinal)
            || path.Contains('\\') || path.Contains(':') || path.Split('/').Any(p => p is "" or "." or ".."))
            throw new InvalidDataException("备份包含非法云端路径。");
    }

    private static void ValidateFile(FileRecord file)
    {
        ValidateRemotePath(file.Path);
        if (file.Length < 0 || string.IsNullOrEmpty(file.Sha256) || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("备份文件的长度或校验值无效。");
    }

    private static void ValidateSnapshot(SnapshotManifest value, string path)
    {
        ValidateRemotePath(path);
        if (value.Games == null || value.SaveManifestPaths == null || value.Notices == null || string.IsNullOrWhiteSpace(value.Id) || value.DeviceId == Guid.Empty)
            throw new InvalidDataException("软件备份清单缺少必要字段。");
        string prefix = $"v1/snapshots/{value.DeviceId:N}/{value.Id}";
        if (value.FormatVersion != 1 || path != prefix + "/manifest.json" || (value.Content & ~BackupContent.All) != 0)
            throw new InvalidDataException("备份版本或清单路径不受支持。");
        value.ManifestPath = path;
        if (value.Data != null) { ValidateFile(value.Data); if (value.Data.Path != prefix + "/data.zip") throw new InvalidDataException("软件资料路径无效。"); }
        if (value.Media != null) { ValidateFile(value.Media); if (value.Media.Path != prefix + "/media.zip") throw new InvalidDataException("图片路径无效。"); }
        foreach (string save in value.SaveManifestPaths)
        {
            ValidateRemotePath(save);
            if (!save.StartsWith("v1/saves/", StringComparison.Ordinal) || !save.EndsWith("/manifest.json", StringComparison.Ordinal))
                throw new InvalidDataException("存档引用路径无效。");
        }
    }

    private static void ValidateSave(SaveManifest value, string path)
    {
        ValidateRemotePath(path);
        if (value.Game == null || value.Game.Id == Guid.Empty || value.Game.SourceIds == null || value.Locations == null || value.Files == null ||
            string.IsNullOrWhiteSpace(value.Id) || value.DeviceId == Guid.Empty || value.Locations.Any(l => l == null || !Enum.IsDefined(l.BaseKind) || l.RelativePath == null))
            throw new InvalidDataException("存档清单缺少必要字段或位置规则无效。");
        string prefix = $"v1/saves/{value.Game.Id:N}/{value.DeviceId:N}/{value.Id}";
        if (value.FormatVersion != 1 || path != prefix + "/manifest.json" || value.ArchivePath != prefix + "/save.zip")
            throw new InvalidDataException("存档版本或清单路径不受支持。");
        value.ManifestPath = path;
        ValidateFile(new FileRecord { Path = value.ArchivePath, Sha256 = value.Sha256, Length = value.Length });
    }

    private void RemoveWork(string path)
    {
        string root = Path.GetFullPath(Path.Combine(_root, "work")) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);
        if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full)) Directory.Delete(full, true);
    }
}
