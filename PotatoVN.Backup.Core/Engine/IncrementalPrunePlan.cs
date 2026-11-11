namespace PotatoVN.Backup.Core.Engine;

/// <summary>仅计算已发布历史的清理候选，不访问云端或执行任何删除。</summary>
public sealed class IncrementalPrunePlan
{
    public IReadOnlyList<string> ManifestPaths { get; }
    public IReadOnlyList<string> LegacyArchivePaths { get; }
    public IReadOnlyList<string> ObjectPaths { get; }

    private IncrementalPrunePlan(IEnumerable<string> manifests, IEnumerable<string> archives, IEnumerable<string> objects)
    {
        ManifestPaths = Paths(manifests);
        LegacyArchivePaths = Paths(archives);
        ObjectPaths = Paths(objects);
    }

    public static IncrementalPrunePlan Create(IReadOnlyList<SnapshotManifest> snapshots,
        IReadOnlyList<SaveManifest> saves, Guid deviceId, int keepSnapshots, int keepSaves)
    {
        if (deviceId == Guid.Empty) throw new ArgumentException("清理计划缺少设备标识。", nameof(deviceId));
        if (keepSnapshots < 1) throw new ArgumentOutOfRangeException(nameof(keepSnapshots));
        if (keepSaves < 1) throw new ArgumentOutOfRangeException(nameof(keepSaves));

        SnapshotManifest[] expiredSnapshots = snapshots.Where(snapshot => snapshot.DeviceId == deviceId)
            .OrderByDescending(snapshot => snapshot.CreatedUtc)
            .ThenByDescending(snapshot => snapshot.ManifestPath, StringComparer.Ordinal)
            .Skip(keepSnapshots).ToArray();
        HashSet<string> expiredSnapshotPaths = expiredSnapshots.Select(snapshot => snapshot.ManifestPath).ToHashSet(StringComparer.Ordinal);
        SnapshotManifest[] retainedSnapshots = snapshots.Where(snapshot => !expiredSnapshotPaths.Contains(snapshot.ManifestPath)).ToArray();
        HashSet<string> protectedSaves = retainedSnapshots.SelectMany(snapshot => snapshot.SaveManifestPaths).ToHashSet(StringComparer.Ordinal);
        HashSet<string> knownSaves = saves.Select(save => save.ManifestPath).ToHashSet(StringComparer.Ordinal);
        if (protectedSaves.Any(path => !knownSaves.Contains(path)))
            throw new InvalidDataException("保留快照引用的存档清单尚未完整读取，已停止历史清理。");

        SaveManifest[] expiredSaves = saves.Where(save => save.DeviceId == deviceId).GroupBy(save => save.Game.Id)
            .SelectMany(game => game.OrderByDescending(save => save.CapturedUtc)
                .ThenByDescending(save => save.ManifestPath, StringComparer.Ordinal).Skip(keepSaves))
            .Where(save => !protectedSaves.Contains(save.ManifestPath)).ToArray();
        HashSet<string> expiredSavePaths = expiredSaves.Select(save => save.ManifestPath).ToHashSet(StringComparer.Ordinal);
        SaveManifest[] retainedSaves = saves.Where(save => !expiredSavePaths.Contains(save.ManifestPath)).ToArray();

        // 存档历史独立于软件快照保留；没有任何快照引用的保留存档仍保护其对象。
        HashSet<string> retainedObjects = retainedSnapshots.SelectMany(SnapshotRecipes)
            .Concat(retainedSaves.Where(save => save.Recipe != null).Select(save => save.Recipe!))
            .SelectMany(IncrementalArchiveStore.ObjectPaths).ToHashSet(StringComparer.Ordinal);
        string ownedPrefix = $"v2/objects/{deviceId:N}/";
        IEnumerable<string> expiredObjects = expiredSnapshots.SelectMany(SnapshotRecipes)
            .Concat(expiredSaves.Where(save => save.Recipe != null).Select(save => save.Recipe!))
            .SelectMany(IncrementalArchiveStore.ObjectPaths)
            .Where(path => path.StartsWith(ownedPrefix, StringComparison.Ordinal) && !retainedObjects.Contains(path));

        // 只从明确过期的清单形成候选，不扫描孤立对象或其他设备的命名空间。
        IEnumerable<string> legacyArchives = expiredSnapshots.Where(snapshot => snapshot.FormatVersion == 1)
            .SelectMany(snapshot => new[] { snapshot.Data?.Path, snapshot.Media?.Path })
            .Concat(expiredSaves.Where(save => save.FormatVersion == 1).Select(save => (string?)save.ArchivePath))
            .Where(path => !string.IsNullOrEmpty(path)).Select(path => path!);
        return new IncrementalPrunePlan(expiredSnapshots.Select(snapshot => snapshot.ManifestPath)
            .Concat(expiredSaves.Select(save => save.ManifestPath)), legacyArchives, expiredObjects);
    }

    private static IEnumerable<ArchiveRecipe> SnapshotRecipes(SnapshotManifest snapshot)
    {
        if (snapshot.DataRecipe != null) yield return snapshot.DataRecipe;
        if (snapshot.MediaRecipe != null) yield return snapshot.MediaRecipe;
    }

    private static IReadOnlyList<string> Paths(IEnumerable<string> paths) => paths.Distinct(StringComparer.Ordinal)
        .OrderBy(path => path, StringComparer.Ordinal).ToList().AsReadOnly();
}