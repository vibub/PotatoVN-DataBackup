using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using GalgameManager.Models;
using GalgameManager.Models.Sources;
using PotatoVN.App.PluginBase.BackupUi;
using PotatoVN.Backup.Core;
using PotatoVN.Backup.Core.Engine;

namespace PotatoVN.App.PluginBase.Backup;

public sealed partial class PotatoHostAdapter
{
    private object SourceService() => Service("GalgameManager.Contracts.Services.IGalgameSourceCollectionService");

    private static IEnumerable<GalgameSourceBase> GetSources(object service) =>
        (IEnumerable<GalgameSourceBase>)service.GetType().GetMethod("GetGalgameSources")!.Invoke(service, null)!;

    public IReadOnlyList<LocalLibrary> GetLocalLibraries() => OnMainAsync<IReadOnlyList<LocalLibrary>>(() =>
        GetSources(SourceService()).Where(source => source.SourceType is GalgameSourceType.LocalFolder or
                GalgameSourceType.LocalZip or GalgameSourceType.Steam)
            .Where(source => Directory.Exists(source.Path))
            .Select(source => new LocalLibrary(source.Id, source.Name, source.Path, (LibraryKind)source.SourceType))
            .OrderBy(library => library.Name, StringComparer.CurrentCultureIgnoreCase).ToArray()).GetAwaiter().GetResult();

    public Task<(LocalLibrary Library, int Linked)> ApplyLibraryMappingAsync(PendingLibraryMapping mapping,
        string targetRoot, Guid? targetLibraryId, CancellationToken ct) => OnMainAsync(async () =>
    {
        ct.ThrowIfCancellationRequested();
        if (Directory.GetFiles(DataRoot, "*.pvnExport.zip").Length != 0)
            throw new InvalidOperationException("已有待导入的软件数据，请先完成或取消该导入。");
        var incoming = JsonNode.Parse(mapping.SourceJson) as JsonObject ?? throw new InvalidDataException("待关联游戏库配置损坏。");
        LibraryKind kind = LibraryAssociation.GetKind(incoming) ?? throw new InvalidOperationException("这个游戏库类型不支持目录关联。");
        var type = (GalgameSourceType)kind;
        object service = SourceService();
        Type serviceType = service.GetType();
        MethodInfo save = serviceType.GetMethod("Save", [typeof(GalgameSourceBase)])!;
        void Save(GalgameSourceBase source) => save.Invoke(service, [source]);
        GalgameSourceBase[] sources = GetSources(service).ToArray();
        GalgameSourceBase? target = targetLibraryId is { } id
            ? sources.SingleOrDefault(source => source.Id == id && source.SourceType == type)
                ?? throw new InvalidOperationException("所选游戏库已不存在，请刷新后重选。")
            : sources.FirstOrDefault(source => source.SourceType == type && LibraryAssociation.SamePath(source.Path, targetRoot));
        string root = target?.Path ?? targetRoot;
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            throw new DirectoryNotFoundException("请选择这台电脑上已存在的游戏库文件夹。");
        root = Path.GetFullPath(root);

        var comparable = sources.Where(source => kind == LibraryKind.Archive
            ? source.SourceType == type : source.SourceType is GalgameSourceType.LocalFolder or GalgameSourceType.Steam).ToArray();
        var occupied = comparable.SelectMany(source => source.Galgames)
            .Where(member => LibraryAssociation.Exists(member.Path, kind))
            .ToLookup(member => Path.TrimEndingDirectorySeparator(Path.GetFullPath(member.Path)),
                member => member.Galgame.Uuid, StringComparer.OrdinalIgnoreCase);
        var games = _api.GetAllGames().ToDictionary(game => game.Uuid);
        var running = GetGames().Where(game => game.IsRunning).Select(game => game.Id).ToHashSet();
        var additions = new Dictionary<Galgame, (string Path, GalgameSourceBase[] Stale)>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonObject member in LibraryAssociation.Members(incoming))
        {
            if (!games.TryGetValue(LibraryAssociation.GameId(member), out Galgame? game) || additions.ContainsKey(game)) continue;
            string? currentPath = target?.GetPath(game);
            bool inTarget = LibraryAssociation.Exists(currentPath, kind);
            GalgameSourceBase[] local = game.Sources.Where(source => source.SourceType is GalgameSourceType.LocalFolder or GalgameSourceType.Steam).ToArray();
            GalgameSourceBase[] stale = kind == LibraryKind.Archive ? [] : local
                .Where(source => source != target && !Directory.Exists(source.GetPath(game))).ToArray();
            if (inTarget && stale.Length == 0) continue;
            if (!inTarget && kind != LibraryKind.Archive &&
                local.Any(source => source != target && Directory.Exists(source.GetPath(game)))) continue;
            string? path = inTarget ? currentPath : LibraryAssociation.ResolveMember(mapping.OriginalPath, member["Path"]?.ToString(), root, kind);
            if (path == null) continue;
            string normalized = Path.TrimEndingDirectorySeparator(path);
            if (occupied[normalized].Any(owner => owner != game.Uuid) || !paths.Add(normalized)) continue;
            if (running.Contains(game.Uuid)) throw new InvalidOperationException($"请先退出正在运行的“{game.Name.Value}”，再关联游戏库。");
            additions.Add(game, (path, stale));
        }
        ct.ThrowIfCancellationRequested();

        if (target == null)
        {
            MethodInfo add = serviceType.GetMethod("AddGalgameSourceAsync")!;
            target = await (Task<GalgameSourceBase>)add.Invoke(service, [type, root, false, false])!;
            target.Name = mapping.Name;
            target.ImagePath = RestoreLibraryCover(incoming, target.Id);
            Save(target);
        }
        foreach (var (game, (path, stale)) in additions)
        {
            SaveMembership(target, game, path, Save);
            foreach (GalgameSourceBase oldSource in stale)
                SaveMembership(oldSource, game, null, Save);
        }
        return (new LocalLibrary(target.Id, target.Name, target.Path, kind), additions.Count);
    }).Unwrap();

    private static void SaveMembership(GalgameSourceBase source, Galgame game, string? path, Action<GalgameSourceBase> save)
    {
        string? previous = source.GetPath(game);
        if (string.Equals(previous, path, StringComparison.OrdinalIgnoreCase)) return;
        if (previous != null) source.DeleteGalgame(game);
        if (path != null) source.AddGalgame(game, path);
        try { save(source); }
        catch
        {
            // 保留这条关系的原状态，下次确认可以重试。
            if (path != null) source.DeleteGalgame(game);
            if (previous != null) source.AddGalgame(game, previous);
            throw;
        }
    }

    private string? RestoreLibraryCover(JsonObject incoming, Guid libraryId)
    {
        string? reference = incoming["ImagePath"]?.ToString().Replace('\\', '/');
        if (reference?.StartsWith("./", StringComparison.Ordinal) == true) reference = reference[2..];
        if (reference == null || incoming["_DataBackupImages"] is not JsonObject images ||
            images[reference] is not JsonValue image || !image.TryGetValue<string>(out string? content)) return null;
        string name = $"Images/pvn_library_{libraryId:N}" + Path.GetExtension(reference);
        string path = BackupFiles.Within(DataRoot, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Convert.FromBase64String(content));
        return path;
    }
}
