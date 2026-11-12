namespace PotatoVN.Backup.Core.Engine;

// 比较完整配方，包含删除和仅改名；上传数仍由存储层报告实际网络写入。
public static class BackupChangeSummary
{
    public static string Describe(SnapshotManifest? previous, SnapshotManifest current, IReadOnlyList<SaveManifest> saves)
    {
        var parts = new List<string>();
        AddFiles("资料", previous?.DataRecipe, current.DataRecipe);
        AddFiles("图片", previous?.MediaRecipe, current.MediaRecipe);
        var oldPaths = previous?.SaveManifestPaths.ToHashSet(StringComparer.Ordinal) ?? [];
        int changed = current.SaveManifestPaths.Where(path => !oldPaths.Contains(path))
            .Select(path => saves.First(save => save.ManifestPath == path).Game.Id).Distinct().Count();
        var currentGames = current.SaveManifestPaths.Select(path => saves.First(save => save.ManifestPath == path).Game.Id).ToHashSet();
        int removed = oldPaths.Select(path => saves.First(save => save.ManifestPath == path).Game.Id).Distinct().Except(currentGames).Count();
        if (changed > 0) parts.Add($"存档更新 {changed} 款游戏");
        if (removed > 0) parts.Add($"存档移出此版本 {removed} 份");
        if (parts.Count == 0) parts.Add("备份范围或游戏资料清单已更新");
        return string.Join("；", parts);

        void AddFiles(string label, ArchiveRecipe? before, ArchiveRecipe? after)
        {
            var old = before?.Files.ToDictionary(file => file.Path, StringComparer.Ordinal) ?? [];
            var next = after?.Files.ToDictionary(file => file.Path, StringComparer.Ordinal) ?? [];
            int added = next.Keys.Except(old.Keys, StringComparer.Ordinal).Count();
            int edited = next.Count(pair => old.TryGetValue(pair.Key, out FileRecord? file) &&
                (file.Sha256 != pair.Value.Sha256 || file.Length != pair.Value.Length));
            int deleted = old.Keys.Except(next.Keys, StringComparer.Ordinal).Count();
            var counts = new List<string>();
            if (added > 0) counts.Add($"新增 {added}");
            if (edited > 0) counts.Add($"修改 {edited}");
            if (deleted > 0) counts.Add($"移除 {deleted}");
            if (counts.Count > 0) parts.Add(label + string.Join("、", counts) + " 个文件");
        }
    }

    public static string Size(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1048576d:0.#} MB" :
        bytes >= 1024 ? $"{bytes / 1024d:0.#} KB" : $"{bytes} B";
}
