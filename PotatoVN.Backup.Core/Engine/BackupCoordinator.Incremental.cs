using System.Text.Json;
using System.Text.Json.Nodes;

namespace PotatoVN.Backup.Core.Engine;

public sealed partial class BackupCoordinator
{
    // File-level v2 storage is the default; existing v1 archives remain readable.
    public async Task<SnapshotManifest> BackupIncrementalAsync(CancellationToken ct = default, bool automatic = false)
    {
        BackupContent content = Settings.GetBackupContent(automatic);
        if (content == BackupContent.None) throw new InvalidOperationException("请至少选择一种备份内容。");
        using IBackupProvider provider = _providers(ActiveProfile);
        string work = Work();
        try
        {
            ReportBackup(2, "正在检查云端备份…");
            await provider.ProbeAsync(ct);
            if (!await RemoteCommit.IsReadyAsync(provider, "v2/repository.json", work, ct))
                await PutIncrementalJsonAsync(provider, "v2/repository.json", new { formatVersion = 2 }, work, ct);
            else
            {
                JsonObject repository = await ReadVersionedJsonAsync<JsonObject>(provider, "v2/repository.json", work, ct);
                if ((repository["formatVersion"] ?? repository["FormatVersion"])?.GetValue<int>() != 2)
                    throw new InvalidDataException("增量仓库格式不受支持。");
            }
            List<SnapshotManifest> published = await ReadPublishedSnapshotsAsync(provider, work, ct);
            List<SaveManifest> saves = await ReadPublishedSavesAsync(provider, work, ct);
            HashSet<string> committed = published.SelectMany(SnapshotRecipes).Concat(saves.Where(save => save.Recipe != null).Select(save => save.Recipe!))
                .SelectMany(IncrementalArchiveStore.ObjectPaths).ToHashSet(StringComparer.Ordinal);
            var archives = new IncrementalArchiveStore(provider, work, committed);
            async Task<ArchiveRecipe> UploadAsync(string archive, int start, int end, string label)
            {
                void Update(int current, int total) => ReportBackup(start + (end - start) * current / total,
                    $"{label}：{current}/{total} 个文件；上传 {archives.UploadedFiles}，复用 {archives.ReusedFiles}，已上传 {archives.UploadedBytes:N0} 字节。");
                ReportBackup(start, label + "…");
                archives.UploadProgress += Update;
                try { return await archives.UploadAsync(archive, Settings.DeviceId, ct); }
                finally { archives.UploadProgress -= Update; }
            }
            int reusedSaveFiles = 0;
            IReadOnlyList<GameIdentity> all = _host.GetGames();
            List<GameIdentity> selected = all.Where(Settings.IncludesGame).ToList();
            if (content.HasFlag(BackupContent.Saves) && selected.Any(game => game.IsRunning))
                throw new InvalidOperationException("所选游戏仍在运行，请在游戏退出后备份。");
            var snapshot = new SnapshotManifest
            {
                FormatVersion = 2, Id = NewId(), DeviceId = Settings.DeviceId, DeviceName = Settings.DeviceName,
                CreatedUtc = DateTimeOffset.UtcNow, Content = content, Games = selected.Select(CanonicalIdentity).ToList()
            };
            snapshot.ManifestPath = $"v2/snapshots/{Settings.DeviceId:N}/{snapshot.Id}/manifest.json";
            if ((content & ~BackupContent.Saves) != BackupContent.None)
            {
                ReportBackup(10, "正在导出软件资料…");
                HostExport exported = await _host.ExportAsync(Path.Combine(work, "export"), ct);
                ReportBackup(25, "正在整理所选资料和图片…");
                SoftwarePackage package = await SoftwareBackupCodec.CreateAsync(exported, Settings, all, Path.Combine(work, "software"), ct, content);
                snapshot.Games = package.Games.ToList();
                if (package.DataPath != null) snapshot.DataRecipe = await UploadAsync(package.DataPath, 30, 40, "正在上传资料");
                if (package.MediaPath != null) snapshot.MediaRecipe = await UploadAsync(package.MediaPath, 40, 55, "正在上传图片");
            }
            int processedGames = 0;
            if (content.HasFlag(BackupContent.Saves))
                foreach (GameIdentity game in selected)
                {
                    ct.ThrowIfCancellationRequested();
                    int start = 55 + 33 * processedGames / selected.Count;
                    int end = 55 + 33 * ++processedGames / selected.Count;
                    ReportBackup(start, $"正在检查存档（{processedGames}/{selected.Count}）：{game.Name}");
                    Guid canonicalId = Settings.GameMappings.LastOrDefault(mapping => mapping.LocalGameId == game.Id)?.BackupGameId ?? game.Id;
                    SaveManifest? latest = await FindReadableSaveAsync(provider, archives,
                        saves.Where(save => save.Game.Id == canonicalId && save.DeviceId == Settings.DeviceId), ct);
                    latest ??= await FindReadableSaveAsync(provider, archives,
                        saves.Where(save => save.Game.Id == canonicalId && save.DeviceId != Settings.DeviceId), ct);
                    GameSaveConfig? config = Settings.SaveConfigs.FirstOrDefault(item => item.GameId == game.Id);
                    SaveManifest? created = null;
                    string local = Path.Combine(work, canonicalId.ToString("N") + ".zip");
                    if (config?.Locations.Count > 0)
                    {
                        List<SaveLocation> locations = config.Locations.Select(CloneLocation).ToList();
                        foreach (SaveLocation location in locations)
                            if (Settings.CustomSavePaths.TryGetValue(location.Id, out string? custom)) location.CustomPath = custom;
                        var identity = new GameIdentity { Id = canonicalId, Name = game.Name, SourceIds = game.SourceIds, LocalDirectory = game.LocalDirectory };
                        created = await SaveArchiveService.CreateAsync(identity, locations, local, Settings.DeviceId, ct);
                    }
                    if (created == null)
                    {
                        if (latest != null)
                        {
                            if (latest.FormatVersion == 1)
                            {
                                latest = await CopyLegacySaveAsync(provider, archives, latest, work, ct);
                                saves.Add(latest);
                            }
                            else reusedSaveFiles += latest.Files.Count;
                            snapshot.SaveManifestPaths.Add(latest.ManifestPath);
                            snapshot.Notices.Add($"{game.Name}：没有可完整读取的本机存档，沿用 {latest.CapturedUtc:O} 的完整版本。");
                        }
                        else snapshot.Notices.Add($"{game.Name}：尚无可备份的存档位置或文件。");
                    }
                    else if (latest?.FormatVersion == 2 && created.ContentHash == latest.ContentHash && SameLocations(created.Locations, latest.Locations))
                    {
                        snapshot.SaveManifestPaths.Add(latest.ManifestPath);
                        reusedSaveFiles += latest.Files.Count;
                    }
                    else
                    {
                        created.FormatVersion = 2;
                        created.Id = NewId();
                        created.ManifestPath = $"v2/saves/{canonicalId:N}/{Settings.DeviceId:N}/{created.Id}/manifest.json";
                        created.Recipe = await UploadAsync(local, start, end, $"正在上传存档：{game.Name}");
                        created.ArchivePath = "";
                        created.Sha256 = "";
                        created.Length = created.Files.Sum(file => file.Length);
                        ValidateVersionedSave(created, created.ManifestPath);
                        await PutIncrementalJsonAsync(provider, created.ManifestPath, created, work, ct);
                        saves.Add(created);
                        snapshot.SaveManifestPaths.Add(created.ManifestPath);
                    }
                }
            ReportBackup(90, "正在发布完整备份…");
            await PutIncrementalJsonAsync(provider, snapshot.ManifestPath, snapshot, work, ct);
            Settings.LastSuccessfulBackupUtc = DateTimeOffset.UtcNow;
            await _settingsStore.SaveAsync(Settings);
            ReportBackup(95, "备份已发布，正在清理过期历史…");
            // User approved automatic retention after planner and deletion-failure regressions passed.
            try { await PruneIncrementalAsync(provider, work, ct); }
            catch (Exception error) when (error is IOException or HttpRequestException or InvalidDataException or OperationCanceledException or JsonException)
            { snapshot.Notices.Add("备份已完成，历史清理未完成：" + error.Message); }
            ReportBackup(100, $"备份完成。本次上传 {archives.UploadedFiles} 个新增或变化文件，复用 {archives.ReusedFiles + reusedSaveFiles} 个文件；上传数据 {archives.UploadedBytes:N0} 字节。" + string.Join("；", snapshot.Notices));
            return snapshot;
        }
        finally { RemoveWork(work); }
    }

    private async Task<SaveManifest> CopyLegacySaveAsync(IBackupProvider provider, IncrementalArchiveStore archives,
        SaveManifest source, string work, CancellationToken ct)
    {
        Report($"正在建立存档增量基础：{source.Game.Name}");
        string local = Path.Combine(work, Guid.NewGuid().ToString("N") + ".zip");
        await DownloadCheckedAsync(provider, new FileRecord { Path = source.ArchivePath, Length = source.Length, Sha256 = source.Sha256 }, local, ct);
        SaveManifest copy = JsonSerializer.Deserialize<SaveManifest>(JsonSerializer.SerializeToUtf8Bytes(source, BackupJson.Options), BackupJson.Options)!;
        copy.FormatVersion = 2;
        copy.Id = NewId();
        copy.SourceDeviceId = source.SourceDeviceId ?? source.DeviceId;
        copy.DeviceId = Settings.DeviceId;
        copy.ManifestPath = $"v2/saves/{copy.Game.Id:N}/{copy.DeviceId:N}/{copy.Id}/manifest.json";
        copy.Recipe = await archives.UploadAsync(local, Settings.DeviceId, ct);
        copy.ArchivePath = "";
        copy.Sha256 = "";
        copy.Length = copy.Files.Sum(file => file.Length);
        ValidateVersionedSave(copy, copy.ManifestPath);
        await PutIncrementalJsonAsync(provider, copy.ManifestPath, copy, work, ct);
        return copy;
    }

    // Explicit and post-backup cleanup share the same tested retention plan.
    public async Task CleanupIncrementalHistoryAsync(CancellationToken ct = default)
    {
        using IBackupProvider provider = _providers(ActiveProfile);
        string work = Work();
        try { await PruneIncrementalAsync(provider, work, ct); }
        finally { RemoveWork(work); }
    }

    private async Task PruneIncrementalAsync(IBackupProvider provider, string work, CancellationToken ct)
    {
        List<SnapshotManifest> snapshots = await ReadPublishedSnapshotsAsync(provider, work, ct);
        List<SaveManifest> saves = await ReadPublishedSavesAsync(provider, work, ct);
        IncrementalPrunePlan plan = IncrementalPrunePlan.Create(snapshots, saves, Settings.DeviceId,
            Math.Max(1, Settings.KeepSnapshots), Math.Max(1, Settings.KeepSaves));
        foreach (string path in plan.ManifestPaths)
        {
            if (path.StartsWith("v2/", StringComparison.Ordinal) && await provider.ExistsAsync(RemoteCommit.MarkerPath(path), ct))
                await provider.DeleteAsync(RemoteCommit.MarkerPath(path), ct);
            await provider.DeleteAsync(path, ct);
        }
        foreach (string path in plan.LegacyArchivePaths) await provider.DeleteAsync(path, ct);
        foreach (string path in plan.ObjectPaths)
        {
            if (await provider.ExistsAsync(RemoteCommit.MarkerPath(path), ct)) await provider.DeleteAsync(RemoteCommit.MarkerPath(path), ct);
            await provider.DeleteAsync(path, ct);
        }
    }

    private static IEnumerable<ArchiveRecipe> SnapshotRecipes(SnapshotManifest snapshot)
    {
        if (snapshot.DataRecipe != null) yield return snapshot.DataRecipe;
        if (snapshot.MediaRecipe != null) yield return snapshot.MediaRecipe;
    }

    private static async Task<SaveManifest?> FindReadableSaveAsync(IBackupProvider provider, IncrementalArchiveStore archives,
        IEnumerable<SaveManifest> saves, CancellationToken ct)
    {
        foreach (SaveManifest save in saves.OrderByDescending(save => save.CapturedUtc))
            if (save.FormatVersion == 1 ? await provider.ExistsAsync(save.ArchivePath, ct) : await archives.IsAvailableAsync(save.Recipe!, ct)) return save;
        return null;
    }

    private static async Task<List<SnapshotManifest>> ReadPublishedSnapshotsAsync(IBackupProvider provider, string work, CancellationToken ct)
    {
        List<SnapshotManifest> result = [];
        foreach (string root in new[] { "v1/snapshots", "v2/snapshots" })
            foreach (RemoteItem file in await WalkVersionedAsync(provider, root, ct))
            {
                if (!file.Path.EndsWith("/manifest.json", StringComparison.Ordinal)) continue;
                if (file.Path.StartsWith("v2/", StringComparison.Ordinal) && !await RemoteCommit.IsReadyAsync(provider, file.Path, work, ct)) continue;
                SnapshotManifest snapshot = await ReadVersionedJsonAsync<SnapshotManifest>(provider, file.Path, work, ct);
                ValidateVersionedSnapshot(snapshot, file.Path);
                result.Add(snapshot);
            }
        return result;
    }

    private static async Task<List<SaveManifest>> ReadPublishedSavesAsync(IBackupProvider provider, string work, CancellationToken ct)
    {
        List<SaveManifest> result = [];
        foreach (string root in new[] { "v1/saves", "v2/saves" })
            foreach (RemoteItem file in await WalkVersionedAsync(provider, root, ct))
            {
                if (!file.Path.EndsWith("/manifest.json", StringComparison.Ordinal)) continue;
                if (file.Path.StartsWith("v2/", StringComparison.Ordinal) && !await RemoteCommit.IsReadyAsync(provider, file.Path, work, ct)) continue;
                SaveManifest save = await ReadVersionedJsonAsync<SaveManifest>(provider, file.Path, work, ct);
                ValidateVersionedSave(save, file.Path);
                result.Add(save);
            }
        return result;
    }

    private static async Task PutIncrementalJsonAsync<T>(IBackupProvider provider, string path, T value, string work, CancellationToken ct)
    {
        string local = Path.Combine(work, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(local, JsonSerializer.Serialize(value, BackupJson.Options), ct);
        await RemoteCommit.UploadAsync(provider, path, local, work, ct);
    }

    private static async Task<T> ReadVersionedJsonAsync<T>(IBackupProvider provider, string path, string work, CancellationToken ct)
    {
        ValidateVersionedPath(path);
        string local = Path.Combine(work, Guid.NewGuid().ToString("N") + ".json");
        if (path.StartsWith("v2/", StringComparison.Ordinal))
        {
            if (!await RemoteCommit.DownloadIfCommittedAsync(provider, path, local, work, ct))
                throw new InvalidDataException("此增量清单尚未完整提交，请重新读取历史备份。");
        }
        else await provider.DownloadAsync(path, local, ct);
        if (new FileInfo(local).Length > 32 * 1024 * 1024) throw new InvalidDataException("备份清单过大。");
        return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(local, ct), BackupJson.Options)
            ?? throw new InvalidDataException("备份清单为空。");
    }

    private static async Task<List<RemoteItem>> WalkVersionedAsync(IBackupProvider provider, string root, CancellationToken ct)
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
                ValidateVersionedPath(item.Path);
                if (!item.Path.StartsWith(directory.TrimEnd('/') + "/", StringComparison.Ordinal))
                    throw new InvalidDataException("云端目录返回了不属于该目录的文件。");
                if (item.IsDirectory) directories.Enqueue(item.Path);
                else files.Add(item);
            }
        }
        return files;
    }

    private static void ValidateVersionedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || (!path.StartsWith("v1/", StringComparison.Ordinal) && !path.StartsWith("v2/", StringComparison.Ordinal)) ||
            path.Contains('\\') || path.Contains(':') || path.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("备份包含非法云端路径。");
    }

    private static void ValidateVersionedSnapshot(SnapshotManifest value, string path)
    {
        if (value.FormatVersion == 1)
        {
            ValidateSnapshot(value, path);
            if (value.DataRecipe != null || value.MediaRecipe != null) throw new InvalidDataException("旧版快照不能夹带增量清单。");
            return;
        }
        ValidateVersionedPath(path);
        if (value.FormatVersion != 2 || value.Games == null || value.SaveManifestPaths == null || value.Notices == null ||
            string.IsNullOrWhiteSpace(value.Id) || value.DeviceId == Guid.Empty || (value.Content & ~BackupContent.All) != 0 ||
            path != $"v2/snapshots/{value.DeviceId:N}/{value.Id}/manifest.json" || value.Data != null || value.Media != null)
            throw new InvalidDataException("增量快照版本或清单字段无效。");
        value.ManifestPath = path;
        foreach (ArchiveRecipe recipe in SnapshotRecipes(value)) IncrementalArchiveStore.Validate(recipe);
        if ((value.Content & ~BackupContent.Saves) != BackupContent.None && value.DataRecipe == null)
            throw new InvalidDataException("软件备份缺少完整文件清单。");
        foreach (string save in value.SaveManifestPaths)
        {
            ValidateVersionedPath(save);
            if ((!save.StartsWith("v1/saves/", StringComparison.Ordinal) && !save.StartsWith("v2/saves/", StringComparison.Ordinal)) ||
                !save.EndsWith("/manifest.json", StringComparison.Ordinal)) throw new InvalidDataException("存档引用路径无效。");
        }
    }

    private static void ValidateVersionedSave(SaveManifest value, string path)
    {
        if (value.FormatVersion == 1)
        {
            ValidateSave(value, path);
            if (value.Recipe != null) throw new InvalidDataException("旧版存档不能夹带增量清单。");
            return;
        }
        ValidateVersionedPath(path);
        if (value.FormatVersion != 2 || value.Game == null || value.Game.Id == Guid.Empty || value.Game.SourceIds == null ||
            value.Locations == null || value.Files == null || value.Locations.Count == 0 || value.Files.Count == 0 ||
            string.IsNullOrWhiteSpace(value.Id) || value.DeviceId == Guid.Empty || value.Recipe == null ||
            !string.IsNullOrEmpty(value.ArchivePath) || !string.IsNullOrEmpty(value.Sha256) ||
            path != $"v2/saves/{value.Game.Id:N}/{value.DeviceId:N}/{value.Id}/manifest.json")
            throw new InvalidDataException("增量存档版本或清单字段无效。");
        value.ManifestPath = path;
        IncrementalArchiveStore.Validate(value.Recipe);
        if (value.Locations.Any(location => location == null || string.IsNullOrEmpty(location.Id) ||
                location.Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-') || !Enum.IsDefined(location.BaseKind) || location.RelativePath == null) ||
            value.Locations.Select(location => location.Id).Distinct(StringComparer.Ordinal).Count() != value.Locations.Count ||
            value.Files.Any(file => file == null) || value.Recipe.Files.Count != value.Files.Count ||
            !string.Equals(BackupFiles.TreeHash(value.Files), BackupFiles.TreeHash(value.Recipe.Files), StringComparison.Ordinal) ||
            !string.Equals(value.ContentHash, BackupFiles.TreeHash(value.Files), StringComparison.Ordinal) ||
            value.Files.Any(file => !value.Locations.Any(location => file.Path.StartsWith($"roots/{location.Id}/", StringComparison.Ordinal))))
            throw new InvalidDataException("存档文件与增量清单不一致。");
    }
    public async Task<IReadOnlyList<SnapshotManifest>> ListIncrementalSnapshotsAsync(CancellationToken ct = default)
    {
        using IBackupProvider provider = _providers(ActiveProfile);
        string work = Work();
        try { return (await ReadPublishedSnapshotsAsync(provider, work, ct)).OrderByDescending(snapshot => snapshot.CreatedUtc).ToList(); }
        finally { RemoveWork(work); }
    }

    public async Task StageIncrementalRestoreAsync(SnapshotManifest snapshot, BackupContent content, CancellationToken ct = default,
        RestoreMatchingDecisions? matching = null)
    {
        ValidateVersionedSnapshot(snapshot, snapshot.ManifestPath);
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
            var archives = new IncrementalArchiveStore(provider, work);
            string? data = null, media = null;
            if ((content & ~BackupContent.Saves) != BackupContent.None)
            {
                data = Path.Combine(work, "data.zip");
                if (snapshot.FormatVersion == 1)
                {
                    if (snapshot.Data == null) throw new InvalidDataException("这个版本缺少软件资料包。");
                    await DownloadCheckedAsync(provider, snapshot.Data, data, ct);
                    if (snapshot.Media != null)
                    {
                        media = Path.Combine(work, "media.zip");
                        await DownloadCheckedAsync(provider, snapshot.Media, media, ct);
                    }
                }
                else
                {
                    await archives.RestoreAsync(snapshot.DataRecipe!, data, ct);
                    if (snapshot.MediaRecipe != null)
                    {
                        media = Path.Combine(work, "media.zip");
                        await archives.RestoreAsync(snapshot.MediaRecipe, media, ct);
                    }
                }
            }
            var downloadedSaves = new List<(SaveManifest Save, string Archive)>();
            if (content.HasFlag(BackupContent.Saves))
            {
                foreach (string manifestPath in snapshot.SaveManifestPaths)
                {
                    SaveManifest save = await ReadVersionedJsonAsync<SaveManifest>(provider, manifestPath, work, ct);
                    ValidateVersionedSave(save, manifestPath);
                    if (matching != null && !matching.Targets.ContainsKey(save.Game.Id))
                        throw new InvalidDataException("存档清单中的游戏不属于当前快照，请检查备份完整性。");
                    string archive;
                    if (save.FormatVersion == 1)
                    {
                        archive = Path.Combine(work, save.Sha256 + ".zip");
                        if (!File.Exists(archive))
                            await DownloadCheckedAsync(provider, new FileRecord { Path = save.ArchivePath, Length = save.Length, Sha256 = save.Sha256 }, archive, ct);
                    }
                    else
                    {
                        archive = Path.Combine(work, Guid.NewGuid().ToString("N") + ".zip");
                        await archives.RestoreAsync(save.Recipe!, archive, ct);
                        save = await IncrementalSaveAdapter.ToLocalAsync(save, archive, ct);
                    }
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

}
