namespace PotatoVN.Backup.Core;

public sealed class RestoreMatchingDecisions
{
    // 当前快照中每个云端身份必须出现一次；null 表示不关联已有条目。
    public Dictionary<Guid, Guid?> Targets { get; set; } = [];
    public List<LocalGameMerge> Merges { get; set; } = [];
}

public sealed record LocalGameMerge(Guid RemovedGameId, Guid KeptGameId);
