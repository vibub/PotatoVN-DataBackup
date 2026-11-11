using System.Text.Json.Nodes;
using PotatoVN.Backup.Core;
using PotatoVN.Backup.Core.Engine;

internal static partial class EngineTests
{
    private static async Task BackupSelectionSettingsAsync()
    {
        string root = Work();
        await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), "{\"Content\":4,\"AllGames\":false,\"SelectedGameIds\":[]}");
        var store = new SettingsStore(root);
        BackupSettings settings = await store.LoadAsync();
        Check(settings.IncludeLocalGames && settings.IncludeNonLocalGames && !settings.AllGames &&
            settings.AutomaticContent == null && settings.GetBackupContent(true) == BackupContent.Reviews, "旧配置未沿用原有选择。");
        settings.AutomaticContent = BackupContent.Saves;
        settings.IncludeNonLocalGames = false;
        await store.SaveAsync(settings);
        BackupSettings loaded = await store.LoadAsync();
        Check(loaded.Content == BackupContent.Reviews && loaded.GetBackupContent(true) == BackupContent.Saves &&
            loaded.IncludeLocalGames && !loaded.IncludeNonLocalGames, "独立内容或游戏范围未持久化。");
        settings.AutomaticContent = (BackupContent)4096;
        await store.SaveAsync(settings);
        await Throws<InvalidDataException>(() => store.LoadAsync());
    }

    private static async Task GameScopeProjectionAsync()
    {
        GameIdentity local = Game(directory: Work()), remote = Game(), removed = Game(directory: Path.Combine(Work(), "uninstalled"));
        GameIdentity[] games = [local, remote, removed];
        var group = new JsonArray(new JsonObject { ["Galgames"] = new JsonArray(games.Select(game => (JsonNode?)JsonValue.Create(game.Id.ToString())).ToArray()) });
        HostExport export = await NativeAsync(games.Select(game => NativeGame(game)), group);
        var settings = Settings(BackupContent.GameInfo | BackupContent.Covers | BackupContent.LibrariesAndCategories);
        foreach (bool includeLocal in new[] { true, false })
        {
            settings.IncludeLocalGames = includeLocal;
            settings.IncludeNonLocalGames = !includeLocal;
            SoftwarePackage package = await SoftwareBackupCodec.CreateAsync(export, settings, games, Work());
            Guid[] expected = (includeLocal ? new[] { local } : new[] { remote, removed }).Select(game => game.Id).ToArray();
            Check(package.Games.Select(game => game.Id).Order().SequenceEqual(expected.Order()), "范围包含了错误的游戏身份。");
            JsonArray projected = ReadEntry(package.DataPath!, "games.json").AsArray();
            Check(projected.Count == expected.Length, "资料范围未实际过滤。");
            string categories = ReadEntry(package.DataPath!, "categories.json").ToJsonString();
            foreach (GameIdentity game in games)
                Check(categories.Contains(game.Id.ToString()) == expected.Contains(game.Id), "分类引用泄露了范围外的游戏。");
            Check(package.MediaPath != null, "范围内封面未备份。");
        }
        settings.IncludeLocalGames = settings.IncludeNonLocalGames = true;
        settings.AllGames = false;
        settings.SelectedGameIds = [local.Id, remote.Id];
        settings.IncludeLocalGames = false;
        Check(games.Where(settings.IncludesGame).Select(game => game.Id).SequenceEqual([remote.Id]), "指定游戏与类型范围未取交集。");
        settings.IncludeNonLocalGames = false;
        Check(!games.Any(settings.IncludesGame), "都未勾选时错误备份了全部游戏。");
    }

    private static async Task AutomaticBackupContentAsync()
    {
        var fixture = await SaveFixtureAsync();
        var setup = Coordinator(BackupContent.Reviews | BackupContent.Covers, fixture.Game);
        setup.Settings.AutomaticContent = BackupContent.Saves;
        setup.Settings.SaveConfigs = [new() { GameId = fixture.Game.Id, Locations = [fixture.Location] }];
        SnapshotManifest automatic = await setup.Coordinator.BackupAsync(automatic: true);
        Check(automatic.Content == BackupContent.Saves && automatic.DataRecipe == null && automatic.MediaRecipe == null &&
            automatic.SaveManifestPaths.Count == 1, "自动存档备份携带了手动资料或图片。");
        SnapshotManifest manual = await setup.Coordinator.BackupAsync();
        Check(manual.Content == setup.Settings.Content && manual.DataRecipe != null && manual.MediaRecipe != null &&
            manual.SaveManifestPaths.Count == 0 && setup.Settings.AutomaticContent == BackupContent.Saves, "手动备份使用或改写了自动内容。");
        setup.Settings.AutomaticContent = BackupContent.PlayTime;
        SnapshotManifest playTime = await setup.Coordinator.BackupAsync(automatic: true);
        Check(playTime.Content == BackupContent.PlayTime && playTime.MediaRecipe == null && playTime.SaveManifestPaths.Count == 0,
            "自动资料备份未过滤图片或存档。");
        string archive = Path.Combine(Work(), "automatic.zip");
        await new IncrementalArchiveStore(setup.Provider, Work()).RestoreAsync(playTime.DataRecipe!, archive);
        JsonObject fields = ReadEntry(archive, "games.json")[0]!["Fields"]!.AsObject();
        Check(fields.ContainsKey("PlayCount") && !fields.ContainsKey("Comment") && !fields.ContainsKey("ImagePath"), "自动备份包字段沿用了手动选择。");
        setup.Settings.IncludeLocalGames = false;
        setup.Settings.AutomaticContent = BackupContent.Saves;
        SnapshotManifest excluded = await setup.Coordinator.BackupAsync(automatic: true);
        Check(excluded.Games.Count == 0 && excluded.SaveManifestPaths.Count == 0, "存档备份忽略了游戏类型范围。");
    }
}
