using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PotatoVN.Backup.Core;
using PotatoVN.Backup.Core.Engine;

internal static class CoordinatorBoundaryChecks
{
    public static async Task ManifestLastAndDedup(string root)
    {
        var context = Create(root);
        var first = await context.Coordinator.BackupAsync();
        var save = first.SaveManifestPaths.Single();
        Check(context.Remote.Uploads.Last() == first.ManifestPath, "软件快照清单必须最后上传。");
        var second = await context.Coordinator.BackupAsync();
        Check(second.SaveManifestPaths.Single() == save, "未变化存档应复用已有版本。");
        Check(context.Remote.Uploads.Count(p => p.StartsWith("v1/saves/") && p.EndsWith("/save.zip")) == 1,
            "相同文件不应重复上传 save.zip。");
    }

    public static async Task InterruptedUploadNeverCommits(string root)
    {
        var context = Create(root);
        context.Remote.FailUpload = p => p.EndsWith("/save.zip");
        await Throws<IOException>(() => context.Coordinator.BackupAsync());
        Check(!context.Remote.Files.Keys.Any(p => p.StartsWith("v1/snapshots/") && p.EndsWith("/manifest.json")),
            "存档上传失败不能发布软件快照清单。");
        Check(!context.Remote.Files.Keys.Any(p => p.StartsWith("v1/saves/") && p.EndsWith("/manifest.json")),
            "存档上传失败不能发布存档清单。");
    }

    public static async Task NewDeviceRetainsOldSaveReference(string root)
    {
        var original = Create(root);
        var snapshot = await original.Coordinator.BackupAsync();
        var fresh = Create(root, original.Remote, original.Game.Id, false);
        await fresh.Coordinator.StageRestoreAsync(snapshot, BackupContent.Saves);
        Check(fresh.Coordinator.Pending.Items.Count == 1 && fresh.Coordinator.Pending.Items[0].Status == RestoreStatus.PendingLink,
            "新设备未关联游戏时必须暂存旧设备存档。");
        var next = await fresh.Coordinator.BackupAsync();
        Check(next.SaveManifestPaths.Count == 1 && next.SaveManifestPaths[0] == snapshot.SaveManifestPaths[0],
            "新设备没有本机存档时，后续快照应保留旧设备已有云端版本引用。");
        var manifest = original.Remote.Read<SaveManifest>(next.SaveManifestPaths.Single());
        Check(manifest.DeviceId == original.Settings.DeviceId, "复用旧存档应保留原设备标识。");
    }

    public static async Task LocationChangeCreatesNewRecipe(string root)
    {
        var context = Create(root);
        var first = await context.Coordinator.BackupAsync();
        var secondDirectory = Path.Combine(context.Game.LocalDirectory!, "saveB");
        Directory.CreateDirectory(secondDirectory);
        File.Copy(Path.Combine(context.Game.LocalDirectory!, "saveA", "slot.dat"), Path.Combine(secondDirectory, "slot.dat"));
        context.Settings.SaveConfigs[0].Locations[0].RelativePath = "saveB";
        var second = await context.Coordinator.BackupAsync();
        Check(second.SaveManifestPaths.Single() != first.SaveManifestPaths.Single(),
            "即使文件内容相同，路径规则变化也必须产生新的存档清单。");
        Check(context.Remote.Read<SaveManifest>(second.SaveManifestPaths.Single()).Locations[0].RelativePath == "saveB",
            "新清单必须携带新的相对位置。");
    }

    public static async Task MissingArchiveIsNotReused(string root)
    {
        var context = Create(root);
        var first = await context.Coordinator.BackupAsync();
        var previous = context.Remote.Read<SaveManifest>(first.SaveManifestPaths.Single());
        context.Remote.Files.Remove(previous.ArchivePath);
        var second = await context.Coordinator.BackupAsync();
        var current = context.Remote.Read<SaveManifest>(second.SaveManifestPaths.Single());
        Check(context.Remote.Files.TryGetValue(current.ArchivePath, out var archive),
            "不能发布仍引用缺失 save.zip 的成功快照。");
        Check(Convert.ToHexString(SHA256.HashData(archive!)).Equals(current.Sha256, StringComparison.OrdinalIgnoreCase),
            "补传后的存档必须与引用清单校验值一致。");
    }

    public static async Task OtherDeviceReferencesProtectHistory(string root)
    {
        var context = Create(root);
        context.Settings.KeepSnapshots = 1;
        context.Settings.KeepSaves = 1;
        var first = await context.Coordinator.BackupAsync();
        var oldSave = context.Remote.Read<SaveManifest>(first.SaveManifestPaths.Single());
        var otherDevice = Guid.NewGuid();
        var foreign = new SnapshotManifest
        {
            Id = NewId(), DeviceId = otherDevice, CreatedUtc = DateTimeOffset.UtcNow,
            Content = BackupContent.Saves, SaveManifestPaths = [oldSave.ManifestPath], Games = [context.Game]
        };
        foreign.ManifestPath = $"v1/snapshots/{otherDevice:N}/{foreign.Id}/manifest.json";
        context.Remote.Put(foreign.ManifestPath, foreign);
        await File.WriteAllTextAsync(Path.Combine(context.Game.LocalDirectory!, "saveA", "slot.dat"), "new-progress");
        await context.Coordinator.BackupAsync();
        Check(!context.Remote.Files.ContainsKey(first.ManifestPath), "当前设备过期软件历史应被清理。");
        Check(context.Remote.Files.ContainsKey(foreign.ManifestPath), "其他设备的软件快照不能被当前设备清理。");
        Check(context.Remote.Files.ContainsKey(oldSave.ManifestPath) && context.Remote.Files.ContainsKey(oldSave.ArchivePath),
            "其他保留快照引用的旧存档不能因数量上限被删除。");
    }

    public static async Task MalformedRemotePathsRejected(string root)
    {
        var context = Create(root);
        context.Remote.ListOverride = p => p == "v1/snapshots"
            ? [new RemoteItem("v1/snapshots/../outside/manifest.json", false)] : null;
        await Throws<InvalidDataException>(() => context.Coordinator.ListSnapshotsAsync());
        Check(context.Remote.Downloads.Count == 0, "非法路径必须在请求下载前被拒绝。");
        context.Remote.ListOverride = null;
        var snapshot = new SnapshotManifest { Id = NewId(), DeviceId = Guid.NewGuid(), Content = BackupContent.Saves };
        snapshot.ManifestPath = $"v1/snapshots/{snapshot.DeviceId:N}/{snapshot.Id}/manifest.json";
        var document = JsonSerializer.SerializeToNode(snapshot, BackupJson.Options)!.AsObject();
        document["SaveManifestPaths"] = null;
        context.Remote.Files[snapshot.ManifestPath] = JsonSerializer.SerializeToUtf8Bytes(document, BackupJson.Options);
        await Throws<InvalidDataException>(() => context.Coordinator.ListSnapshotsAsync());
    }

    private static Context Create(string root, RemoteState? remote = null, Guid? gameId = null, bool installed = true)
    {
        var local = Path.Combine(root, "coordinator", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(local);
        string? gameRoot = null;
        if (installed)
        {
            gameRoot = Path.Combine(local, "game");
            Directory.CreateDirectory(Path.Combine(gameRoot, "saveA"));
            File.WriteAllText(Path.Combine(gameRoot, "saveA", "slot.dat"), "initial-progress");
        }
        var game = new GameIdentity { Id = gameId ?? Guid.NewGuid(), Name = "离线游戏", SourceIds = new() { ["vndb"] = "v123" }, LocalDirectory = gameRoot };
        var settings = new BackupSettings { Content = BackupContent.Saves };
        var profile = new ProviderProfile { Name = "模拟仓库", Kind = ProviderKind.WebDav };
        settings.Profiles.Add(profile);
        settings.ActiveProfileId = profile.Id;
        settings.SaveConfigs.Add(new GameSaveConfig
        {
            GameId = game.Id,
            Locations = [new SaveLocation { Id = "rootA", BaseKind = SaveBaseKind.GameRoot, RelativePath = "saveA" }]
        });
        remote ??= new RemoteState();
        var coordinator = new BackupCoordinator(local, new SaveOnlyHost(game), settings, new SettingsStore(local),
            new PendingRestoreStore(local), _ => new MemoryProvider(remote));
        return new Context(coordinator, settings, remote, game);
    }

    private static string NewId() => DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "_" + Guid.NewGuid().ToString("N");
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private sealed record Context(BackupCoordinator Coordinator, BackupSettings Settings, RemoteState Remote, GameIdentity Game);
    private sealed class SaveOnlyHost(GameIdentity game) : IBackupHost
    {
        public IReadOnlyList<GameIdentity> GetGames() => [game];
        public Task<HostExport> ExportAsync(string workDirectory, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("存档专用测试不得调用软件导出。");
        public Task StageImportAsync(string mergedArchivePath, Dictionary<string, JsonNode?> settings, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("存档专用测试不得调用软件导入。");
    }
    private sealed class RemoteState
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public List<string> Uploads { get; } = [];
        public List<string> Downloads { get; } = [];
        public Func<string, bool>? FailUpload { get; set; }
        public Func<string, IReadOnlyList<RemoteItem>?>? ListOverride { get; set; }
        public void Put<T>(string path, T value) => Files[path] = JsonSerializer.SerializeToUtf8Bytes(value, BackupJson.Options);
        public T Read<T>(string path) => JsonSerializer.Deserialize<T>(Files[path], BackupJson.Options)!;
    }
    private sealed class MemoryProvider(RemoteState state) : IBackupProvider
    {
        public Task AuthorizeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ProbeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RemoteItem>> ListAsync(string directory, CancellationToken cancellationToken = default)
        {
            if (state.ListOverride?.Invoke(directory) is { } replacement) return Task.FromResult(replacement);
            var prefix = directory.TrimEnd('/') + "/";
            var children = new Dictionary<string, RemoteItem>();
            foreach (var (path, bytes) in state.Files.Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                var relative = path[prefix.Length..];
                var slash = relative.IndexOf('/');
                var child = slash < 0 ? path : prefix + relative[..slash];
                children[child] = new RemoteItem(child, slash >= 0, slash < 0 ? bytes.Length : 0);
            }
            return Task.FromResult<IReadOnlyList<RemoteItem>>(children.Values.ToList());
        }
        public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(state.Files.ContainsKey(path) || state.Files.Keys.Any(k => k.StartsWith(path + "/", StringComparison.Ordinal)));
        public async Task UploadAsync(string path, string localFile, CancellationToken cancellationToken = default)
        {
            if (state.FailUpload?.Invoke(path) == true) throw new IOException("模拟上传中断。");
            state.Files[path] = await File.ReadAllBytesAsync(localFile, cancellationToken);
            state.Uploads.Add(path);
        }
        public async Task DownloadAsync(string path, string localFile, CancellationToken cancellationToken = default)
        {
            state.Downloads.Add(path);
            if (!state.Files.TryGetValue(path, out var bytes)) throw new FileNotFoundException("模拟云端文件不存在。");
            Directory.CreateDirectory(Path.GetDirectoryName(localFile)!);
            await File.WriteAllBytesAsync(localFile, bytes, cancellationToken);
        }
        public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
        {
            state.Files.Remove(path);
            return Task.CompletedTask;
        }
        public void Dispose() { }
    }
}
