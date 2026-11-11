using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PotatoVN.Backup.Core.Engine;

public sealed class SoftwarePackage
{
    public string? DataPath { get; set; }
    public string? MediaPath { get; set; }
    public List<GameIdentity> Games { get; set; } = [];
}

public sealed class SoftwareMergeResult
{
    public string ArchivePath { get; set; } = "";
    public Dictionary<string, JsonNode?> Settings { get; set; } = [];
    public List<string> Conflicts { get; set; } = [];
    public List<string> Notices { get; set; } = [];
}

public sealed class SoftwareGame
{
    public GameIdentity Identity { get; set; } = new();
    public JsonObject Fields { get; set; } = new();
}

public static class SoftwareBackupCodec
{
    private static readonly Dictionary<BackupContent, string[]> GameFields = new()
    {
        [BackupContent.GameInfo] = ["Name", "CnName", "OriginalName", "ChineseName", "Description", "Developer", "Engine", "ExpectedPlayTime", "Rating", "ReleaseDate", "Tags", "LastFetchInfoTime", "AddTime", "Ids", "RssType", "IdForPlugins"],
        [BackupContent.Covers] = ["ImagePath"],
        [BackupContent.Reviews] = ["PlayType", "Comment", "MyRate", "PrivateComment"],
        [BackupContent.PlayTime] = ["TotalPlayTime", "PlayedTime", "PlayCount", "LastPlayTime"],
        [BackupContent.Characters] = ["Characters"],
        [BackupContent.Headers] = ["HeaderImagePath", "HeaderImageUrl"]
    };
    public static IReadOnlyList<string> PortableSettingKeys { get; } = [
        "AppBackgroundRequestedTheme", "language", "backgroundMaterial", "glassAlpha", "glassColor", "backgroundStretchMode", "useBannerAsBackground",
        "sortKeys", "sortKeysAscending", "PrimarySortKey", "primarySortKey", "SecondarySortKey", "PrimarySortAscending", "SecondarySortAscending", "PrimarySortDescending", "SecondarySortDescending",
        "displayPlayTypePolygon", "fixHorizontalPictrue", "displayVirtualGame", "specialDisplayVirtualGame", "multiStreamPageAllowScroll", "timeAsHour", "transparentNavigationView", "showGameNameInControl", "defaultGameName",
        "GalgamePagePrimaryTitleType", "GalgamePageSecondaryTitleType", "galgamePageNewLayout_showPainter", "galgamePageNewLayout_showSeiyu", "galgamePageNewLayout_showWriter", "galgamePageNewLayout_showMusician", "galgamePageNewLayout_showHeaderImage", "galgamePageNewLayout_coverImage", "galgamePageNewLayout_showCoverWhenNoBackground", "galgamePageNewLayout_showExpectedPlayTime", "galgamePageNewLayout_showRating", "galgamePageNewLayout_showTags", "galgamePageNewLayout_showCharacters",
        "homeFilterShowPlayStatusAndSourcePanel", "homeFilterShowEnginePanel", "homeFilterShowDeveloperPanel", "homeFilterShowTagPanel", "libraryNavBar", "libraryStatistics", "LibrarySortKey", "LibraryGameSortDescending", "LibraryFolderSortKey", "LibraryFolderSortDescending",
        "rssType", "overrideLocalName", "overrideLocalNameWithChinese", "syncPlayStatusWhenPhrasing", "fetchCharacters", "mixedPhraserOrder", "mixedPhraserEnabled", "vndbTranslateTags", "vndbCensorTags", "vndbRemoveSpoilerTags",
        "notifyWhenGetGalgameInFolder", "eventGetGalgameInFolderEmpty", "notifyWhenUnpackGame", "eventPvnSyncNotify", "eventPvnSyncEmptyNotify", "closeMode", "minPlayTimeRecordThreshold", "recordOnlyWhenForeground"
    ];
    private static readonly (BackupContent Flag, string Portable, string Native)[] Ancillary = [
        (BackupContent.LibrariesAndCategories, "libraries.json", "data.galgameSources.json"),
        (BackupContent.LibrariesAndCategories, "categories.json", "data.categoryGroups.json"),
        (BackupContent.Staff, "staff.json", "data.staff.json"),
        (BackupContent.Home, "home.json", "data.homepage_list.json")
    ];

    public static async Task<SoftwarePackage> CreateAsync(HostExport export, BackupSettings settings,
        IReadOnlyList<GameIdentity> games, string workDirectory, CancellationToken ct = default, BackupContent? content = null)
    {
        BackupContent selectedContent = content ?? settings.Content;
        var selectedLocal = games.Where(settings.IncludesGame).ToList();
        var canonicalIds = selectedLocal.ToDictionary(g => g.Id, g =>
            settings.GameMappings.LastOrDefault(m => m.LocalGameId == g.Id)?.BackupGameId ?? g.Id);
        var result = new SoftwarePackage
        {
            Games = selectedLocal.Select(g => new GameIdentity
            {
                Id = canonicalIds[g.Id], Name = g.Name, SourceIds = g.SourceIds,
                LocalDirectory = g.LocalDirectory, IsRunning = g.IsRunning
            }).ToList()
        };
        if (result.Games.GroupBy(g => g.Id).Any(group => group.Count() > 1))
            throw new InvalidOperationException("多个本地游戏映射到同一个备份身份，请先修正游戏对应关系。");
        if ((selectedContent & ~BackupContent.Saves) == BackupContent.None) return result;
        var native = Path.Combine(workDirectory, "native");
        var data = Path.Combine(workDirectory, "data");
        var media = Path.Combine(workDirectory, "media");
        Directory.CreateDirectory(data);
        await BackupFiles.ExtractAsync(export.ArchivePath, native, ct);
        var chosen = selectedLocal.Select(g => g.Id).ToHashSet();
        var rawGames = await ReadArrayAsync(Path.Combine(native, "data.galgames.json"), ct);
        var fields = SelectedFields(selectedContent);
        var projected = new List<SoftwareGame>();
        foreach (var raw in rawGames.OfType<JsonObject>())
        {
            if (!Guid.TryParse(raw["Uuid"]?.ToString(), out var id) || !chosen.Contains(id)) continue;
            var item = new SoftwareGame { Identity = result.Games.Single(g => g.Id == canonicalIds[id]) };
            foreach (var key in fields)
                if (raw.TryGetPropertyValue(key, out var value)) item.Fields[key] = value?.DeepClone();
            await CopyReferencedMediaAsync(item.Fields, native, media, null, ct);
            projected.Add(item);
        }
        await BackupFiles.WriteJsonAsync(Path.Combine(data, "package.json"), new { FormatVersion = 1, Content = selectedContent }, ct);
        await BackupFiles.WriteJsonAsync(Path.Combine(data, "games.json"), projected, ct);
        foreach (var (flag, portable, source) in Ancillary)
        {
            if (!selectedContent.HasFlag(flag) || !File.Exists(Path.Combine(native, source))) continue;
            var document = await ReadNodeAsync(Path.Combine(native, source), ct);
            if (document is null) continue;
            StripSensitiveFields(document);
            if (!settings.AllGames || !settings.IncludeLocalGames || !settings.IncludeNonLocalGames) FilterReferences(document, chosen);
            RewriteReferences(document, canonicalIds);
            await CopyReferencedMediaAsync(document, native, media, null, ct);
            await WriteNodeAsync(Path.Combine(data, portable), document, ct);
        }
        if (selectedContent.HasFlag(BackupContent.Settings))
            await BackupFiles.WriteJsonAsync(Path.Combine(data, "settings.json"), export.Settings
                .Where(p => PortableSettingKeys.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value), ct);
        result.DataPath = Path.Combine(workDirectory, "data.zip");
        await BackupFiles.ZipAsync(data, result.DataPath, ct);
        if (Directory.Exists(media) && Directory.EnumerateFiles(media, "*", SearchOption.AllDirectories).Any())
        {
            result.MediaPath = Path.Combine(workDirectory, "media.zip");
            await BackupFiles.ZipAsync(media, result.MediaPath, ct);
        }
        return result;
    }

    public static async Task<SoftwareMergeResult> MergeAsync(HostExport current, string dataZip, string? mediaZip,
        BackupContent selectedContent, BackupSettings settings, IReadOnlyList<GameIdentity> localGames,
        string workDirectory, CancellationToken ct = default)
    {
        var result = new SoftwareMergeResult();
        var native = Path.Combine(workDirectory, "merged");
        var portable = Path.Combine(workDirectory, "incoming");
        var media = Path.Combine(workDirectory, "incoming-media");
        await BackupFiles.ExtractAsync(current.ArchivePath, native, ct);
        await BackupFiles.ExtractAsync(dataZip, portable, ct);
        var package = await ReadNodeAsync(Path.Combine(portable, "package.json"), ct) as JsonObject
            ?? throw new InvalidDataException("软件备份缺少包信息。");
        if (package["FormatVersion"]?.GetValue<int>() != 1) throw new InvalidDataException("不支持的软件备份版本。");
        var included = package["Content"]?.Deserialize<BackupContent>(BackupJson.Options) ?? BackupContent.None;
        selectedContent &= included;
        var matchesGames = RestoreMatchingPreview.RequiresMatching(selectedContent);
        if (matchesGames && mediaZip is not null) await BackupFiles.ExtractAsync(mediaZip, media, ct);
        var nativeGames = matchesGames ? await ReadArrayAsync(Path.Combine(native, "data.galgames.json"), ct) : new JsonArray();
        var incomingGames = matchesGames && File.Exists(Path.Combine(portable, "games.json"))
            ? await BackupFiles.ReadJsonAsync<List<SoftwareGame>>(Path.Combine(portable, "games.json"), ct) : [];
        if (incomingGames.GroupBy(g => g.Identity.Id).Any(group => group.Count() > 1))
        {
            result.Conflicts.Add("这个软件备份包含重复的游戏身份，不能确定恢复目标。");
            return result;
        }
        var decisions = matchesGames ? settings.ActiveRestoreMatching : null;
        if (decisions is not null)
        {
            try { RestoreMatchingPreview.Validate(decisions, incomingGames.Select(g => g.Identity).ToList(), localGames); }
            catch (InvalidOperationException ex) { result.Conflicts.Add(ex.Message); return result; }
        }
        var removedIds = decisions?.Merges.Select(m => m.RemovedGameId).ToHashSet() ?? [];
        // 自动匹配只能查恢复前的条目；同批新建的游戏不能成为后续云端条目的目标。
        var originalGames = localGames.Where(g => !removedIds.Contains(g.Id)).ToList();
        var uuidMap = new Dictionary<Guid, Guid>();
        var learnedMappings = new List<GameMatchMapping>();
        var destinations = new List<(SoftwareGame Item, GameIdentity? Existing, Guid Id)>();
        foreach (var item in incomingGames)
        {
            GameIdentity? existing;
            if (decisions is not null)
                existing = decisions.Targets[item.Identity.Id] is { } targetId ? originalGames.Single(g => g.Id == targetId) : null;
            else
            {
                var match = GameMatcher.Match(item.Identity, originalGames, settings.GameMappings);
                if (match.Conflict)
                {
                    result.Conflicts.Add($"{item.Identity.Name}（{item.Identity.Id}）：来源标识冲突或匹配到多个游戏，请手动映射。");
                    continue;
                }
                existing = match.Game;
            }
            if (existing is null && !selectedContent.HasFlag(BackupContent.GameInfo))
            {
                result.Notices.Add(item.Identity.Name + "：没有匹配条目，未选游戏信息，因此未创建新游戏。");
                continue;
            }
            var id = existing?.Id ?? item.Identity.Id;
            if (existing is null && !removedIds.Contains(id) &&
                (localGames.Any(g => g.Id == id) || nativeGames.OfType<JsonObject>().Any(g => Guid.TryParse(g["Uuid"]?.ToString(), out var parsed) && parsed == id)))
            {
                result.Conflicts.Add($"{item.Identity.Name}（{id}）：不关联已有条目时，新条目的身份已被本地游戏占用。");
                continue;
            }
            destinations.Add((item, existing, id));
        }
        foreach (var duplicate in destinations.GroupBy(d => d.Id).Where(group => group.Count() > 1))
            result.Conflicts.Add($"{string.Join("、", duplicate.Select(d => d.Item.Identity.Name))}：同一快照中的多个云端条目指向同一个本地游戏，请分别选择目标。");
        // 不执行部分含冲突的导入，也不在冲突时改变待映射配置。
        if (result.Conflicts.Count > 0) return result;
        if (matchesGames) await ApplyLocalMergesAsync(native, nativeGames, settings, ct);
        var usableGames = originalGames.ToList();
        var imageNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (item, existing, id) in destinations)
        {
            if (existing is not null && existing.Id != item.Identity.Id &&
                settings.GameMappings.LastOrDefault(m => m.BackupGameId == item.Identity.Id)?.LocalGameId != existing.Id)
                learnedMappings.Add(new GameMatchMapping { BackupGameId = item.Identity.Id, LocalGameId = existing.Id });
            uuidMap[item.Identity.Id] = id;
            var target = nativeGames.OfType<JsonObject>().FirstOrDefault(g => Guid.TryParse(g["Uuid"]?.ToString(), out var parsed) && parsed == id);
            if (target is null)
            {
                target = new JsonObject { ["Uuid"] = id.ToString() };
                nativeGames.Add(target);
                usableGames.Add(new GameIdentity { Id = id, Name = item.Identity.Name, SourceIds = item.Identity.SourceIds });
            }
            foreach (var field in SelectedFields(selectedContent))
            {
                if (!item.Fields.TryGetPropertyValue(field, out var value)) continue;
                var wrapper = new JsonObject { [field] = value?.DeepClone() };
                await CopyReferencedMediaAsync(wrapper, media, native, imageNames, ct);
                target[field] = wrapper[field]?.DeepClone();
            }
        }
        settings.GameMappings.AddRange(learnedMappings);
        if (matchesGames) await WriteNodeAsync(Path.Combine(native, "data.galgames.json"), nativeGames, ct);
        foreach (var (flag, portableName, nativeName) in Ancillary)
        {
            if (!selectedContent.HasFlag(flag) || !File.Exists(Path.Combine(portable, portableName))) continue;
            var incoming = await ReadNodeAsync(Path.Combine(portable, portableName), ct);
            if (incoming is null) continue;
            StripSensitiveFields(incoming);
            ValidateNativeTypeNames(incoming);
            // 未创建资料且没有选定本地目标的条目，不应把分类等关系套到同 UUID 的本地游戏。
            FilterReferences(incoming, uuidMap.Keys.ToHashSet());
            RewriteReferences(incoming, uuidMap);
            if (portableName == "libraries.json")
            {
                foreach (var source in AsArray(incoming).OfType<JsonObject>())
                {
                    if (string.IsNullOrWhiteSpace(source["Path"]?.ToString())) continue;
                    source.Remove("_DataBackupImages");
                    var embedded = new JsonObject();
                    foreach (var image in EnumerateImageReferences(source).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        var file = BackupFiles.Within(media, image);
                        if (File.Exists(file)) embedded[image] = Convert.ToBase64String(await File.ReadAllBytesAsync(file, ct));
                    }
                    if (embedded.Count > 0) source["_DataBackupImages"] = embedded;
                    var id = source["Id"]?.ToString() ?? Guid.NewGuid().ToString();
                    var saved = settings.LibraryMappings.FirstOrDefault(m => m.Id == id);
                    var mapping = new PendingLibraryMapping
                    {
                        Id = id, Name = source["Name"]?.ToString() ?? "游戏库", OriginalPath = source["Path"]?.ToString() ?? "",
                        SourceJson = source.ToJsonString(), TargetPath = saved?.TargetPath
                    };
                    if (saved is not null) settings.LibraryMappings.Remove(saved);
                    settings.LibraryMappings.Add(mapping);
                }
                result.Notices.Add("原游戏库已保存为待映射配置；未根据旧电脑路径建立本地关联。");
                continue;
            }
            await CopyReferencedMediaAsync(incoming, media, native, imageNames, ct);
            var path = Path.Combine(native, nativeName);
            if (portableName is "categories.json" or "staff.json")
            {
                var old = await ReadArrayAsync(path, ct);
                foreach (var entry in AsArray(incoming).OfType<JsonObject>())
                {
                    var id = entry["Id"]?.ToString();
                    var existing = old.OfType<JsonObject>().FirstOrDefault(o => o["Id"]?.ToString() == id);
                    if (existing is not null) MergeEntry(existing, entry, uuidMap.Values.ToHashSet());
                    else old.Add(entry.DeepClone());
                }
                await WriteNodeAsync(path, old, ct);
            }
            else await WriteNodeAsync(path, incoming, ct);
        }
        if (selectedContent.HasFlag(BackupContent.Settings) && File.Exists(Path.Combine(portable, "settings.json")))
        {
            var incoming = await BackupFiles.ReadJsonAsync<Dictionary<string, JsonNode?>>(Path.Combine(portable, "settings.json"), ct);
            result.Settings = incoming.Where(p => PortableSettingKeys.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        }
        if (matchesGames) await ApplyMappingsToNativeAsync(native, settings, usableGames, result, ct);
        result.ArchivePath = Path.Combine(workDirectory, "PotatoVN_DataBackup.pvnExport.zip");
        await BackupFiles.ZipAsync(native, result.ArchivePath, ct);
        return result;
    }

    public static async Task<SoftwareMergeResult> MergeLocalGamesAsync(HostExport current,
        BackupSettings settings, IReadOnlyList<GameIdentity> localGames, string workDirectory, CancellationToken ct = default)
    {
        var result = new SoftwareMergeResult();
        if (settings.ActiveRestoreMatching is { } decisions)
        {
            var identities = decisions.Targets.Keys.Select(id => new GameIdentity { Id = id }).ToList();
            try { RestoreMatchingPreview.Validate(decisions, identities, localGames); }
            catch (InvalidOperationException ex) { result.Conflicts.Add(ex.Message); return result; }
        }
        var native = Path.Combine(workDirectory, "merged-local");
        await BackupFiles.ExtractAsync(current.ArchivePath, native, ct);
        var nativeGames = await ReadArrayAsync(Path.Combine(native, "data.galgames.json"), ct);
        await ApplyLocalMergesAsync(native, nativeGames, settings, ct);
        await WriteNodeAsync(Path.Combine(native, "data.galgames.json"), nativeGames, ct);
        result.ArchivePath = Path.Combine(workDirectory, "PotatoVN_MergedGames.pvnExport.zip");
        await BackupFiles.ZipAsync(native, result.ArchivePath, ct);
        return result;
    }

    private static async Task ApplyLocalMergesAsync(string native, JsonArray nativeGames,
        BackupSettings settings, CancellationToken ct)
    {
        if (settings.ActiveRestoreMatching is not { Merges.Count: > 0 } decisions) return;
        var mapping = decisions.Merges.ToDictionary(m => m.RemovedGameId, m => m.KeptGameId);
        for (var i = nativeGames.Count - 1; i >= 0; i--)
            if (nativeGames[i] is JsonObject game && Guid.TryParse(game["Uuid"]?.ToString(), out var id) && mapping.ContainsKey(id))
                nativeGames.RemoveAt(i);
        foreach (var (_, _, file) in Ancillary)
        {
            var path = Path.Combine(native, file);
            if (!File.Exists(path)) continue;
            var document = await ReadNodeAsync(path, ct);
            if (document is null) continue;
            RewriteMergedReferences(document, mapping);
            await WriteNodeAsync(path, document, ct);
        }
        foreach (var deferred in settings.LibraryMappings)
        {
            var document = JsonNode.Parse(deferred.SourceJson) ?? throw new InvalidDataException("待映射库配置损坏。");
            RewriteMergedReferences(document, mapping);
            deferred.SourceJson = document.ToJsonString();
        }
    }

    private static void RewriteMergedReferences(JsonNode node, Dictionary<Guid, Guid> mapping)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj.ToList())
                if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text) &&
                    Guid.TryParse(text, out var id) && mapping.TryGetValue(id, out var target)) obj[pair.Key] = target.ToString();
                else if (pair.Value is not null) RewriteMergedReferences(pair.Value, mapping);
        }
        else if (node is JsonArray array)
        {
            var originalIds = array.Select(GameReferenceId).ToList();
            var positions = new Dictionary<Guid, int>();
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue value && value.TryGetValue<string>(out var text) &&
                    Guid.TryParse(text, out var old) && mapping.TryGetValue(old, out var target)) array[i] = target.ToString();
                else if (array[i] is { } child) RewriteMergedReferences(child, mapping);
                if (GameReferenceId(array[i]) is not { } id || !mapping.ContainsValue(id)) continue;
                if (!positions.TryGetValue(id, out var previous)) { positions[id] = i; continue; }
                // 两条关联原本同时存在时，优先保留目标条目的路径等本机信息。
                var preferCurrent = originalIds[i] == id && originalIds[previous] != id;
                var kept = preferCurrent ? array[i] : array[previous];
                var discarded = preferCurrent ? array[previous] : array[i];
                if (kept is JsonObject keptObject && discarded is JsonObject discardedObject &&
                    keptObject["Relation"] is JsonArray relations && discardedObject["Relation"] is JsonArray otherRelations)
                    foreach (var relation in otherRelations)
                        if (!relations.Any(existing => JsonNode.DeepEquals(existing, relation))) relations.Add(relation?.DeepClone());
                if (preferCurrent)
                {
                    array[previous] = kept?.DeepClone();
                    originalIds[previous] = originalIds[i];
                }
                array.RemoveAt(i);
                originalIds.RemoveAt(i--);
            }
        }
    }

    private static Guid? GameReferenceId(JsonNode? node)
    {
        var value = node is JsonObject obj ? obj["Galgame"] ?? obj["GameId"] ?? obj["GalgameId"] ?? obj["$guid"] : node;
        if (value is JsonObject guidObject) value = guidObject["$guid"];
        return Guid.TryParse(value?.ToString(), out var id) ? id : null;
    }

    public static async Task<SoftwareMergeResult> ApplyLibraryMappingsAsync(HostExport current,
        BackupSettings settings, IReadOnlyList<GameIdentity> games, string workDirectory, CancellationToken ct = default)
    {
        var result = new SoftwareMergeResult();
        var native = Path.Combine(workDirectory, "mapped-native");
        await BackupFiles.ExtractAsync(current.ArchivePath, native, ct);
        await ApplyMappingsToNativeAsync(native, settings, games, result, ct);
        result.ArchivePath = Path.Combine(workDirectory, "PotatoVN_MappedLibrary.pvnExport.zip");
        await BackupFiles.ZipAsync(native, result.ArchivePath, ct);
        return result;
    }

    private static async Task ApplyMappingsToNativeAsync(string native, BackupSettings settings,
        IReadOnlyList<GameIdentity> games, SoftwareMergeResult result, CancellationToken ct)
    {
        var path = Path.Combine(native, "data.galgameSources.json");
        var sources = await ReadArrayAsync(path, ct);
        foreach (var mapping in settings.LibraryMappings.Where(m => !string.IsNullOrWhiteSpace(m.TargetPath)))
        {
            if (!Directory.Exists(mapping.TargetPath))
            {
                result.Notices.Add(mapping.Name + "：目标库目录不存在，尚未关联。");
                continue;
            }
            var incoming = JsonNode.Parse(mapping.SourceJson) as JsonObject ?? throw new InvalidDataException("待映射库配置损坏。");
            ValidateNativeTypeNames(incoming);
            if (incoming["_DataBackupImages"] is JsonObject embedded)
            {
                foreach (var image in embedded)
                {
                    var renamed = "Images/pvn_library_" + Guid.NewGuid().ToString("N") + Path.GetExtension(image.Key);
                    var imagePath = BackupFiles.Within(native, renamed);
                    Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
                    await File.WriteAllBytesAsync(imagePath, Convert.FromBase64String(image.Value!.GetValue<string>()), ct);
                    ReplaceImageReference(incoming, image.Key, renamed);
                }
                incoming.Remove("_DataBackupImages");
            }
            incoming["Path"] = Path.GetFullPath(mapping.TargetPath!);
            incoming["ScanOnStart"] = false;
            incoming["Detect"] = false;
            var memberships = incoming["Galgames"] is JsonArray array ? array : new JsonArray();
            var kept = new JsonArray();
            foreach (var member in memberships.OfType<JsonObject>())
            {
                if (!Guid.TryParse(member["Galgame"]?.ToString(), out var gameId)) continue;
                var game = games.FirstOrDefault(g => g.Id == gameId);
                if (game is null || (game.LocalDirectory is { } linked && Directory.Exists(linked))) continue;
                var original = member["Path"]?.ToString();
                if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(mapping.OriginalPath)) continue;
                var relative = Path.GetRelativePath(mapping.OriginalPath, original);
                var target = relative == "." ? Path.GetFullPath(mapping.TargetPath!) : BackupFiles.Within(mapping.TargetPath!, relative);
                if (!Directory.Exists(target)) continue;
                var copy = (JsonObject)member.DeepClone();
                copy["Path"] = target;
                kept.Add(copy);
            }
            incoming["Galgames"] = kept;
            await CopyReferencedMediaAsync(incoming, native, native, null, ct);
            var existing = sources.OfType<JsonObject>().FirstOrDefault(s => s["Id"]?.ToString() == mapping.Id);
            if (existing is not null)
            {
                if (existing["Path"]?.ToString() == incoming["Path"]?.ToString())
                {
                    existing["Name"] = incoming["Name"]?.DeepClone();
                    existing["ImagePath"] = incoming["ImagePath"]?.DeepClone();
                    var currentMembers = existing["Galgames"] as JsonArray ?? new JsonArray();
                    foreach (var member in kept.OfType<JsonObject>())
                        if (!currentMembers.OfType<JsonObject>().Any(m => m["Galgame"]?.ToString() == member["Galgame"]?.ToString()))
                            currentMembers.Add(member.DeepClone());
                    if (existing["Galgames"] is null) existing["Galgames"] = currentMembers;
                    continue;
                }
                incoming["Id"] = Guid.NewGuid().ToString();
            }
            sources.Add(incoming);
        }
        await WriteNodeAsync(path, sources, ct);
    }

    private static HashSet<string> SelectedFields(BackupContent content) => GameFields
        .Where(p => content.HasFlag(p.Key)).SelectMany(p => p.Value).ToHashSet(StringComparer.Ordinal);

    private static async Task<JsonArray> ReadArrayAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return new JsonArray();
        return AsArray(await ReadNodeAsync(path, ct));
    }
    private static JsonArray AsArray(JsonNode? node) => node as JsonArray ?? node?["$values"] as JsonArray ?? new JsonArray();
    private static async Task<JsonNode?> ReadNodeAsync(string path, CancellationToken ct) =>
        JsonNode.Parse(await File.ReadAllTextAsync(path, ct));
    private static Task WriteNodeAsync(string path, JsonNode node, CancellationToken ct) =>
        File.WriteAllTextAsync(path, node.ToJsonString(BackupJson.Options), ct);

    private static async Task CopyReferencedMediaAsync(JsonNode node, string sourceRoot, string destinationRoot,
        Dictionary<string, string>? rename, CancellationToken ct)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj.ToList())
            {
                if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text) &&
                    text.Replace('\\', '/').TrimStart('.', '/').StartsWith("Images/", StringComparison.OrdinalIgnoreCase))
                {
                    var relative = text.Replace('\\', '/');
                    if (relative.StartsWith("./", StringComparison.Ordinal)) relative = relative[2..];
                    var source = BackupFiles.Within(sourceRoot, relative);
                    if (!File.Exists(source)) continue;
                    var name = relative;
                    if (rename is not null)
                    {
                        if (!rename.TryGetValue(relative, out name))
                            rename[relative] = name = "Images/pvn_" + Guid.NewGuid().ToString("N") + Path.GetExtension(relative);
                        obj[pair.Key] = "./" + name;
                    }
                    var destination = BackupFiles.Within(destinationRoot, name);
                    if (!string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        if (!File.Exists(destination))
                        {
                            await using var input = File.OpenRead(source);
                            await using var output = File.Create(destination);
                            await input.CopyToAsync(output, ct);
                        }
                    }
                }
                else if (pair.Value is not null) await CopyReferencedMediaAsync(pair.Value, sourceRoot, destinationRoot, rename, ct);
            }
        }
        else if (node is JsonArray array)
            foreach (var child in array.OfType<JsonNode>()) await CopyReferencedMediaAsync(child, sourceRoot, destinationRoot, rename, ct);
    }

    private static IEnumerable<string> EnumerateImageReferences(JsonNode node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            var relative = text.Replace('\\', '/');
            if (relative.StartsWith("./", StringComparison.Ordinal)) relative = relative[2..];
            if (relative.StartsWith("Images/", StringComparison.OrdinalIgnoreCase)) yield return relative;
        }
        else if (node is JsonObject obj)
            foreach (var child in obj.Select(p => p.Value).OfType<JsonNode>())
                foreach (var reference in EnumerateImageReferences(child)) yield return reference;
        else if (node is JsonArray array)
            foreach (var child in array.OfType<JsonNode>())
                foreach (var reference in EnumerateImageReferences(child)) yield return reference;
    }
    private static void FilterReferences(JsonNode node, HashSet<Guid> chosen)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj.ToList())
            {
                if (pair.Value is JsonArray array && pair.Key is "Galgames" or "GalgamesX" or "GalgameIds" or "GameIds" or "Games")
                {
                    for (var i = array.Count - 1; i >= 0; i--)
                    {
                        var idText = array[i] is JsonObject entry
                            ? entry["Galgame"]?.ToString() ?? entry["GameId"]?.ToString() ?? entry["GalgameId"]?.ToString() ?? entry["$guid"]?.ToString()
                            : array[i]?.ToString();
                        if (Guid.TryParse(idText, out var id) && !chosen.Contains(id)) array.RemoveAt(i);
                    }
                }
                if (pair.Value is not null) FilterReferences(pair.Value, chosen);
            }
        }
        else if (node is JsonArray array) foreach (var child in array.OfType<JsonNode>()) FilterReferences(child, chosen);
    }

    private static void RewriteReferences(JsonNode node, Dictionary<Guid, Guid> mapping)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj.ToList())
            {
                if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text) &&
                    Guid.TryParse(text, out var id) && mapping.TryGetValue(id, out var target)) obj[pair.Key] = target.ToString();
                else if (pair.Value is not null) RewriteReferences(pair.Value, mapping);
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
                if (array[i] is JsonValue value && value.TryGetValue<string>(out var text) &&
                    Guid.TryParse(text, out var id) && mapping.TryGetValue(id, out var target)) array[i] = target.ToString();
                else if (array[i] is { } child) RewriteReferences(child, mapping);
        }
    }

    private static void ReplaceImageReference(JsonNode node, string oldPath, string newPath)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj.ToList())
                if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text) &&
                    text.Replace('\\', '/').TrimStart('.', '/') == oldPath) obj[pair.Key] = "./" + newPath;
                else if (pair.Value is not null) ReplaceImageReference(pair.Value, oldPath, newPath);
        }
        else if (node is JsonArray array) foreach (var child in array.OfType<JsonNode>()) ReplaceImageReference(child, oldPath, newPath);
    }

    private static void MergeEntry(JsonObject target, JsonObject incoming, HashSet<Guid> gameScope)
    {
        foreach (var property in incoming)
        {
            if (property.Value is JsonArray items && target[property.Key] is JsonArray old)
            {
                if (property.Key == "Categories")
                {
                    foreach (var category in items.OfType<JsonObject>())
                    {
                        var existing = old.OfType<JsonObject>().FirstOrDefault(o => o["Id"]?.ToString() == category["Id"]?.ToString());
                        if (existing is null) old.Add(category.DeepClone()); else MergeEntry(existing, category, gameScope);
                    }
                    continue;
                }
                if (property.Key is "Games" or "GalgamesX" or "Galgames" or "GalgameIds" or "GameIds")
                {
                    var merged = new JsonArray();
                    foreach (var item in old)
                    {
                        var id = item is JsonObject obj ? obj["Galgame"]?.ToString() ?? obj["GameId"]?.ToString() : item?.ToString();
                        if (!Guid.TryParse(id, out var guid) || !gameScope.Contains(guid)) merged.Add(item?.DeepClone());
                    }
                    foreach (var item in items) merged.Add(item?.DeepClone());
                    target[property.Key] = merged;
                    continue;
                }
            }
            target[property.Key] = property.Value?.DeepClone();
        }
    }

    private static void StripSensitiveFields(JsonNode node)
    {
        string[] forbidden = ["Password", "AccessToken", "RefreshToken", "Token", "ClientSecret", "Credentials", "ProtectedCredentials", "BangumiAccount", "VndbAccount", "PvnAccount", "Authorization", "Cookie", "Cookies", "RequirePvnSync", "PvnUpdate", "PvnUploadProperties", "ExtraData"];
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(p => p.Key).Where(k => forbidden.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList()) obj.Remove(key);
            foreach (var child in obj.Select(p => p.Value).OfType<JsonNode>()) StripSensitiveFields(child);
        }
        else if (node is JsonArray array) foreach (var child in array.OfType<JsonNode>()) StripSensitiveFields(child);
    }

    private static void ValidateNativeTypeNames(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj["_type"] is JsonValue dtoType && dtoType.TryGetValue<string>(out var dtoName))
            {
                string[] dtoNames = ["GalgameManager.MultiStreamPage.Lists.GameListDbDto", "GalgameManager.MultiStreamPage.Lists.CategoryListDbDto", "GalgameManager.MultiStreamPage.Lists.SourceListDbDto"];
                if (!dtoNames.Contains(dtoName.Split(',')[0]) || !dtoName.Contains(", GalgameManager", StringComparison.Ordinal))
                    throw new InvalidDataException("主页备份包含不支持的DTO类型：" + dtoName);
            }
            if (obj["$type"] is JsonValue value && value.TryGetValue<string>(out var type))
            {
                // 原生主页数据会携带集合和宿主类型；禁止其他程序集类型进入宿主的类型反序列化入口。
                if (type.StartsWith("System.Collections.Generic.List`", StringComparison.Ordinal) ||
                    type.StartsWith("System.Collections.ObjectModel.ObservableCollection`", StringComparison.Ordinal))
                    obj.Remove("$type");
                else if (!type.StartsWith("GalgameManager.", StringComparison.Ordinal) || !type.Contains(", GalgameManager", StringComparison.Ordinal))
                    throw new InvalidDataException("备份包含不允许的原生类型：" + type);
            }
            foreach (var child in obj.Select(p => p.Value).OfType<JsonNode>().ToList()) ValidateNativeTypeNames(child);
        }
        else if (node is JsonArray array) foreach (var child in array.OfType<JsonNode>()) ValidateNativeTypeNames(child);
    }
}
