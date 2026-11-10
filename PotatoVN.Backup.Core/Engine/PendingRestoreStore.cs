namespace PotatoVN.Backup.Core.Engine;

public sealed record PendingStats(long Bytes, int Games, int Versions, IReadOnlyDictionary<RestoreStatus, int> Counts);

public sealed class PendingRestoreStore
{
    private readonly string _root;
    private readonly string _statePath;
    public List<PendingRestore> Items { get; private set; } = [];
    public PendingRestoreStore(string localRoot)
    {
        _root = Path.GetFullPath(Path.Combine(localRoot, "pending"));
        _statePath = Path.Combine(_root, "tasks.json");
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (File.Exists(_statePath)) Items = await BackupFiles.ReadJsonAsync<List<PendingRestore>>(_statePath, ct);
        foreach (var item in Items)
        {
            if (item is null || item.Manifest is null || !Enum.IsDefined(item.Status))
                throw new InvalidDataException("待恢复任务记录无效，请先保留本机副本再修正 tasks.json。");
            ValidateManifest(item.Manifest);
            if (!item.Manifest.Locations.Any(l => l.Id == item.LocationId))
                throw new InvalidDataException("待恢复任务指向不存在的存档位置。");
            ArchivePath(item.Manifest);
        }
        CleanupUnreferencedArchives();
    }

    public Task SaveAsync(CancellationToken ct = default) => BackupFiles.WriteJsonAsync(_statePath, Items, ct);

    public string ArchivePath(SaveManifest manifest)
    {
        if (manifest.Sha256.Length != 64 || manifest.Sha256.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("存档包的 SHA-256 无效。");
        return Path.Combine(_root, "archives", manifest.Sha256.ToLowerInvariant() + ".zip");
    }

    public async Task StageAsync(ProviderProfile profile, SaveManifest manifest, string downloadedZip, CancellationToken ct = default,
        IReadOnlyList<Guid>? excludedLocalGameIds = null, Guid? selectedLocalGameId = null)
    {
        ValidateManifest(manifest);
        if (!string.Equals(await BackupFiles.HashAsync(downloadedZip, ct), manifest.Sha256, StringComparison.OrdinalIgnoreCase) ||
            new FileInfo(downloadedZip).Length != manifest.Length)
            throw new InvalidDataException("存档下载校验失败。");
        var destination = ArchivePath(manifest);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!File.Exists(destination) || !string.Equals(await BackupFiles.HashAsync(destination, ct), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var source = File.OpenRead(downloadedZip))
                await using (var target = File.Create(temp)) await source.CopyToAsync(target, ct);
                ct.ThrowIfCancellationRequested();
                AtomicFiles.Commit(temp, destination);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        foreach (var location in manifest.Locations)
        {
            var item = Items.FirstOrDefault(i => i.ProfileId == profile.Id && i.Manifest.ManifestPath == manifest.ManifestPath &&
                                                i.LocationId == location.Id);
            if (item?.IsBusy == true) throw new InvalidOperationException("该存档正在使用中，请先完成或取消恢复任务。");
            if (item == null)
            {
                item = new PendingRestore
                {
                    ProfileId = profile.Id, ProfileName = profile.Name, Manifest = manifest,
                    LocationId = location.Id, Status = RestoreStatus.PendingLink
                };
                Items.Add(item);
            }
            if (excludedLocalGameIds == null) continue;
            item.ExcludedLocalGameIds = excludedLocalGameIds.ToList();
            if (selectedLocalGameId == null || item.LocalGameId != selectedLocalGameId)
            {
                item.LocalGameId = selectedLocalGameId;
                item.TargetOverride = null;
                item.TargetPath = null;
                item.Error = null;
                item.Status = RestoreStatus.PendingLink;
            }
        }
        await SaveAsync(ct);
    }

    public void Refresh(IReadOnlyList<GameIdentity> games, IReadOnlyList<GameMatchMapping> mappings)
    {
        foreach (var pending in Items.Where(i => !i.IsBusy))
        {
            Guid? explicitTarget = mappings.LastOrDefault(m => m.BackupGameId == pending.Manifest.Game.Id)?.LocalGameId;
            var candidates = games.Where(g => !pending.ExcludedLocalGameIds.Contains(g.Id) || g.Id == explicitTarget).ToList();
            var game = GameMatcher.Match(pending.Manifest.Game, candidates, mappings).Game;
            if (pending.LocalGameId != game?.Id)
            {
                pending.TargetOverride = null;
                pending.TargetPath = null;
                pending.Error = null;
                pending.Status = RestoreStatus.PendingLink;
            }
            pending.LocalGameId = game?.Id;
            if (game?.LocalDirectory is not { } root || !Directory.Exists(root))
            {
                pending.Status = RestoreStatus.PendingLink;
                pending.TargetPath = null;
                continue;
            }
            var location = pending.Manifest.Locations.SingleOrDefault(l => l.Id == pending.LocationId);
            pending.TargetPath = location is null ? null : SaveArchiveService.Resolve(location, game, pending.TargetOverride);
            if (pending.TargetPath is null) pending.Status = RestoreStatus.PendingPath;
            else if (pending.Status != RestoreStatus.Restored && pending.Status != RestoreStatus.Failed)
                pending.Status = RestoreStatus.Ready;
        }
    }

    public async Task ClearAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var selected = ids.ToHashSet();
        if (Items.Any(i => selected.Contains(i.Id) && i.IsBusy))
            throw new InvalidOperationException("选中的项目正在使用中，请先完成或取消任务。");
        Items.RemoveAll(i => selected.Contains(i.Id));
        await SaveAsync(ct);
        CleanupUnreferencedArchives();
    }

    private void ValidateManifest(SaveManifest manifest)
    {
        if (manifest.FormatVersion != 1 || manifest.Game is null || manifest.Game.Id == Guid.Empty || manifest.Game.SourceIds is null ||
            manifest.Files is null || manifest.Files.Count == 0 || manifest.Locations is null || manifest.Locations.Count == 0)
            throw new InvalidDataException("存档清单版本或内容无效。");
        var locationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var location in manifest.Locations)
        {
            if (location is null || string.IsNullOrEmpty(location.Id) || !locationIds.Add(location.Id) || location.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') || !Enum.IsDefined(location.BaseKind))
                throw new InvalidDataException("存档位置标识无效。");
            if (location.BaseKind != SaveBaseKind.Custom) BackupFiles.Within(_root, location.RelativePath);
        }
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (file is null) throw new InvalidDataException("存档文件清单无效。");
            BackupFiles.Within(_root, file.Path);
            if ( !unique.Add(file.Path) || file.Length < 0 || string.IsNullOrEmpty(file.Sha256) || file.Sha256.Length != 64 || file.Sha256.Any(c => !Uri.IsHexDigit(c)) ||
                !manifest.Locations.Any(l => file.Path.StartsWith($"roots/{l.Id}/", StringComparison.Ordinal)))
                throw new InvalidDataException("存档文件清单无效。");
        }
    }

    private void CleanupUnreferencedArchives()
    {
        var directory = Path.Combine(_root, "archives");
        if (!Directory.Exists(directory)) return;
        BackupFiles.RejectReparseAncestors(directory);
        var referenced = Items.Select(i => ArchivePath(i.Manifest)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var managedZip = file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && name.Length == 64 && name.All(Uri.IsHexDigit);
            if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || (managedZip && !referenced.Contains(file)))
                File.Delete(file);
        }
    }

    public PendingStats GetStats()
    {
        var files = Directory.Exists(Path.Combine(_root, "archives"))
            ? Directory.EnumerateFiles(Path.Combine(_root, "archives")).Sum(p => new FileInfo(p).Length) : 0;
        return new PendingStats(files, Items.Select(i => i.Manifest.Game.Id).Distinct().Count(),
            Items.Select(i => (i.ProfileId, i.Manifest.ManifestPath)).Distinct().Count(),
            Enum.GetValues<RestoreStatus>().ToDictionary(s => s, s => Items.Count(i => i.Status == s)));
    }
}

public sealed record GameMatchResult(GameIdentity? Game, bool Conflict);

public static class GameMatcher
{
    public static GameMatchResult Match(GameIdentity incoming, IReadOnlyList<GameIdentity> locals,
        IReadOnlyList<GameMatchMapping> mappings)
    {
        if (mappings.LastOrDefault(m => m.BackupGameId == incoming.Id) is { } map)
        {
            var mapped = locals.FirstOrDefault(g => g.Id == map.LocalGameId);
            return new(mapped, mapped is null);
        }
        var sameUuid = locals.FirstOrDefault(g => g.Id == incoming.Id);
        if (sameUuid is not null) return ConflictingIds(incoming, sameUuid) ? new(null, true) : new(sameUuid, false);
        var matches = locals.Where(g => incoming.SourceIds.Any(p =>
            g.SourceIds.TryGetValue(p.Key, out var value) && value == p.Value)).ToList();
        if (matches.Count == 1 && !ConflictingIds(incoming, matches[0])) return new(matches[0], false);
        return new(null, matches.Count > 0);
    }
    private static bool ConflictingIds(GameIdentity a, GameIdentity b) => a.SourceIds.Any(p =>
        b.SourceIds.TryGetValue(p.Key, out var value) && value != p.Value);
}
