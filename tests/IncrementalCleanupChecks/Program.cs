using System.Text.Json;
using PotatoVN.Backup.Core;
using PotatoVN.Backup.Core.Engine;

var tests = new (string Name, Action Run)[]
{
    ("保留快照的存档清单引用不完整时拒绝生成清理计划", () =>
    {
        var owner = Guid.NewGuid();
        var snapshot = new SnapshotManifest { DeviceId = owner, SaveManifestPaths = ["v2/saves/missing/manifest.json"] };
        try
        {
            IncrementalPrunePlan.Create([snapshot], [], owner, 1, 1);
            throw new Exception("缺少被保护清单时不能允许清理。");
        }
        catch (InvalidDataException) { }
    }),
    ("未变化的软件及图片对象继续被保留，计划不修改输入", Checks.SharedObjectsRemain),
    ("最后引用释放后才规划对象，v1/v2合算且旧ZIP独立列出", Checks.LastReferenceReleased),
    ("独立存档按每游戏保留，保护共享对象及保留的无引用存档", Checks.IndependentSaveRetention),
    ("其他设备保留快照保护本设备旧存档和对象", Checks.OtherDeviceProtectsSave),
    ("仅规划过期清单所含本设备对象，不包含其他设备命名空间或孤立对象", Checks.ForeignAndOrphanObjectsExcluded)
};
int failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + test.Name + ": " + error); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} incremental cleanup plan checks passed.");
return failed == 0 ? 0 : 1;

internal static class Checks
{
    private static readonly Guid Local = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Foreign = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid GameA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid GameB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static void SharedObjectsRemain()
    {
        SnapshotManifest old = Snapshot(Local, 1, Recipe(Local, 'a'));
        old.MediaRecipe = Recipe(Local, 'b', "media/cover.png");
        SnapshotManifest latest = Snapshot(Local, 2, Recipe(Local, 'a'));
        latest.MediaRecipe = Recipe(Local, 'b', "media/renamed-cover.png");
        SnapshotManifest[] snapshots = [old, latest];
        string before = JsonSerializer.Serialize(snapshots, BackupJson.Options);
        IncrementalPrunePlan plan = IncrementalPrunePlan.Create(snapshots, [], Local, 1, 1);
        Equal([old.ManifestPath], plan.ManifestPaths, "only the older snapshot expires");
        Equal([], plan.ObjectPaths, "shared data and media objects remain");
        Equal([], plan.LegacyArchivePaths, "v2 contains no legacy archive payload");
        Require(before == JsonSerializer.Serialize(snapshots, BackupJson.Options), "planning must not mutate manifests");
    }

    public static void LastReferenceReleased()
    {
        SnapshotManifest legacy = Snapshot(Local, 0, null, version: 1);
        legacy.Media = Record(legacy.ManifestPath.Replace("manifest.json", "media.zip"), 'f');
        SnapshotManifest old = Snapshot(Local, 1, Recipe(Local, 'a'));
        SnapshotManifest middle = Snapshot(Local, 2, Recipe(Local, 'a', "renamed.json"));
        SnapshotManifest latest = Snapshot(Local, 3, Recipe(Local, 'c'));
        IncrementalPrunePlan plan = IncrementalPrunePlan.Create([middle, legacy, latest, old], [], Local, 1, 1);
        Equal([legacy.ManifestPath, old.ManifestPath, middle.ManifestPath], plan.ManifestPaths, "retention combines v1/v2 snapshots");
        Equal([Object(Local, 'a')], plan.ObjectPaths, "one candidate for the last released shared object");
        Equal([legacy.Data!.Path, legacy.Media.Path], plan.LegacyArchivePaths, "expired v1 ZIPs are explicit candidates");
        Require(!plan.ObjectPaths.Contains(Object(Local, 'c')), "latest data remains");
    }

    public static void IndependentSaveRetention()
    {
        SaveManifest legacy = Save(Local, GameA, 0, Recipe(Local, 'e', "roots/root/slot.dat"), version: 1);
        SaveManifest old = Save(Local, GameA, 1, Recipe(Local, 'a', "roots/root/slot.dat"));
        SaveManifest latest = Save(Local, GameA, 2, Recipe(Local, 'a', "roots/root/slot.dat"));
        SaveManifest otherGame = Save(Local, GameB, 1, Recipe(Local, 'b', "roots/root/slot.dat"));
        SnapshotManifest expired = Snapshot(Local, 1, Recipe(Local, 'a'), [old.ManifestPath]);
        expired.MediaRecipe = Recipe(Local, 'b', "media/old.png");
        SnapshotManifest current = Snapshot(Local, 2, Recipe(Local, 'c'));
        IncrementalPrunePlan plan = IncrementalPrunePlan.Create([expired, current], [legacy, old, latest, otherGame], Local, 1, 1);
        Equal([expired.ManifestPath, legacy.ManifestPath, old.ManifestPath], plan.ManifestPaths, "each game's newest independent save is retained");
        Equal([], plan.ObjectPaths, "independent save retention protects hashes even without any retained snapshot reference");
        Equal([legacy.ArchivePath], plan.LegacyArchivePaths, "expired v1 save archive is separate from shared objects");
        Require(!plan.ManifestPaths.Contains(otherGame.ManifestPath), "one game's versions cannot evict another game's only save");
    }

    public static void OtherDeviceProtectsSave()
    {
        SaveManifest protectedSave = Save(Local, GameA, 1, Recipe(Local, 'a', "roots/root/slot.dat"));
        SaveManifest newestSave = Save(Local, GameA, 2, Recipe(Local, 'b', "roots/root/slot.dat"));
        SnapshotManifest old = Snapshot(Local, 1, Recipe(Local, 'a'), [protectedSave.ManifestPath]);
        SnapshotManifest latest = Snapshot(Local, 2, Recipe(Local, 'c'));
        SnapshotManifest foreign = Snapshot(Foreign, 0, null, [protectedSave.ManifestPath]);
        IncrementalPrunePlan plan = IncrementalPrunePlan.Create([foreign, old, latest], [protectedSave, newestSave], Local, 1, 1);
        Equal([old.ManifestPath], plan.ManifestPaths, "foreign snapshot and its referenced old save remain");
        Equal([], plan.ObjectPaths, "foreign snapshot indirectly protects the local object's last save reference");
        Require(!plan.ManifestPaths.Contains(protectedSave.ManifestPath), "protected save exceeds the numeric keep limit");
    }

    public static void ForeignAndOrphanObjectsExcluded()
    {
        SnapshotManifest old = Snapshot(Local, 1, Recipe(Foreign, 'e'));
        old.MediaRecipe = Recipe(Local, 'a', "media/old.png");
        SnapshotManifest latest = Snapshot(Local, 2, Recipe(Local, 'c'));
        SnapshotManifest foreignSnapshot = Snapshot(Foreign, 0, Recipe(Foreign, 'd'));
        SaveManifest oldSave = Save(Local, GameA, 1, Recipe(Foreign, 'f', "roots/root/slot.dat"));
        SaveManifest latestSave = Save(Local, GameA, 2, Recipe(Local, 'b', "roots/root/slot.dat"));
        SaveManifest foreignSave = Save(Foreign, GameB, 0, Recipe(Foreign, 'c', "roots/root/slot.dat"));
        IncrementalPrunePlan plan = IncrementalPrunePlan.Create([old, latest, foreignSnapshot], [oldSave, latestSave, foreignSave], Local, 1, 1);
        Equal([old.ManifestPath, oldSave.ManifestPath], plan.ManifestPaths, "only local manifests can expire");
        Equal([Object(Local, 'a')], plan.ObjectPaths, "foreign objects never become local deletion candidates");
        Require(!plan.ObjectPaths.Contains(Object(Local, 'd')), "an orphan object absent from manifests cannot enter the plan");
        Require(plan.ObjectPaths.All(path => path.StartsWith($"v2/objects/{Local:N}/", StringComparison.Ordinal)), "all candidate objects belong to the current device");
    }

    private static SnapshotManifest Snapshot(Guid device, int order, ArchiveRecipe? recipe,
        string[]? saves = null, int version = 2)
    {
        string id = "snapshot-" + order;
        string root = $"v{version}/snapshots/{device:N}/{id}";
        return new SnapshotManifest
        {
            FormatVersion = version, Id = id, DeviceId = device, DeviceName = "synthetic",
            CreatedUtc = Epoch.AddMinutes(order), ManifestPath = root + "/manifest.json",
            Content = recipe == null && version == 2 ? BackupContent.Saves : BackupContent.Settings,
            DataRecipe = version == 2 ? recipe : null,
            Data = version == 1 ? Record(root + "/data.zip", 'e') : null,
            SaveManifestPaths = saves?.ToList() ?? []
        };
    }

    private static SaveManifest Save(Guid device, Guid game, int order, ArchiveRecipe recipe, int version = 2)
    {
        string id = "save-" + order;
        string root = $"v{version}/saves/{game:N}/{device:N}/{id}";
        return new SaveManifest
        {
            FormatVersion = version, Id = id, DeviceId = device, Game = new GameIdentity { Id = game, Name = "synthetic" },
            CapturedUtc = Epoch.AddMinutes(order), ManifestPath = root + "/manifest.json",
            ArchivePath = version == 1 ? root + "/save.zip" : "", Sha256 = version == 1 ? new string('f', 64) : "",
            Length = 1, Files = recipe.Files, ContentHash = BackupFiles.TreeHash(recipe.Files),
            Recipe = version == 2 ? recipe : null,
            Locations = [new SaveLocation { Id = "root", BaseKind = SaveBaseKind.GameRoot, RelativePath = "save" }]
        };
    }

    private static ArchiveRecipe Recipe(Guid device, char hash, string path = "data.games.json") =>
        new() { DeviceId = device, Files = [Record(path, hash)] };
    private static FileRecord Record(string path, char hash) => new() { Path = path, Length = 1, Sha256 = new string(hash, 64) };
    private static string Object(Guid device, char hash) => IncrementalArchiveStore.ObjectPath(device, new string(hash, 64));
    private static void Equal(IEnumerable<string> expected, IEnumerable<string> actual, string message) =>
        Require(expected.OrderBy(path => path, StringComparer.Ordinal).SequenceEqual(actual.OrderBy(path => path, StringComparer.Ordinal), StringComparer.Ordinal), message);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}