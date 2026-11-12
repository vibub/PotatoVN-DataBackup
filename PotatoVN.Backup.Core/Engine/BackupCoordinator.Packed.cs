using System.Text.Json;

namespace PotatoVN.Backup.Core.Engine;

public sealed partial class BackupCoordinator
{
    public async Task<SnapshotManifest> BackupPackedAsync(CancellationToken ct = default, bool automatic = false)
    {
        BackupContent content = Settings.GetBackupContent(automatic);
        if (content == BackupContent.None) throw new InvalidOperationException("请至少选择一种备份内容。");
        ProviderProfile profile = ActiveProfile;
        string work = Work();
        try
        {
            using FileStream writer = AcquirePackedWriter(profile);
            using IBackupProvider provider = _providers(profile);
            ReportBackup(2, "正在读取备份索引…");
            PackedCatalog catalog = await PackedCatalog.LoadAsync(provider, Settings.DeviceId, work, ct);
            PackedState state = catalog.State;
            var store = new PackedArchiveStore(provider, catalog, work);
            await store.CheckObjectsAsync(ct);
            IReadOnlyList<GameIdentity> all = _host.GetGames();
            List<GameIdentity> selected = all.Where(Settings.IncludesGame).ToList();
            if (content.HasFlag(BackupContent.Saves) && selected.Any(game => game.IsRunning)) throw new InvalidOperationException("所选游戏仍在运行，请退出后备份。");
            var snapshot = new SnapshotManifest
            {
                FormatVersion = 3, Id = NewId(), DeviceId = Settings.DeviceId, DeviceName = Settings.DeviceName,
                CreatedUtc = DateTimeOffset.UtcNow, Content = content, Games = selected.Select(CanonicalIdentity).ToList()
            };
            snapshot.ManifestPath = PackedFormat.SnapshotPath(Settings.DeviceId, snapshot.Id);
            if (catalog.Notice != null) snapshot.Notices.Add(catalog.Notice);
            FileHashCache? images = null;
            if ((content & ~BackupContent.Saves) != 0)
            {
                ReportBackup(10, "正在采集所选资料与图片摘要…");
                images = new FileHashCache(_root);
                await images.LoadAsync(ct);
                HostExport export = await _host.ExportForBackupAsync(Path.Combine(work, "capture"), ct);
                SoftwarePackage package = await SoftwareBackupCodec.CreateAsync(export, Settings, all, Path.Combine(work, "software"), ct, content, sourcesOnly: true, imageCache: images);
                snapshot.Games = package.Games;
                if (package.DataSources.Count > 0) snapshot.DataRecipe = store.Register(package.DataSources);
                if (package.MediaSources.Count > 0) snapshot.MediaRecipe = store.Register(package.MediaSources, media: true);
            }
            List<PackedCatalog>? foreign = null;
            List<SaveManifest>? legacy = null;
            int processed = 0;
            if (content.HasFlag(BackupContent.Saves)) foreach (GameIdentity game in selected)
            {
                ct.ThrowIfCancellationRequested();
                ReportBackup(30 + 25 * processed++ / Math.Max(1, selected.Count), $"正在校验存档（{processed}/{selected.Count}）：{game.Name}");
                GameIdentity identity = CanonicalIdentity(game);
                SaveManifest? latest = state.Saves.Where(save => save.Game.Id == identity.Id).OrderByDescending(save => save.CapturedUtc).FirstOrDefault();
                GameSaveConfig? config = Settings.SaveConfigs.FirstOrDefault(config => config.GameId == game.Id);
                PreparedSave? prepared = null;
                if (config?.Locations.Count > 0)
                {
                    List<SaveLocation> locations = config.Locations.Select(CloneLocation).ToList();
                    foreach (SaveLocation location in locations)
                        if (Settings.CustomSavePaths.TryGetValue(location.Id, out string? custom)) location.CustomPath = custom;
                    prepared = await SaveArchiveService.PrepareAsync(new GameIdentity
                    { Id = identity.Id, Name = identity.Name, SourceIds = identity.SourceIds, LocalDirectory = game.LocalDirectory }, locations, Settings.DeviceId, ct);
                }
                if (prepared != null)
                {
                    if (latest != null && latest.ContentHash == prepared.Manifest.ContentHash && SameLocations(latest.Locations, prepared.Manifest.Locations) && store.IsAvailable(latest.Recipe!))
                        snapshot.SaveManifestPaths.Add(latest.ManifestPath);
                    else
                    {
                        SaveManifest save = prepared.Manifest;
                        save.FormatVersion = 3; save.Id = NewId(); save.ManifestPath = PackedFormat.SavePath(Settings.DeviceId, save.Id);
                        save.Recipe = store.Register(prepared.Files);
                        state.Saves.Add(save);
                        snapshot.SaveManifestPaths.Add(save.ManifestPath);
                    }
                }
                else
                {
                    if (latest == null)
                    {
                        // 只在没有本机数据/本设备版本时按需读取其他设备与旧格式，日常备份不遍历旧仓库。
                        foreign ??= await ReadPackedCatalogsAsync(provider, work, ct, Settings.DeviceId);
                        var source = foreign.SelectMany(owner => owner.State.Saves.Select(save => (Owner: owner, Save: save)))
                            .Where(item => item.Save.Game.Id == identity.Id).OrderByDescending(item => item.Save.CapturedUtc).FirstOrDefault();
                        if (source.Save != null) latest = await CopySaveToPackedAsync(provider, store, source.Save, source.Owner, work, ct);
                        else
                        {
                            legacy ??= await ReadPublishedSavesAsync(provider, work, ct);
                            SaveManifest? old = legacy.Where(save => save.Game.Id == identity.Id).OrderByDescending(save => save.CapturedUtc).FirstOrDefault();
                            if (old != null) latest = await CopySaveToPackedAsync(provider, store, old, null, work, ct);
                        }
                        if (latest != null) state.Saves.Add(latest);
                    }
                    if (latest != null)
                    {
                        snapshot.SaveManifestPaths.Add(latest.ManifestPath);
                        snapshot.Notices.Add($"{game.Name}：沿用 {latest.CapturedUtc:yyyy-MM-dd HH:mm} 的完整存档。");
                    }
                    else snapshot.Notices.Add($"{game.Name}：尚无可备份的存档位置或文件。");
                }
            }
            SnapshotManifest? previous = state.Snapshots.OrderByDescending(item => item.CreatedUtc).FirstOrDefault();
            bool unchanged = previous != null && PackedSnapshotSignature(previous) == PackedSnapshotSignature(snapshot);
            if (!unchanged) state.Snapshots.Add(snapshot);
            bool retainedChanged = ApplyPackedRetention(state);
            store.UploadProgress += (current, total) => ReportBackup(55 + 30 * current / Math.Max(1, total),
                $"正在上传变化内容：{current}/{total} 个文件，{store.UploadedObjects} 个包，{store.UploadedBytes:N0} 字节。");
            ReportBackup(55, "正在准备变化内容包…");
            await store.UploadPendingAsync(ct);
            store.TrimUnreferenced();
            if (!unchanged || store.UploadedObjects > 0 || retainedChanged || store.HasMaintenanceChanges || catalog.Notice != null)
            {
                ReportBackup(90, "正在发布并校验备份索引…");
                await catalog.PublishAsync(ct);
            }
            ReportBackup(95, "正在清理已过期内容…");
            var notices = snapshot.Notices.ToList();
            try { await store.CleanupAsync(ct); }
            catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException)
            { notices.Add("备份已完成，历史清理未完成：" + error.Message); }
            if (images != null)
            {
                try { await images.SaveAsync(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { notices.Add("图片摘要缓存未能保存：" + error.Message); }
            }
            Settings.LastSuccessfulBackupUtc = DateTimeOffset.UtcNow;
            await _settingsStore.SaveAsync(Settings);
            string result = unchanged ? "内容未变化，已复用现有版本。" : "备份完成。";
            if (unchanged && store.UploadedObjects > 0) result = "已修复缺失的云端内容，保留原版本。";
            ReportBackup(100, result + $"上传 {store.UploadedObjects} 个包、{store.UploadedFiles} 个变化文件，{store.UploadedBytes:N0} 字节。" +
                (images == null ? "" : $"图片摘要复用 {images.ReusedHashes}，重新计算 {images.ComputedHashes}。") + string.Join("；", notices));
            return unchanged ? previous! : snapshot;
        }
        finally { RemoveWork(work); }
    }
    private FileStream AcquirePackedWriter(ProviderProfile profile)
    {
        string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{Settings.DeviceId:N}|{profile.Kind}|{profile.Endpoint}|{profile.RemotePath}|{profile.RepositoryFolder}|{profile.ClientId}")));
        string directory = Path.Combine(_root, "locks");
        Directory.CreateDirectory(directory);
        try { return new FileStream(Path.Combine(directory, key + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new IOException("本设备已有同渠道备份任务正在运行，请稍后重试。", error); }
    }
    private bool ApplyPackedRetention(PackedState state)
    {
        int snapshots = state.Snapshots.Count, saves = state.Saves.Count;
        state.Snapshots = state.Snapshots.OrderByDescending(item => item.CreatedUtc).ThenByDescending(item => item.ManifestPath, StringComparer.Ordinal).Take(Math.Max(1, Settings.KeepSnapshots)).ToList();
        HashSet<string> protectedSaves = state.Snapshots.SelectMany(item => item.SaveManifestPaths).ToHashSet(StringComparer.Ordinal);
        state.Saves = state.Saves.GroupBy(save => save.Game.Id).SelectMany(group => group.OrderByDescending(save => save.CapturedUtc)
            .ThenByDescending(save => save.ManifestPath, StringComparer.Ordinal).Take(Math.Max(1, Settings.KeepSaves)))
            .Concat(state.Saves.Where(save => protectedSaves.Contains(save.ManifestPath))).DistinctBy(save => save.ManifestPath).ToList();
        return snapshots != state.Snapshots.Count || saves != state.Saves.Count;
    }
    private static string PackedSnapshotSignature(SnapshotManifest snapshot) => JsonSerializer.Serialize(new
    {
        snapshot.Content, snapshot.DeviceName,
        Games = snapshot.Games.OrderBy(game => game.Id).Select(game => new { game.Id, game.Name, SourceIds = game.SourceIds.OrderBy(pair => pair.Key).ToArray() }),
        Data = snapshot.DataRecipe == null ? null : BackupFiles.TreeHash(snapshot.DataRecipe.Files),
        Media = snapshot.MediaRecipe == null ? null : BackupFiles.TreeHash(snapshot.MediaRecipe.Files),
        Saves = snapshot.SaveManifestPaths.Order(StringComparer.Ordinal).ToArray()
    }, PackedFormat.Json);
    private static async Task<List<PackedCatalog>> ReadPackedCatalogsAsync(IBackupProvider provider, string work, CancellationToken ct, Guid? exclude = null)
    {
        var result = new List<PackedCatalog>();
        const string prefix = "v3/devices/";
        foreach (RemoteItem item in await provider.ListAsync("v3/devices", ct))
        {
            if (!item.Path.StartsWith(prefix, StringComparison.Ordinal) || item.Path[prefix.Length..].Contains('/')) throw new InvalidDataException("v3设备目录越界。");
            if (item.IsDirectory && Guid.TryParseExact(item.Path[prefix.Length..], "N", out Guid device) && device != exclude)
                result.Add(await PackedCatalog.LoadAsync(provider, device, work, ct));
        }
        return result;
    }
    public async Task<IReadOnlyList<SnapshotManifest>> ListPackedSnapshotsAsync(CancellationToken ct = default, bool includeLegacy = true)
    {
        using IBackupProvider provider = _providers(ActiveProfile);
        string work = Work();
        try
        {
            var snapshots = (await ReadPackedCatalogsAsync(provider, work, ct)).SelectMany(owner => owner.State.Snapshots).ToList();
            if (includeLegacy) snapshots.AddRange(await ReadPublishedSnapshotsAsync(provider, work, ct));
            return snapshots.OrderByDescending(snapshot => snapshot.CreatedUtc).ToList();
        }
        finally { RemoveWork(work); }
    }
    private async Task<SaveManifest> CopySaveToPackedAsync(IBackupProvider provider, PackedArchiveStore target, SaveManifest source,
        PackedCatalog? owner, string work, CancellationToken ct)
    {
        string directory = Path.Combine(work, "copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string archive = Path.Combine(directory, "save.zip");
        if (source.FormatVersion == 3) await new PackedArchiveStore(provider, owner!, directory).RestoreAsync(source.Recipe!, archive, ct);
        else if (source.FormatVersion == 2) await new IncrementalArchiveStore(provider, directory).RestoreAsync(source.Recipe!, archive, ct);
        else await DownloadCheckedAsync(provider, new FileRecord { Path = source.ArchivePath, Sha256 = source.Sha256, Length = source.Length }, archive, ct);
        string extracted = Path.Combine(directory, "files");
        await BackupFiles.ExtractAsync(archive, extracted, ct);
        var files = new List<PreparedFile>();
        foreach (FileRecord record in source.Files)
        {
            PreparedFile file = await PreparedFile.ReadAsync(record.Path, BackupFiles.Within(extracted, record.Path), ct);
            if (file.Record.Length != record.Length || !string.Equals(file.Record.Sha256, record.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("引用存档校验失败。");
            files.Add(file);
        }
        SaveManifest save = PackedFormat.Clone(source);
        save.FormatVersion = 3; save.Id = NewId(); save.SourceDeviceId = source.SourceDeviceId ?? source.DeviceId; save.DeviceId = Settings.DeviceId;
        save.ManifestPath = PackedFormat.SavePath(Settings.DeviceId, save.Id);
        save.Recipe = target.Register(files);
        save.Files = save.Recipe.Files; save.ContentHash = BackupFiles.TreeHash(save.Files); save.Length = save.Files.Sum(file => file.Length);
        save.ArchivePath = ""; save.Sha256 = "";
        return save;
    }
}
