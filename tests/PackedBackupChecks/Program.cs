using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PotatoVN.Backup.Core;
using PotatoVN.Backup.Core.Engine;

string root = Path.Combine(Path.GetTempPath(), "PotatoVNPackedChecks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = new (string Name, Func<Task> Run)[]
{
    ("变化摘要包含删除且不列未变化类型", Summary),
    ("v3首次合包及完整重建", Initial),
    ("跨实例无变化只读索引及目录", Unchanged),
    ("只合包变化文件并保留历史", Delta),
    ("索引半写回退及双损坏停止", InterruptedIndex),
    ("图片缓存及七天完整重校验", ImageCache),
    ("默认恢复接入待恢复区与软件合并", RestoreFlow),
    ("缺失包补传与损坏包拒绝恢复", MissingAndCorrupt),
    ("不可补齐历史缺失时不上传或清理", UnrecoverableHistory),
    ("重命名删除只更新完整引用清单", RenameDelete),
    ("双槽保护及删除失败后重试", Retention),
    ("取消不发布索引并释放临时文件", CancelUpload),
    ("新设备无本机存档时复制独立基础", CrossDevice),
    ("手动自动内容独立及非法引用拒绝", SelectionAndValidation),
    ("v2存档迁入v3并保留旧历史读取", LegacyMigration),
    ("v1存档复制为独立v3对象", LegacyV1Migration),
    ("同设备写入串行且损坏本机缓存可重建", WriterAndCache),
    ("100游戏实际WebDAV请求数与本机缓存占用", () => RequestScaleChecks.Run(root)),
};
int passed = 0;
try
{
    foreach (var check in checks) { await check.Run(); passed++; Console.WriteLine("PASS " + check.Name); }
    Console.WriteLine($"PASS {passed}/{checks.Length} packed backup checks; synthetic data only");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }
finally
{
    string allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PotatoVNPackedChecks")) + Path.DirectorySeparatorChar;
    if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("合成清理越界");
    Directory.Delete(root, true);
}
async Task Summary()
{
    var game = new GameIdentity { Id = Guid.NewGuid(), Name = "摘要测试" };
    var old = new SnapshotManifest { DataRecipe = new ArchiveRecipe { Files = [new FileRecord { Path = "old", Sha256 = "a" }, new FileRecord { Path = "edit", Sha256 = "b" }] }, SaveManifestPaths = ["save-old"] };
    var next = new SnapshotManifest { DataRecipe = new ArchiveRecipe { Files = [new FileRecord { Path = "new", Sha256 = "c" }, new FileRecord { Path = "edit", Sha256 = "d" }] }, SaveManifestPaths = ["save-new"] };
    string summary = BackupChangeSummary.Describe(old, next, [new SaveManifest { ManifestPath = "save-old", Game = game }, new SaveManifest { ManifestPath = "save-new", Game = game }]);
    Check(summary.Contains("新增 1、修改 1、移除 1") && summary.Contains("存档更新 1") && !summary.Contains("图片") && !summary.Contains("存档移出"), "摘要漏报删除或把存档更新误判为移出");
    var fixture = Fixture.Create(root);
    fixture.Settings.Content = BackupContent.Saves;
    fixture.Settings.SaveConfigs.Clear();
    string result = "";
    fixture.Coordinator.Progress += text => result = text;
    var snapshot = await fixture.Coordinator.BackupAsync();
    Check(snapshot.SaveManifestPaths.Count == 0 && result.Contains("1 款未找到文件") && !result.Contains("合成游戏"), "无存档应汇总数量，不能逐项打印游戏名");
}

async Task Initial()
{
    var fixture = Fixture.Create(root);
    SnapshotManifest snapshot = await fixture.Coordinator.BackupAsync();
    Check(snapshot.FormatVersion == 3 && snapshot.SaveManifestPaths.Count == 1 && fixture.Host.FullExports == 0, "默认合包路径调用了完整导出");
    Check(fixture.Remote.Files.Keys.Count(path => path.EndsWith(".zip")) <= 2 && !fixture.Remote.Files.Keys.Any(path => path.EndsWith(".complete")), "未合包或仍逐对象写标记");
    PackedCatalog catalog = await fixture.Catalog();
    var store = new PackedArchiveStore(fixture.Provider(), catalog, fixture.Root);
    string media = Path.Combine(fixture.Root, "media.zip");
    await store.RestoreAsync(snapshot.MediaRecipe!, media);
    Check(ReadZip(media, "Images/cover.bin").SequenceEqual(fixture.Host.Image), "图片重建错误");
    SaveManifest save = catalog.State.Saves.Single();
    string archive = Path.Combine(fixture.Root, "save.zip");
    await store.RestoreAsync(save.Recipe!, archive);
    Check(Encoding.UTF8.GetString(ReadZip(archive, "roots/save/slot.dat")) == "initial progress", "存档重建错误");
}
async Task Unchanged()
{
    var fixture = Fixture.Create(root);
    SnapshotManifest initial = await fixture.Coordinator.BackupAsync();
    fixture.Remote.Reset();
    var coordinator = fixture.NewCoordinator();
    string summary = "";
    coordinator.Progress += text => summary = text;
    SnapshotManifest second = await coordinator.BackupAsync();
    Check(summary.StartsWith("内容未变化") && !summary.Contains("本次更改") && !summary.Contains("图片摘要"), "无变化结果不应逐项刷屏");
    Check(second.ManifestPath == initial.ManifestPath && fixture.Remote.Uploads.Count == 0 && fixture.Remote.Operations <= 6, "无变化仍产生版本、上传或逐文件检查");
    Console.WriteLine($"REQUEST unchanged: {fixture.Remote.Operations}; upload 0");
}
async Task Delta()
{
    var fixture = Fixture.Create(root);
    SnapshotManifest first = await fixture.Coordinator.BackupAsync();
    await File.WriteAllTextAsync(fixture.SavePath, "new progress");
    fixture.Host.PlayCount++;
    fixture.Remote.Reset();
    var coordinator = fixture.NewCoordinator();
    string summary = "";
    coordinator.Progress += text => summary = text;
    SnapshotManifest next = await coordinator.BackupAsync();
    Check(summary.Contains("本次更改：资料") && summary.Contains("存档更新 1 款游戏") && !summary.Contains("图片") && !summary.Contains("移出"), "变化摘要应只列真实变化，存档更新不能算移出");
    Check(summary.Split('\n').Length <= 5 && summary.Contains("上传："), "完成结果应分段且简短");
    var listed = await coordinator.ListPackedSnapshotsAsync(includeLegacy: false);
    Check(listed.All(snapshot => snapshot.SaveGames.Count == 1 && snapshot.SaveGames.ContainsKey(fixture.Host.GetGames()[0].Id)), "历史存档标签必须来自该快照的实际引用");
    Check(next.ManifestPath != first.ManifestPath && fixture.Remote.Uploads.Count(path => path.EndsWith(".zip")) == 1, "单份存档及资料没有合成一个变化包");
    PackedCatalog catalog = await fixture.Catalog();
    Check(catalog.State.Snapshots.Count == 2 && catalog.State.Saves.Count == 2, "历史丢失");
    var store = new PackedArchiveStore(fixture.Provider(), catalog, fixture.Root);
    foreach (var pair in new[] { (first, "initial progress"), (next, "new progress") })
    {
        SaveManifest save = catalog.State.Saves.Single(save => save.ManifestPath == pair.Item1.SaveManifestPaths.Single());
        string archive = Path.Combine(fixture.Root, Guid.NewGuid().ToString("N") + ".zip");
        await store.RestoreAsync(save.Recipe!, archive);
        Check(Encoding.UTF8.GetString(ReadZip(archive, "roots/save/slot.dat")) == pair.Item2, "增量历史不能独立恢复");
    }
}
async Task InterruptedIndex()
{
    var fixture = Fixture.Create(root);
    SnapshotManifest first = await fixture.Coordinator.BackupAsync();
    await File.WriteAllTextAsync(fixture.SavePath, "interrupted progress");
    fixture.Remote.BeforeUpload = (path, bytes) =>
    {
        if (!path.EndsWith(".json.gz")) return;
        fixture.Remote.Files[path] = bytes[..(bytes.Length / 2)];
        throw new IOException("synthetic half index");
    };
    await Fails<IOException>(() => fixture.Coordinator.BackupAsync());
    fixture.Remote.BeforeUpload = null;
    PackedCatalog recovered = await fixture.Catalog();
    Check(recovered.State.Snapshots.Single().ManifestPath == first.ManifestPath, "半写索引覆盖了完成版本");
    await fixture.Coordinator.BackupAsync();
    fixture.Remote.Files[PackedFormat.IndexPath(fixture.Settings.DeviceId, 0)] = [1, 2, 3];
    fixture.Remote.Files[PackedFormat.IndexPath(fixture.Settings.DeviceId, 1)] = [1, 2, 3];
    fixture.Remote.Reset();
    await Fails<InvalidDataException>(() => fixture.Coordinator.BackupAsync());
    Check(fixture.Remote.Uploads.Count == 0 && fixture.Remote.Deletes.Count == 0, "双索引损坏仍写入或删除数据");
}
async Task RestoreFlow()
{
    var fixture = Fixture.Create(root);
    SnapshotManifest snapshot = await fixture.Coordinator.BackupAsync();
    Check((await fixture.Coordinator.ListSnapshotsAsync()).Any(item => item.ManifestPath == snapshot.ManifestPath), "默认历史没有v3版本");
    await fixture.Coordinator.StageRestoreAsync(snapshot, BackupContent.Saves);
    Check(fixture.Coordinator.Pending.Items.Count == 1 && fixture.Host.FullExports == 0, "仅恢复存档不应完整导出或自动写游戏目录");
    PendingRestore item = fixture.Coordinator.Pending.Items.Single();
    Check(item.Manifest.FormatVersion == 1 && item.Manifest.Recipe == null, "待恢复区没有转换为本机ZIP");
    await fixture.Coordinator.StageRestoreAsync(snapshot, BackupContent.GameInfo | BackupContent.Covers);
    Check(fixture.Host.FullExports == 1 && fixture.Host.ImportedArchive != null, "软件恢复未走原生合并流程");
    using var archive = new ZipArchive(new MemoryStream(fixture.Host.ImportedArchive!));
    Check(archive.GetEntry("data.galgames.json") != null, "合并包缺少游戏资料");
}

async Task MissingAndCorrupt()
{
    var fixture = Fixture.Create(root);
    SnapshotManifest snapshot = await fixture.Coordinator.BackupAsync();
    PackedCatalog initial = await fixture.Catalog();
    string mediaPath = PackedFormat.ObjectPath(initial.State.DeviceId, initial.State.Objects[initial.State.Files[snapshot.MediaRecipe!.Files[0].Sha256].ObjectId]);
    fixture.Remote.Files.Remove(mediaPath);
    fixture.Remote.Reset();
    SnapshotManifest repaired = await fixture.NewCoordinator().BackupAsync();
    Check(fixture.Remote.Uploads.Count(path => path.EndsWith(".zip")) == 1 && fixture.Remote.Files.ContainsKey(mediaPath), "缺失图片包没有按需补传");
    PackedCatalog catalog = await fixture.Catalog();
    fixture.Remote.Files[mediaPath][0] ^= 0x40;
    string target = Path.Combine(fixture.Root, "protected.zip");
    await File.WriteAllTextAsync(target, "existing target");
    var store = new PackedArchiveStore(fixture.Provider(), catalog, fixture.Root);
    await Fails<InvalidDataException>(() => store.RestoreAsync(repaired.MediaRecipe!, target));
    Check(await File.ReadAllTextAsync(target) == "existing target", "损坏包覆盖了已有目标");
}
async Task UnrecoverableHistory()
{
    var fixture = Fixture.Create(root);
    SnapshotManifest first = await fixture.Coordinator.BackupAsync();
    PackedCatalog initial = await fixture.Catalog();
    SaveManifest old = initial.State.Saves.Single();
    string path = PackedFormat.ObjectPath(initial.State.DeviceId, initial.State.Objects[initial.State.Files[old.Files[0].Sha256].ObjectId]);
    await File.WriteAllTextAsync(fixture.SavePath, "new local progress");
    fixture.Host.PlayCount++;
    await fixture.NewCoordinator().BackupAsync();
    fixture.Remote.Files.Remove(path);
    fixture.Remote.Reset();
    await Fails<InvalidDataException>(() => fixture.NewCoordinator().BackupAsync());
    Check(fixture.Remote.Uploads.Count == 0 && fixture.Remote.Deletes.Count == 0, "不可补齐历史时仍然上传或清理");
}

async Task RenameDelete()
{
    var fixture = Fixture.Create(root);
    string directory = Path.GetDirectoryName(fixture.SavePath)!;
    string extra = Path.Combine(directory, "extra.dat");
    await File.WriteAllTextAsync(extra, "extra old progress");
    await fixture.Coordinator.BackupAsync();
    string renamed = Path.Combine(directory, "renamed.dat");
    Check(Path.GetFullPath(renamed).StartsWith(Path.GetFullPath(fixture.Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "合成重命名越界");
    File.Move(fixture.SavePath, renamed); File.Delete(extra);
    fixture.Remote.Reset();
    SnapshotManifest snapshot = await fixture.NewCoordinator().BackupAsync();
    Check(!fixture.Remote.Uploads.Any(path => path.StartsWith("v3/objects/", StringComparison.Ordinal)), "重命名删除重传了内容");
    PackedCatalog catalog = await fixture.Catalog();
    SaveManifest save = catalog.State.Saves.Single(save => save.ManifestPath == snapshot.SaveManifestPaths.Single());
    Check(save.Files.Count == 1 && save.Files[0].Path.EndsWith("renamed.dat"), "新版本没有完整反映重命名删除");
}
async Task Retention()
{
    var fixture = Fixture.Create(root);
    fixture.Settings.Content = BackupContent.Saves; fixture.Settings.KeepSnapshots = 1; fixture.Settings.KeepSaves = 1;
    byte[] bytes = new byte[8 * 1024 * 1024 + 1];
    bytes[0] = 1; await File.WriteAllBytesAsync(fixture.SavePath, bytes);
    await fixture.Coordinator.BackupAsync();
    string oldest = fixture.Remote.Files.Keys.Single(path => path.StartsWith("v3/objects/", StringComparison.Ordinal) && path.EndsWith(".gz"));
    bytes[0] = 2; await File.WriteAllBytesAsync(fixture.SavePath, bytes); await fixture.NewCoordinator().BackupAsync();
    Check(fixture.Remote.Files.ContainsKey(oldest), "上一个索引仍引用时提前删除了旧包");
    bool injected = false;
    fixture.Remote.BeforeDelete = path => { if (path == oldest && !injected) { injected = true; throw new IOException("synthetic delete failure"); } };
    bytes[0] = 3; await File.WriteAllBytesAsync(fixture.SavePath, bytes); await fixture.NewCoordinator().BackupAsync();
    Check(injected && fixture.Remote.Files.ContainsKey(oldest), "删除故障未被保留为可重试候选");
    await fixture.NewCoordinator().BackupAsync();
    Check(!fixture.Remote.Files.ContainsKey(oldest), "无变化备份没有重试已确认过期的包");
    PackedCatalog catalog = await fixture.Catalog();
    Check(catalog.State.Snapshots.Count == 1 && catalog.State.Saves.Count == 1, "保留数量未生效");
    string restored = Path.Combine(fixture.Root, "large-restored.zip");
    await new PackedArchiveStore(fixture.Provider(), catalog, fixture.Root).RestoreAsync(catalog.State.Saves.Single().Recipe!, restored);
    byte[] original = ReadZip(restored, "roots/save/slot.dat");
    Check(original.Length == bytes.Length && original[0] == 3, "大文件独立GZip对象恢复不完整");
}
async Task CancelUpload()
{
    var fixture = Fixture.Create(root);
    SnapshotManifest first = await fixture.Coordinator.BackupAsync();
    await File.WriteAllTextAsync(fixture.SavePath, "cancel new progress");
    using var cancellation = new CancellationTokenSource();
    fixture.Remote.BeforeUpload = (path, bytes) =>
    {
        if (!path.EndsWith(".zip")) return;
        fixture.Remote.Files[path] = bytes[..(bytes.Length / 2)]; cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested();
    };
    await Fails<OperationCanceledException>(() => fixture.Coordinator.BackupAsync(cancellation.Token));
    fixture.Remote.BeforeUpload = null;
    Check((await fixture.Catalog()).State.Snapshots.Single().ManifestPath == first.ManifestPath, "取消发布了未完成版本");
    Check(!Directory.EnumerateFiles(Path.Combine(fixture.Root, "work"), "*", SearchOption.AllDirectories).Any(), "取消后保留了本机工作文件");
    await fixture.NewCoordinator().BackupAsync();
}
async Task CrossDevice()
{
    var original = Fixture.Create(root);
    original.Settings.Content = BackupContent.Saves;
    SnapshotManifest snapshot = await original.Coordinator.BackupAsync();
    SaveManifest source = (await original.Catalog()).State.Saves.Single();
    string nextRoot = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(nextRoot);
    var game = new GameIdentity { Id = original.Host.GetGames()[0].Id, Name = "合成新设备游戏" };
    var settings = new BackupSettings { Content = BackupContent.Saves, Profiles = original.Settings.Profiles, ActiveProfileId = original.Settings.ActiveProfileId };
    var host = new Host(game, nextRoot);
    var coordinator = new BackupCoordinator(nextRoot, host, settings, new SettingsStore(nextRoot), new PendingRestoreStore(nextRoot), _ => original.Provider());
    SnapshotManifest copied = await coordinator.BackupAsync();
    PackedCatalog catalog = await PackedCatalog.LoadAsync(original.Provider(), settings.DeviceId, nextRoot);
    SaveManifest save = catalog.State.Saves.Single();
    Check(save.SourceDeviceId == original.Settings.DeviceId && save.CapturedUtc == source.CapturedUtc && save.Recipe!.DeviceId == settings.DeviceId, "跨设备复制丢失来源或仍交叉引用对象");
    foreach (string path in original.Remote.Files.Keys.Where(path => path.StartsWith($"v3/objects/{original.Settings.DeviceId:N}/", StringComparison.Ordinal)).ToArray()) original.Remote.Files.Remove(path);
    string archive = Path.Combine(nextRoot, "copied.zip");
    await new PackedArchiveStore(original.Provider(), catalog, nextRoot).RestoreAsync(save.Recipe!, archive);
    Check(Encoding.UTF8.GetString(ReadZip(archive, "roots/save/slot.dat")) == "initial progress", "原设备清理后新设备副本失效");
}
async Task SelectionAndValidation()
{
    var fixture = Fixture.Create(root);
    fixture.Settings.Content = BackupContent.GameInfo | BackupContent.Covers;
    fixture.Settings.AutomaticContent = BackupContent.Saves;
    SnapshotManifest automatic = await fixture.Coordinator.BackupAsync(automatic: true);
    Check(automatic.DataRecipe == null && automatic.MediaRecipe == null && automatic.SaveManifestPaths.Count == 1, "自动备份混入手动内容");
    SnapshotManifest manual = await fixture.NewCoordinator().BackupAsync();
    Check(manual.SaveManifestPaths.Count == 0 && manual.MediaRecipe!.Files.Count == 1, "手动备份混入存档或未选头图");
    PackedCatalog catalog = await fixture.Catalog();
    catalog.State.Snapshots[0].DataRecipe!.Files[0].Path = "../escape";
    int uploads = fixture.Remote.Uploads.Count;
    await Fails<InvalidDataException>(() => catalog.PublishAsync());
    Check(fixture.Remote.Uploads.Count == uploads, "非法引用在校验前上传了索引");
}

async Task LegacyMigration()
{
    var fixture = Fixture.Create(root);
    fixture.Settings.Content = BackupContent.Saves;
    SnapshotManifest old = await fixture.Coordinator.BackupIncrementalAsync();
    string[] oldPaths = fixture.Remote.Files.Keys.ToArray();
    fixture.Settings.SaveConfigs.Clear();
    SnapshotManifest migrated = await fixture.NewCoordinator().BackupAsync();
    PackedCatalog catalog = await fixture.Catalog();
    Check(migrated.FormatVersion == 3 && catalog.State.Saves.Single().CapturedUtc != default && oldPaths.All(fixture.Remote.Files.ContainsKey), "迁移改写或删除了旧格式");
    Check((await fixture.Coordinator.ListSnapshotsAsync()).Select(item => item.FormatVersion).ToHashSet().SetEquals([2, 3]), "历史没有兼容v2和v3");
    string archive = Path.Combine(fixture.Root, "migrated.zip");
    await new PackedArchiveStore(fixture.Provider(), catalog, fixture.Root).RestoreAsync(catalog.State.Saves.Single().Recipe!, archive);
    Check(Encoding.UTF8.GetString(ReadZip(archive, "roots/save/slot.dat")) == "initial progress", "v2引用复制后的内容不完整");
}

async Task LegacyV1Migration()
{
    var fixture = Fixture.Create(root); fixture.Settings.Content = BackupContent.Saves;
    string archive = Path.Combine(fixture.Root, "legacy-source.zip");
    SaveManifest save = (await SaveArchiveService.CreateAsync(fixture.Host.GetGames()[0], fixture.Settings.SaveConfigs[0].Locations, archive, fixture.Settings.DeviceId))!;
    string prefix = $"v1/saves/{save.Game.Id:N}/{save.DeviceId:N}/{save.Id}";
    save.ManifestPath = prefix + "/manifest.json"; save.ArchivePath = prefix + "/save.zip";
    fixture.Remote.Files[save.ArchivePath] = await File.ReadAllBytesAsync(archive);
    fixture.Remote.Files[save.ManifestPath] = JsonSerializer.SerializeToUtf8Bytes(save, BackupJson.Options);
    fixture.Settings.SaveConfigs.Clear();
    await fixture.Coordinator.BackupAsync();
    PackedCatalog catalog = await fixture.Catalog();
    SaveManifest copied = catalog.State.Saves.Single();
    Check(copied.FormatVersion == 3 && copied.CapturedUtc == save.CapturedUtc && fixture.Remote.Files.ContainsKey(save.ArchivePath), "v1迁移丢失时间或改写原件");
}
async Task WriterAndCache()
{
    var fixture = Fixture.Create(root);
    bool checkedWriter = false;
    fixture.Remote.BeforeUpload = (path, _) =>
    {
        if (checkedWriter || !path.EndsWith(".zip")) return;
        checkedWriter = true;
        try { fixture.NewCoordinator().BackupAsync().GetAwaiter().GetResult(); throw new InvalidOperationException("同设备同时写索引"); }
        catch (IOException error) { Check(error.Message.Contains("已有"), "串行写入没有提供明确状态"); }
    };
    SnapshotManifest first = await fixture.Coordinator.BackupAsync();
    fixture.Remote.BeforeUpload = null;
    Check(checkedWriter, "未覆盖同设备写入竞争");
    string cache = Path.Combine(fixture.Root, "cache", "images.json");
    await File.WriteAllTextAsync(cache, "{broken-cache");
    SnapshotManifest next = await fixture.NewCoordinator().BackupAsync();
    Check(first.ManifestPath == next.ManifestPath && (await File.ReadAllTextAsync(cache)).StartsWith("{"), "本机摘要缓存损坏未重建");
}

async Task ImageCache()
{
    string directory = Path.Combine(root, "cache-test"); Directory.CreateDirectory(directory);
    string image = Path.Combine(directory, "image"); await File.WriteAllTextAsync(image, "aaaa");
    DateTime stamp = File.GetLastWriteTimeUtc(image);
    var time = new ManualTime();
    var first = new FileHashCache(directory, time);
    var file = await first.ReadAsync("Images/image", image); await first.SaveAsync();
    await File.WriteAllTextAsync(image, "bbbb"); File.SetLastWriteTimeUtc(image, stamp);
    var cached = new FileHashCache(directory, time); await cached.LoadAsync();
    Check((await cached.ReadAsync("Images/image", image)).Record.Sha256 == file.Record.Sha256 && cached.ReusedHashes == 1, "属性缓存未复用");
    time.Now = time.Now.AddDays(7);
    Check((await cached.ReadAsync("Images/image", image)).Record.Sha256 != file.Record.Sha256, "七天完整校验没有发现同属性变化");
}
static byte[] ReadZip(string path, string entry) { using var zip = ZipFile.OpenRead(path); using var input = zip.GetEntry(entry)!.Open(); using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray(); }
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static async Task Fails<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
sealed class ManualTime : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
sealed class Fixture
{
    public string Root = "", SavePath = "";
    public Remote Remote = new();
    public Host Host = null!;
    public BackupSettings Settings = null!;
    public BackupCoordinator Coordinator = null!;
    public IBackupProvider Provider() => new Provider(Remote);
    public BackupCoordinator NewCoordinator() => new(Root, Host, Settings, new SettingsStore(Root), new PendingRestoreStore(Root), _ => Provider());
    public Task<PackedCatalog> Catalog() => PackedCatalog.LoadAsync(Provider(), Settings.DeviceId, Root);
    public static Fixture Create(string parent)
    {
        var value = new Fixture { Root = Path.Combine(parent, Guid.NewGuid().ToString("N")) };
        string gameRoot = Path.Combine(value.Root, "game"); string save = Path.Combine(gameRoot, "save"); Directory.CreateDirectory(save);
        value.SavePath = Path.Combine(save, "slot.dat"); File.WriteAllText(value.SavePath, "initial progress");
        var game = new GameIdentity { Id = Guid.NewGuid(), Name = "合成游戏", LocalDirectory = gameRoot };
        value.Host = new Host(game, value.Root);
        var profile = new ProviderProfile();
        value.Settings = new BackupSettings { Profiles = [profile], ActiveProfileId = profile.Id,
            SaveConfigs = [new GameSaveConfig { GameId = game.Id, Locations = [new SaveLocation { Id = "save", BaseKind = SaveBaseKind.GameRoot, RelativePath = "save" }] }] };
        value.Coordinator = value.NewCoordinator();
        return value;
    }
}
sealed class Host(GameIdentity game, string root) : IBackupHost
{
    public int PlayCount = 1, FullExports;
    public byte[]? ImportedArchive;
    public byte[] Image = [1, 2, 3, 4, 5];
    public IReadOnlyList<GameIdentity> GetGames() => [game];
    public async Task<HostExport> ExportForBackupAsync(string directory, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        string image = Path.Combine(root, "cover"); if (!File.Exists(image)) await File.WriteAllBytesAsync(image, Image, ct);
        var raw = new JsonArray(new JsonObject { ["Uuid"] = game.Id.ToString(), ["Name"] = new JsonObject { ["Value"] = game.Name }, ["ImagePath"] = "./Images/cover.bin", ["PlayCount"] = PlayCount });
        await File.WriteAllTextAsync(Path.Combine(directory, "data.galgames.json"), raw.ToJsonString(), ct);
        string header = Path.Combine(root, "header"); if (!File.Exists(header)) await File.WriteAllBytesAsync(header, [9, 8, 7, 6], ct);
        raw[0]!["HeaderImagePath"] = "./Images/header.bin";
        await File.WriteAllTextAsync(Path.Combine(directory, "data.galgames.json"), raw.ToJsonString(), ct);
        return new HostExport { NativeDirectory = directory, MediaPaths = new() { ["Images/cover.bin"] = image, ["Images/header.bin"] = header }, Settings = new() { ["language"] = JsonValue.Create("zh-CN") } };
    }
    public async Task<HostExport> ExportAsync(string directory, CancellationToken ct)
    {
        FullExports++;
        HostExport source = await ExportForBackupAsync(directory, ct);
        string archive = Path.Combine(directory, "native.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(Path.Combine(directory, "data.galgames.json"), "data.galgames.json");
            zip.CreateEntryFromFile(source.MediaPaths["Images/cover.bin"], "Images/cover.bin");
        }
        return new HostExport { ArchivePath = archive, Settings = source.Settings };
    }
    public async Task StageImportAsync(string archive, Dictionary<string, JsonNode?> settings, CancellationToken ct) => ImportedArchive = await File.ReadAllBytesAsync(archive, ct);
}
sealed class Remote
{
    public Dictionary<string, byte[]> Files = new(StringComparer.Ordinal);
    public List<string> Uploads = [], Deletes = [];
    public int Operations;
    public Action<string, byte[]>? BeforeUpload;
    public Action<string>? BeforeDelete;
    public void Reset() { Uploads.Clear(); Deletes.Clear(); Operations = 0; }
}
sealed class Provider(Remote remote) : IBackupProvider
{
    public void Dispose() { }
    public Task AuthorizeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> ExistsAsync(string path, CancellationToken ct = default) { remote.Operations++; return Task.FromResult(remote.Files.ContainsKey(path)); }
    public Task<IReadOnlyList<RemoteItem>> ListAsync(string directory, CancellationToken ct = default)
    {
        remote.Operations++; string prefix = directory.TrimEnd('/') + "/";
        IReadOnlyList<RemoteItem> items = remote.Files.Keys.Where(path => path.StartsWith(prefix, StringComparison.Ordinal)).Select(path => path[prefix.Length..].Split('/')[0]).Distinct()
            .Select(name => { string path = prefix + name; return new RemoteItem(path, !remote.Files.ContainsKey(path), remote.Files.TryGetValue(path, out var bytes) ? bytes.LongLength : 0); }).ToArray();
        return Task.FromResult(items);
    }
    public async Task UploadAsync(string path, string localFile, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); remote.Operations++;
        byte[] bytes = await File.ReadAllBytesAsync(localFile, ct); remote.BeforeUpload?.Invoke(path, bytes);
        remote.Files[path] = bytes; remote.Uploads.Add(path);
    }
    public async Task DownloadAsync(string path, string localFile, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); remote.Operations++;
        if (!remote.Files.TryGetValue(path, out var bytes)) throw new HttpRequestException("synthetic missing", null, HttpStatusCode.NotFound);
        Directory.CreateDirectory(Path.GetDirectoryName(localFile)!); await File.WriteAllBytesAsync(localFile, bytes, ct);
    }
    public Task DeleteAsync(string path, CancellationToken ct = default) { remote.Operations++; remote.BeforeDelete?.Invoke(path); remote.Files.Remove(path); remote.Deletes.Add(path); return Task.CompletedTask; }
}
