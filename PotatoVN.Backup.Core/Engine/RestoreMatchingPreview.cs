namespace PotatoVN.Backup.Core.Engine;

public sealed class RestoreMatchingAnalysis
{
    public IReadOnlyList<GameIdentity> CloudGames { get; init; } = [];
    public IReadOnlyList<GameIdentity> LocalGames { get; init; } = [];
    public IReadOnlyList<GameIdentity> UnmatchedLocalGames { get; init; } = [];
    public IReadOnlyDictionary<Guid, Guid> AutomaticTargets { get; init; } = new Dictionary<Guid, Guid>();
    public bool NeedsPrompt => CloudGames.Count > 0 && UnmatchedLocalGames.Count > 0;
}

public static class RestoreMatchingPreview
{
    public static bool RequiresMatching(BackupContent content) => (content & ~BackupContent.Settings) != BackupContent.None;

    public static RestoreMatchingAnalysis Analyze(IReadOnlyList<GameIdentity> cloudGames,
        IReadOnlyList<GameIdentity> localGames, IReadOnlyList<GameMatchMapping> mappings)
    {
        if (cloudGames.GroupBy(g => g.Id).Any(g => g.Count() != 1))
            throw new InvalidDataException("这个备份中包含重复的游戏身份，不能确定恢复目标。");
        Dictionary<Guid, Guid> candidates = [];
        foreach (GameIdentity cloud in cloudGames)
        {
            GameMatchResult match = GameMatcher.Match(cloud, localGames, mappings);
            if (!match.Conflict && match.Game != null) candidates[cloud.Id] = match.Game.Id;
        }
        // 同一个本地目标被多个云端版本竞争时，不把它当作明确匹配。
        HashSet<Guid> contested = candidates.Values.GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        Dictionary<Guid, Guid> confirmed = candidates.Where(pair => !contested.Contains(pair.Value)).ToDictionary(p => p.Key, p => p.Value);
        HashSet<Guid> matchedLocal = confirmed.Values.ToHashSet();
        return new RestoreMatchingAnalysis
        {
            CloudGames = cloudGames, LocalGames = localGames, AutomaticTargets = confirmed,
            UnmatchedLocalGames = localGames.Where(g => !matchedLocal.Contains(g.Id)).ToList()
        };
    }

    public static RestoreMatchingDecisions Decide(RestoreMatchingAnalysis analysis,
        IReadOnlyDictionary<Guid, Guid?> localToCloud)
    {
        HashSet<Guid> editable = analysis.UnmatchedLocalGames.Select(g => g.Id).ToHashSet();
        HashSet<Guid> cloudIds = analysis.CloudGames.Select(g => g.Id).ToHashSet();
        if (localToCloud.Keys.Any(id => !editable.Contains(id)) || localToCloud.Values.Any(id => id != null && !cloudIds.Contains(id.Value)))
            throw new InvalidOperationException("游戏列表已发生变化，请重新确认对应关系。");
        if (localToCloud.Values.Where(id => id != null).GroupBy(id => id).Any(g => g.Count() > 1))
            throw new InvalidOperationException("同一个云端游戏只能选择一个本地目标，请调整重复的选择。");
        RestoreMatchingDecisions result = new();
        foreach (GameIdentity cloud in analysis.CloudGames)
            result.Targets[cloud.Id] = analysis.AutomaticTargets.TryGetValue(cloud.Id, out Guid local) ? local : null;
        foreach (var choice in localToCloud.Where(p => p.Value != null))
        {
            Guid backup = choice.Value!.Value;
            if (result.Targets[backup] is { } previous && previous != choice.Key)
                result.Merges.Add(new LocalGameMerge(previous, choice.Key));
            result.Targets[backup] = choice.Key;
        }
        Validate(result, analysis.CloudGames, analysis.LocalGames);
        return result;
    }

    public static void Validate(RestoreMatchingDecisions decisions, IReadOnlyList<GameIdentity> cloudGames,
        IReadOnlyList<GameIdentity> localGames)
    {
        HashSet<Guid> cloudIds = cloudGames.Select(g => g.Id).ToHashSet();
        if (cloudIds.Count != cloudGames.Count || decisions.Targets.Count != cloudIds.Count || decisions.Targets.Keys.Any(id => !cloudIds.Contains(id)))
            throw new InvalidOperationException("恢复对应关系不完整，请重新选择备份。");
        if (decisions.Targets.Values.Where(v => v != null).GroupBy(v => v).Any(g => g.Count() > 1))
            throw new InvalidOperationException("同一个本地游戏不能同时接收此快照中的多个云端条目。");
        if (decisions.Targets.Values.Any(id => id != null && !localGames.Any(g => g.Id == id.Value)))
            throw new InvalidOperationException("所选本地条目已不存在，请重新选择对应关系。");
        HashSet<Guid> removed = [];
        foreach (LocalGameMerge merge in decisions.Merges)
        {
            GameIdentity? from = localGames.SingleOrDefault(g => g.Id == merge.RemovedGameId);
            GameIdentity? to = localGames.SingleOrDefault(g => g.Id == merge.KeptGameId);
            if (from == null || to == null || from.Id == to.Id || !removed.Add(from.Id) || decisions.Targets.Values.Contains(from.Id))
                throw new InvalidOperationException("待合并条目已改变，请重新确认。");
            if (from.IsRunning || to.IsRunning) throw new InvalidOperationException("请先退出相关游戏，再合并重复资料。");
            if (IsLinked(from) && (!IsLinked(to) || !SameDirectory(from.LocalDirectory!, to.LocalDirectory!)))
                throw new InvalidOperationException($"“{from.Name}”已关联其他本地目录，不能在此次恢复中自动移除；请保留为独立条目。");
            if (!decisions.Targets.Values.Contains(to.Id)) throw new InvalidOperationException("合并目标没有对应云端条目。");
        }
    }

    public static void ApplyMappings(BackupSettings settings, RestoreMatchingDecisions decisions)
    {
        foreach (var pair in decisions.Targets)
        {
            settings.GameMappings.RemoveAll(m => m.BackupGameId == pair.Key);
            if (pair.Value != null) settings.GameMappings.Add(new GameMatchMapping { BackupGameId = pair.Key, LocalGameId = pair.Value.Value });
        }
        foreach (LocalGameMerge merge in decisions.Merges)
        {
            foreach (GameMatchMapping map in settings.GameMappings.Where(m => m.LocalGameId == merge.RemovedGameId)) map.LocalGameId = merge.KeptGameId;
            if (!settings.GameMappings.Any(m => m.BackupGameId == merge.RemovedGameId))
                settings.GameMappings.Add(new GameMatchMapping { BackupGameId = merge.RemovedGameId, LocalGameId = merge.KeptGameId });
            settings.SelectedGameIds = settings.SelectedGameIds.Select(id => id == merge.RemovedGameId ? merge.KeptGameId : id).Distinct().ToList();
            GameSaveConfig? source = settings.SaveConfigs.FirstOrDefault(c => c.GameId == merge.RemovedGameId);
            if (source == null) continue;
            GameSaveConfig? target = settings.SaveConfigs.FirstOrDefault(c => c.GameId == merge.KeptGameId);
            if (target == null) { source.GameId = merge.KeptGameId; continue; }
            foreach (SaveLocation location in source.Locations)
                if (!target.Locations.Any(l => l.Id == location.Id)) target.Locations.Add(location);
            settings.SaveConfigs.Remove(source);
        }
    }

    private static bool IsLinked(GameIdentity game) => game.LocalDirectory is { } root && Directory.Exists(root);
    private static bool SameDirectory(string left, string right) => string.Equals(
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
}
