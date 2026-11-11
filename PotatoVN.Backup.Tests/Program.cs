using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PotatoVN.Backup.Core;
using PotatoVN.Backup.Core.Engine;

return args.Contains("--incremental-gc-only", StringComparer.Ordinal)
    ? await EngineTests.RunIncrementalGcOnlyAsync()
    : args.Contains("--remote-commit-only", StringComparer.Ordinal)
    ? await EngineTests.RunRemoteCommitOnlyAsync()
    : args.Contains("--incremental-store-only", StringComparer.Ordinal)
    ? await EngineTests.RunIncrementalStoreOnlyAsync()
    : args.Contains("--incremental-integration-only", StringComparer.Ordinal)
        ? await EngineTests.RunIncrementalIntegrationOnlyAsync()
        : await EngineTests.RunAsync(!args.Contains("--without-incremental-gc", StringComparer.Ordinal));

internal static partial class EngineTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "PotatoVNBackupTests", Guid.NewGuid().ToString("N"));
    private static int _passed;
    private const int ExpectedChecks = 39;
    public static async Task<int> RunAsync(bool includeIncrementalGc = true)
    {
        int expectedChecks = ExpectedChecks - (includeIncrementalGc ? 0 : 3);
        Directory.CreateDirectory(Root);
        if (!includeIncrementalGc) Console.WriteLine("SKIP 增量清理的3组：按本次入口要求排除清理，其余36组完整执行。");
        try
        {
            await Test("选择评价不泄露其他字段或图片", ProjectionAsync);
            await Test("只选封面包含封面而不包含头图", CoverProjectionAsync);
            await Test("空白设备恢复为非本地条目", NonLocalRestoreAsync);
            await Test("部分恢复保留未选字段和本地启动路径", PartialRestoreAsync);
            await Test("来源冲突需人工映射且不按同名误配", IdentityMatchingAsync);
            await Test("仅恢复分类时正确映射已有游戏UUID", AncillaryOnlyAsync);
            await Test("主页DTO选择与跨UUID迁移", HomeDtoAsync);
            await Test("延后映射库保留封面且不复用旧路径", DeferredLibraryAsync);
            await Test("部分分类恢复保留范围外关联", CategoryScopeAsync);
            await Test("ZIP穿越、重复路径与备用数据流被拒绝", InvalidZipAsync);
            await Test("存档多位置、重复恢复与明确回退", SavesRoundTripAsync);
            await Test("缺失及空存档不生成替代版本", MissingSaveAsync);
            await Test("待恢复区按引用清理且不影响实际存档", PendingCleanupAsync);
            await Test("损坏暂存包不写入现有存档", CorruptSaveAsync);
            await Test("坏配置保留错误而不重置为默认值", InvalidSettingsAsync);
            await Test("资料清单最后发布及未完成上传不可见", PublishLastAsync);
            await Test("存档复用、缺失保留及引用保护", SaveHistoryAsync);
            await Test("下载校验及非法清单路径被拒绝", DownloadValidationAsync);
            await Test("无来源ID映射后备份在新设备仍能关联存档", CanonicalIdentityAsync);
            await Test("资料范围及所有关联引用统一备份身份", CanonicalReferencesAsync);
            await Test("唯一来源匹配别名持久化且不改本机关联", LearnedAliasAsync);
            await Test("待恢复区清理取消残留和无引用包", PendingOrphansAsync);
            await Test("拒绝覆盖整个游戏根目录和用户基础目录", ProtectedTargetsAsync);
            await Test("增量首次上传对象并完整恢复", IncrementalInitialAsync);
            await Test("增量无变化跨实例不重复传对象", IncrementalNoChangeAsync);
            await Test("增量仅修改一个小存档只传一个对象", IncrementalSingleSaveChangeAsync);
            await Test("增量删除及重命名不重传且历史完整", IncrementalDeleteRenameAsync);
            await Test("增量资料更新不重传封面或存档", IncrementalStableMediaAsync);
            await Test("增量部分选择不携带未选引用或字段", IncrementalSelectionAsync);
            await Test("增量断网与取消不发布半成品", IncrementalInterruptedAsync);
            await Test("旧v1完整恢复及新v2独立复制", IncrementalLegacyRestoreAsync);
            if (includeIncrementalGc)
            {
                await Test("增量共享对象跨设备引用保护及回收", IncrementalReferencePruneAsync);
                await Test("增量清理删除中断不损坏保留版本", IncrementalDeleteFailureAsync);
                await Test("增量清理缺失保留引用时停止且不删除", IncrementalMissingReferenceStopsCleanupAsync);
            }
            await Test("增量缺失对象补传后可恢复", IncrementalMissingObjectAsync);
            await Test("增量失败残留同名半对象必须重传", IncrementalPartialObjectRetryAsync);
            await Test("已发布对象缺失且修复半写后重试", IncrementalPublishedRepairRetryAsync);
            await Test("未提交半清单不阻断已发布历史读取", IncrementalPartialManifestAsync);
            await Test("增量存档本机适配与重暂存保留确认状态", IncrementalLocalAdapterAsync);
            Console.WriteLine($"PASS {_passed} integration checks");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); Console.Error.WriteLine("测试数据保留于 " + Root); return 1; }
        finally { if (_passed == expectedChecks && Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private static async Task ProtectedTargetsAsync()
    {
        var directory = Work();
        var game = Game(directory: directory);
        var location = new SaveLocation { BaseKind = SaveBaseKind.Custom, CustomPath = directory };
        await Throws<InvalidDataException>(() => Task.FromResult(SaveArchiveService.Resolve(location, game)));
        var roots = new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.UserProfile };
        foreach (var folder in roots)
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(path))
                await Throws<InvalidDataException>(() => Task.FromResult(SaveArchiveService.Resolve(location, game, path)));
        }
    }

    private static async Task Test(string name, Func<Task> run)
    {
        await run(); _passed++; Console.WriteLine("PASS " + name);
    }
    private static string Work() { var path = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static GameIdentity Game(Guid? id = null, string? directory = null) => new()
    { Id = id ?? Guid.NewGuid(), Name = "测试游戏", SourceIds = new() { ["Vndb"] = "v100" }, LocalDirectory = directory };
    private static JsonObject NativeGame(GameIdentity game, string comment = "cloud") => new()
    {
        ["Uuid"] = game.Id.ToString(), ["Name"] = new JsonObject { ["Value"] = game.Name, ["IsLock"] = false },
        ["Description"] = new JsonObject { ["Value"] = "private-description" }, ["Ids"] = new JsonArray(game.SourceIds.GetValueOrDefault("Vndb"), game.SourceIds.GetValueOrDefault("Bangumi"), null, null, null, null, null, null),
        ["Comment"] = comment, ["MyRate"] = 8, ["PlayCount"] = 4, ["TotalPlayTime"] = 120,
        ["ImagePath"] = new JsonObject { ["Value"] = ".\\Images\\cover.png" },
        ["HeaderImagePath"] = new JsonObject { ["Value"] = ".\\Images\\header.png" },
        ["ExePath"] = "C:\\old\\game.exe", ["DetectedSavePath"] = new JsonObject { ["ParsedPath"] = "%GameRoot%\\savedata" }
    };
    private static async Task<HostExport> NativeAsync(IEnumerable<JsonObject> games, JsonArray? categories = null, JsonArray? libraries = null, JsonArray? home = null, JsonArray? staff = null)
    {
        var work = Work();
        var data = Path.Combine(work, "native"); Directory.CreateDirectory(Path.Combine(data, "Images"));
        await File.WriteAllTextAsync(Path.Combine(data, "data.galgames.json"), new JsonArray(games.Select(g => (JsonNode)g.DeepClone()).ToArray()).ToJsonString());
        await File.WriteAllTextAsync(Path.Combine(data, "data.galgameSources.json"), (libraries ?? new JsonArray()).ToJsonString());
        await File.WriteAllTextAsync(Path.Combine(data, "data.categoryGroups.json"), (categories ?? new JsonArray()).ToJsonString());
        await File.WriteAllTextAsync(Path.Combine(data, "data.staff.json"), (staff ?? new JsonArray()).ToJsonString());
        await File.WriteAllTextAsync(Path.Combine(data, "data.multiStreamPageList.json"), "[]");
        await File.WriteAllTextAsync(Path.Combine(data, "data.homepage_list.json"), (home ?? new JsonArray()).ToJsonString());
        await File.WriteAllTextAsync(Path.Combine(data, "data.dataStatus.json"), "{\"ImportGalgame\":false,\"GameLiteDbUpgrade\":false}");
        await File.WriteAllTextAsync(Path.Combine(data, "Images", "cover.png"), "cover-bytes");
        await File.WriteAllTextAsync(Path.Combine(data, "Images", "header.png"), "header-bytes");
        var archive = Path.Combine(work, "native.pvnExport.zip"); await BackupFiles.ZipAsync(data, archive);
        return new HostExport { ArchivePath = archive, Settings = new() { ["language"] = JsonValue.Create("zh-CN"), ["accessToken"] = JsonValue.Create("DO_NOT_BACK_UP") } };
    }
    private static JsonNode ReadEntry(string archive, string entry)
    {
        using var zip = ZipFile.OpenRead(archive); using var reader = new StreamReader(zip.GetEntry(entry)!.Open());
        return JsonNode.Parse(reader.ReadToEnd())!;
    }
    private static BackupSettings Settings(BackupContent content) => new() { Content = content };

    private static async Task ProjectionAsync()
    {
        var game = Game(); var export = await NativeAsync([NativeGame(game)]);
        var package = await SoftwareBackupCodec.CreateAsync(export, Settings(BackupContent.Reviews), [game], Work());
        var games = ReadEntry(package.DataPath!, "games.json").AsArray();
        var fields = games[0]!["Fields"]!.AsObject();
        Check(fields.ContainsKey("Comment") && !fields.ContainsKey("Description") && !fields.ContainsKey("ImagePath") && !fields.ContainsKey("ExePath"), "未选字段泄露");
        Check(package.MediaPath is null && package.Games.Count == 1, "不应包含媒体或丢失非本地游戏身份");
        var withSettings = await SoftwareBackupCodec.CreateAsync(export, Settings(BackupContent.Settings), [game], Work());
        var keys = ReadEntry(withSettings.DataPath!, "settings.json").AsObject();
        Check(keys.ContainsKey("language") && !keys.ContainsKey("accessToken"), "敏感设置泄露");
    }
    private static async Task CoverProjectionAsync()
    {
        var game = Game(); var export = await NativeAsync([NativeGame(game)]);
        var package = await SoftwareBackupCodec.CreateAsync(export, Settings(BackupContent.Covers), [game], Work());
        using var media = ZipFile.OpenRead(package.MediaPath!);
        Check(media.Entries.Count == 1 && media.Entries[0].Name == "cover.png", "封面选择错误地包含头图");
        var merged = await SoftwareBackupCodec.MergeAsync(await NativeAsync([NativeGame(game)]), package.DataPath!, package.MediaPath,
            BackupContent.Covers, Settings(BackupContent.Covers), [game], Work());
        var path = ReadEntry(merged.ArchivePath, "data.galgames.json")[0]!["ImagePath"]!["Value"]!.GetValue<string>();
        using var zip = ZipFile.OpenRead(merged.ArchivePath);
        Check(path.Contains("pvn_") && zip.GetEntry(path[2..]) is not null, "图片引用没有随新文件名迁移");
    }
    private static async Task NonLocalRestoreAsync()
    {
        var game = Game(); var source = await NativeAsync([NativeGame(game)]);
        var package = await SoftwareBackupCodec.CreateAsync(source, Settings(BackupContent.GameInfo), [game], Work());
        var result = await SoftwareBackupCodec.MergeAsync(await NativeAsync([]), package.DataPath!, null,
            BackupContent.GameInfo, Settings(BackupContent.GameInfo), [], Work());
        var restored = ReadEntry(result.ArchivePath, "data.galgames.json").AsArray();
        Check(restored.Count == 1 && restored[0]!["ExePath"] is null && restored[0]!["DetectedSavePath"] is null, "新设备错误绑定旧路径");
        Check(ReadEntry(result.ArchivePath, "data.galgameSources.json").AsArray().Count == 0, "创建了错误本地库");
    }
    private static async Task PartialRestoreAsync()
    {
        var remote = Game(); var localDir = Work(); var local = Game(Guid.NewGuid(), localDir);
        var package = await SoftwareBackupCodec.CreateAsync(await NativeAsync([NativeGame(remote)]), Settings(BackupContent.All), [remote], Work());
        var original = NativeGame(local, "local"); original["ExePath"] = "D:\\current\\game.exe"; original["PlayCount"] = 99;
        var result = await SoftwareBackupCodec.MergeAsync(await NativeAsync([original]), package.DataPath!, package.MediaPath,
            BackupContent.Reviews, Settings(BackupContent.Reviews), [local], Work());
        var game = ReadEntry(result.ArchivePath, "data.galgames.json")[0]!;
        Check(game["Comment"]!.GetValue<string>() == "cloud" && game["PlayCount"]!.GetValue<int>() == 99 &&
            game["ExePath"]!.GetValue<string>() == "D:\\current\\game.exe" && game["Uuid"]!.GetValue<string>() == local.Id.ToString(), "部分恢复改动未选数据");
    }
    private static Task IdentityMatchingAsync()
    {
        var incoming = Game(); var a = Game(); var b = Game();
        Check(GameMatcher.Match(incoming, [a,b], []).Conflict, "多条目匹配未要求确认");
        var mapped = GameMatcher.Match(incoming, [a,b], [new() { BackupGameId = incoming.Id, LocalGameId = b.Id }]);
        Check(mapped.Game == b, "人工映射未生效");
        Check(GameMatcher.Match(incoming, [a], [new() { BackupGameId = incoming.Id, LocalGameId = b.Id }]).Conflict, "过期人工映射未要求重选");
        var sameName = Game(); sameName.SourceIds.Clear(); incoming.SourceIds.Clear();
        Check(GameMatcher.Match(incoming, [sameName], []).Game is null, "不应仅凭同名匹配");
        return Task.CompletedTask;
    }
    private static async Task AncillaryOnlyAsync()
    {
        var remote = Game(); var local = Game(); var group = Guid.NewGuid();
        var categories = new JsonArray(new JsonObject { ["Id"] = group.ToString(), ["Categories"] = new JsonArray(new JsonObject {
            ["Id"] = Guid.NewGuid().ToString(), ["GalgamesX"] = new JsonArray(new JsonObject { ["Galgame"] = remote.Id.ToString() }) }) });
        var package = await SoftwareBackupCodec.CreateAsync(await NativeAsync([NativeGame(remote)], categories),
            Settings(BackupContent.LibrariesAndCategories), [remote], Work());
        var result = await SoftwareBackupCodec.MergeAsync(await NativeAsync([NativeGame(local)]), package.DataPath!, package.MediaPath,
            BackupContent.LibrariesAndCategories, Settings(BackupContent.LibrariesAndCategories), [local], Work());
        Check(ReadEntry(result.ArchivePath, "data.categoryGroups.json")[0]!["Categories"]![0]!["GalgamesX"]![0]!["Galgame"]!.GetValue<string>() == local.Id.ToString(), "仅分类恢复漏做UUID映射");
    }
    private static async Task HomeDtoAsync()
    {
        var remote = Game(); var local = Game();
        var home = new JsonArray(new JsonObject {
            ["_id"] = new JsonObject { ["$guid"] = Guid.NewGuid().ToString() },
            ["_type"] = "GalgameManager.MultiStreamPage.Lists.GameListDbDto, GalgameManager",
            ["Name"] = "云端主页", ["GameIds"] = new JsonArray(new JsonObject { ["$guid"] = remote.Id.ToString() }) });
        var old = new JsonArray(new JsonObject { ["Name"] = "本机主页", ["_type"] = "GalgameManager.MultiStreamPage.Lists.GameListDbDto, GalgameManager" });
        var package = await SoftwareBackupCodec.CreateAsync(await NativeAsync([NativeGame(remote)], home: home),
            Settings(BackupContent.Home | BackupContent.Reviews), [remote], Work());
        var current = await NativeAsync([NativeGame(local)], home: old);
        var selected = await SoftwareBackupCodec.MergeAsync(current, package.DataPath!, package.MediaPath,
            BackupContent.Home, Settings(BackupContent.Home), [local], Work());
        var dto = ReadEntry(selected.ArchivePath, "data.homepage_list.json")[0]!;
        Check(dto["Name"]!.GetValue<string>() == "云端主页" && dto["GameIds"]![0]!["$guid"]!.GetValue<string>() == local.Id.ToString(), "主页DTO未迁移");
        var uncheckedHome = await SoftwareBackupCodec.MergeAsync(current, package.DataPath!, package.MediaPath,
            BackupContent.Reviews, Settings(BackupContent.Reviews), [local], Work());
        Check(ReadEntry(uncheckedHome.ArchivePath, "data.homepage_list.json")[0]!["Name"]!.GetValue<string>() == "本机主页", "未选主页时仍覆盖主页");
    }

    private static async Task DeferredLibraryAsync()
    {
        var remote = Game(); var libraryId = Guid.NewGuid();
        var originalRoot = Path.Combine(Work(), "OldLibrary");
        var sources = new JsonArray(new JsonObject {
            ["Id"] = libraryId.ToString(), ["Name"] = "旧游戏库", ["Path"] = originalRoot,
            ["SourceType"] = 0, ["ImagePath"] = "./Images/cover.png", ["ExtraData"] = "SHOULD_NOT_TRANSFER",
            ["Galgames"] = new JsonArray(new JsonObject { ["Galgame"] = remote.Id.ToString(), ["Path"] = Path.Combine(originalRoot, "GameA") }) });
        var settings = Settings(BackupContent.GameInfo | BackupContent.LibrariesAndCategories);
        var package = await SoftwareBackupCodec.CreateAsync(await NativeAsync([NativeGame(remote)], libraries: sources), settings, [remote], Work());
        var merged = await SoftwareBackupCodec.MergeAsync(await NativeAsync([]), package.DataPath!, package.MediaPath, settings.Content, settings, [], Work());
        Check(ReadEntry(merged.ArchivePath, "data.galgameSources.json").AsArray().Count == 0 && settings.LibraryMappings.Count == 1, "旧库路径被直接关联");
        Check(settings.LibraryMappings[0].SourceJson.Contains("_DataBackupImages") && !settings.LibraryMappings[0].SourceJson.Contains("SHOULD_NOT_TRANSFER"), "待映射图片未保留或携带额外敏感数据");
        var targetRoot = Work(); var gameRoot = Path.Combine(targetRoot, "GameA"); Directory.CreateDirectory(gameRoot);
        settings.LibraryMappings[0].TargetPath = targetRoot;
        var mapped = await SoftwareBackupCodec.ApplyLibraryMappingsAsync(await NativeAsync([NativeGame(remote)]), settings, [remote], Work());
        var source = ReadEntry(mapped.ArchivePath, "data.galgameSources.json")[0]!;
        Check(source["Galgames"]![0]!["Path"]!.GetValue<string>() == gameRoot && source["ImagePath"]!.GetValue<string>().Contains("pvn_library_"), "新库映射或图片还原不正确");
        Check(source["_DataBackupImages"] is null, "本机待映射附加信息进入了宿主");
    }

    private static async Task CategoryScopeAsync()
    {
        var selected = Game(); var unrelated = Game(); unrelated.SourceIds["Vndb"] = "v200";
        var groupId = Guid.NewGuid().ToString(); var categoryId = Guid.NewGuid().ToString();
        JsonArray Groups(params Guid[] games) => new(new JsonObject { ["Id"] = groupId,
            ["Categories"] = new JsonArray(new JsonObject { ["Id"] = categoryId,
                ["GalgamesX"] = new JsonArray(games.Select(g => JsonValue.Create(g.ToString()) as JsonNode).ToArray()) }) });
        var settings = Settings(BackupContent.LibrariesAndCategories); settings.AllGames = false; settings.SelectedGameIds = [selected.Id];
        var package = await SoftwareBackupCodec.CreateAsync(await NativeAsync([NativeGame(selected)], Groups(selected.Id)), settings, [selected], Work());
        var merged = await SoftwareBackupCodec.MergeAsync(await NativeAsync([NativeGame(selected), NativeGame(unrelated)], Groups(unrelated.Id)),
            package.DataPath!, package.MediaPath, settings.Content, settings, [selected, unrelated], Work());
        var members = ReadEntry(merged.ArchivePath, "data.categoryGroups.json")[0]!["Categories"]![0]!["GalgamesX"]!.AsArray();
        Check(members.Count == 2 && members.Any(m => m!.GetValue<string>() == unrelated.Id.ToString()), "恢复范围外的分类关联被丢弃");
    }

    private static async Task InvalidZipAsync()
    {
        foreach (var entry in new[] { "../escape.txt", "C:/escape.txt", "safe/file.txt:stream" })
        {
            var archive = Path.Combine(Work(), "bad.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) zip.CreateEntry(entry);
            await Throws<InvalidDataException>(() => BackupFiles.ExtractAsync(archive, Path.Combine(Work(), "out")));
        }
        var duplicate = Path.Combine(Work(), "duplicate.zip");
        using (var zip = ZipFile.Open(duplicate, ZipArchiveMode.Create)) { zip.CreateEntry("a.txt"); zip.CreateEntry("A.TXT"); }
        await Throws<InvalidDataException>(() => BackupFiles.ExtractAsync(duplicate, Path.Combine(Work(), "out")));
    }
    private static async Task<(GameIdentity Game, SaveManifest Manifest, string Archive, SaveLocation Location)> SaveFixtureAsync()
    {
        var game = Game(directory: Work()); Directory.CreateDirectory(Path.Combine(game.LocalDirectory!, "savedata"));
        await File.WriteAllTextAsync(Path.Combine(game.LocalDirectory!, "savedata", "slot.dat"), "original-progress");
        var location = SaveArchiveService.Describe(Path.Combine(game.LocalDirectory!, "savedata"), game.LocalDirectory, false);
        var archive = Path.Combine(Work(), "save.zip");
        var manifest = (await SaveArchiveService.CreateAsync(game, [location], archive, Guid.NewGuid()))!;
        manifest.ManifestPath = $"v1/saves/{game.Id:N}/{manifest.DeviceId:N}/{manifest.Id}/manifest.json";
        manifest.ArchivePath = manifest.ManifestPath.Replace("manifest.json", "save.zip");
        return (game, manifest, archive, location);
    }
    private static async Task SavesRoundTripAsync()
    {
        var fixture = await SaveFixtureAsync();
        var secondFile = Path.Combine(fixture.Game.LocalDirectory!, "system.dat"); await File.WriteAllTextAsync(secondFile, "config");
        var second = SaveArchiveService.Describe(secondFile, fixture.Game.LocalDirectory, true);
        var archive = Path.Combine(Work(), "multi.zip");
        var manifest = (await SaveArchiveService.CreateAsync(fixture.Game, [fixture.Location, second], archive, fixture.Manifest.DeviceId))!;
        Check(manifest.Locations.Count == 2 && manifest.Files.Count == 2, "未保留多位置存档");
        var local = Game(fixture.Game.Id, Work()); var pending = new PendingRestore { Manifest = manifest, LocationId = fixture.Location.Id };
        Check(SaveArchiveService.Resolve(fixture.Location, Game()) is null, "未关联时不应解析目标");
        Check(await SaveArchiveService.RestoreAsync(pending, archive, local, Work()), "首次恢复应写入");
        Check(!await SaveArchiveService.RestoreAsync(pending, archive, local, Work()), "相同内容未跳过");
        await File.WriteAllTextAsync(Path.Combine(local.LocalDirectory!, "savedata", "slot.dat"), "new-progress");
        await File.WriteAllTextAsync(Path.Combine(local.LocalDirectory!, "savedata", "extra.dat"), "local-only");
        Check(await SaveArchiveService.RestoreAsync(pending, archive, local, Work()), "已恢复标记阻止了明确回退");
        Check(await File.ReadAllTextAsync(Path.Combine(local.LocalDirectory!, "savedata", "slot.dat")) == "original-progress" &&
            !File.Exists(Path.Combine(local.LocalDirectory!, "savedata", "extra.dat")), "未按整位置快照回退");
    }
    private static async Task MissingSaveAsync()
    {
        var fixture = await SaveFixtureAsync();
        var missing = new SaveLocation { BaseKind = SaveBaseKind.GameRoot, RelativePath = "missing" };
        Check(await SaveArchiveService.CreateAsync(fixture.Game, [fixture.Location, missing], Path.Combine(Work(), "missing.zip"), Guid.NewGuid()) is null,
            "部分root缺失仍创建了不完整新版本");
        Directory.CreateDirectory(Path.Combine(fixture.Game.LocalDirectory!, "empty"));
        Check(await SaveArchiveService.CreateAsync(fixture.Game, [new() { BaseKind = SaveBaseKind.GameRoot, RelativePath = "empty" }],
            Path.Combine(Work(), "empty.zip"), Guid.NewGuid()) is null, "空目录产生了替代存档");
    }
    private static async Task PendingCleanupAsync()
    {
        var fixture = await SaveFixtureAsync(); var root = Work(); var store = new PendingRestoreStore(root);
        var profile = new ProviderProfile(); await store.StageAsync(profile, fixture.Manifest, fixture.Archive);
        await store.StageAsync(profile, fixture.Manifest, fixture.Archive);
        Check(store.Items.Count == 1, "重复下载创建重复任务");
        var otherProfile = new ProviderProfile(); await store.StageAsync(otherProfile, fixture.Manifest, fixture.Archive);
        Check(store.GetStats().Bytes == fixture.Manifest.Length, "共享存档包重复计入占用");
        var loaded = new PendingRestoreStore(root); await loaded.LoadAsync(); Check(loaded.Items.Count == 2, "任务未跨重启保留");
        loaded.Refresh([Game(fixture.Game.Id)], []); Check(loaded.Items.All(i => i.Status == RestoreStatus.PendingLink), "未关联就可恢复");
        loaded.Items[0].IsBusy = true;
        await Throws<InvalidOperationException>(() => loaded.ClearAsync([loaded.Items[0].Id])); loaded.Items[0].IsBusy = false;
        await loaded.ClearAsync([loaded.Items[0].Id]); Check(File.Exists(loaded.ArchivePath(fixture.Manifest)), "清理一个引用误删共享文件");
        await loaded.ClearAsync(loaded.Items.Select(i => i.Id).ToList());
        Check(!File.Exists(loaded.ArchivePath(fixture.Manifest)) && loaded.GetStats().Bytes == 0, "最后引用移除后未释放空间");
        Check(File.Exists(fixture.Archive) && File.Exists(Path.Combine(fixture.Game.LocalDirectory!, "savedata", "slot.dat")), "清理误删来源或真实存档");
    }
    private static async Task CorruptSaveAsync()
    {
        var fixture = await SaveFixtureAsync(); await File.AppendAllTextAsync(fixture.Archive, "tampered");
        await Throws<InvalidDataException>(() => SaveArchiveService.RestoreAsync(new PendingRestore { Manifest = fixture.Manifest, LocationId = fixture.Location.Id },
            fixture.Archive, fixture.Game, Work()));
        Check(await File.ReadAllTextAsync(Path.Combine(fixture.Game.LocalDirectory!, "savedata", "slot.dat")) == "original-progress", "损坏包改写了目标");
    }
    private static async Task InvalidSettingsAsync()
    {
        var root = Work(); var store = new SettingsStore(root); var settings = new BackupSettings { IntervalHours = 0 };
        await store.SaveAsync(settings); await Throws<InvalidDataException>(() => store.LoadAsync());
        Check(File.Exists(Path.Combine(root, "settings.json")), "坏配置被删除");
    }

    private static (BackupCoordinator Coordinator, MemoryProvider Provider, FakeHost Host, BackupSettings Settings) Coordinator(BackupContent content, GameIdentity game)
    {
        var root = Work(); var provider = new MemoryProvider(); var profile = new ProviderProfile();
        var settings = new BackupSettings { Content = content, Profiles = [profile], ActiveProfileId = profile.Id };
        var host = new FakeHost([game]);
        var coordinator = new BackupCoordinator(root, host, settings, new SettingsStore(root), new PendingRestoreStore(root), _ => provider);
        return (coordinator, provider, host, settings);
    }
    private static async Task PublishLastAsync()
    {
        var good = Coordinator(BackupContent.Reviews, Game());
        var snapshot = await good.Coordinator.BackupAsync();
        Check(good.Provider.Uploads.Last() == snapshot.ManifestPath + ".complete", "快照清单未最后发布");
        var bad = Coordinator(BackupContent.Reviews, Game()); bad.Provider.FailUpload = p => p.StartsWith("v2/objects/", StringComparison.Ordinal) && p.EndsWith(".gz", StringComparison.Ordinal);
        await Throws<IOException>(() => bad.Coordinator.BackupAsync());
        Check(!bad.Provider.Files.Keys.Any(k => (k.StartsWith("v1/snapshots/") || k.StartsWith("v2/snapshots/")) && k.EndsWith("manifest.json")), "失败仍发布可恢复版本");
        Check((await good.Coordinator.ListSnapshotsAsync()).Count == 1 && snapshot.Games.Single().LocalDirectory is null, "非本地条目备份被遗漏");
    }
    private static async Task SaveHistoryAsync()
    {
        var fixture = await SaveFixtureAsync(); var setup = Coordinator(BackupContent.Saves, fixture.Game);
        setup.Settings.SaveConfigs = [new() { GameId = fixture.Game.Id, Locations = [fixture.Location] }];
        setup.Settings.KeepSaves = 1;
        var first = await setup.Coordinator.BackupAsync(); var second = await setup.Coordinator.BackupAsync();
        Check(first.SaveManifestPaths.Single() == second.SaveManifestPaths.Single(), "未变化存档重复上传");
        await File.WriteAllTextAsync(Path.Combine(fixture.Game.LocalDirectory!, "savedata", "slot.dat"), "changed-progress");
        var third = await setup.Coordinator.BackupAsync();
        Check(third.SaveManifestPaths.Single() != first.SaveManifestPaths.Single() && setup.Provider.Files.ContainsKey(first.SaveManifestPaths.Single()), "仍被快照引用的旧存档被删除");
        Directory.Delete(Path.Combine(fixture.Game.LocalDirectory!, "savedata"), true);
        var fourth = await setup.Coordinator.BackupAsync();
        Check(fourth.SaveManifestPaths.Single() == third.SaveManifestPaths.Single() && fourth.Notices.Count > 0, "缺失文件未保留旧版本");
        Directory.CreateDirectory(Path.Combine(fixture.Game.LocalDirectory!, "savedata"));
        var fifth = await setup.Coordinator.BackupAsync();
        Check(fifth.SaveManifestPaths.Single() == third.SaveManifestPaths.Single(), "空目录替代旧版本");
        await setup.Coordinator.StageRestoreAsync(fifth, BackupContent.Saves);
        Check(setup.Coordinator.Pending.Items.Count == 1 && setup.Coordinator.Pending.GetStats().Bytes > 0, "存档未暂存");
    }
    private static async Task DownloadValidationAsync()
    {
        var setup = Coordinator(BackupContent.GameInfo, Game()); var snapshot = await setup.Coordinator.BackupAsync();
        string dataPath = snapshot.Data?.Path ?? IncrementalObjectPath(snapshot.DataRecipe!, snapshot.DataRecipe!.Files[0]);
        setup.Provider.Files[dataPath] = Encoding.UTF8.GetBytes("corrupted");
        await Throws<InvalidDataException>(() => setup.Coordinator.StageRestoreAsync(snapshot, BackupContent.GameInfo));
        Check(setup.Host.ImportCalls == 0, "损坏资料包进入了宿主导入");
        snapshot.ManifestPath = "v1/../bad/manifest.json";
        await Throws<InvalidDataException>(() => setup.Coordinator.StageRestoreAsync(snapshot, BackupContent.GameInfo));
    }

    private static async Task CanonicalIdentityAsync()
    {
        var fixture = await SaveFixtureAsync();
        fixture.Game.SourceIds.Clear();
        var originalId = Guid.NewGuid();
        var deviceB = Coordinator(BackupContent.GameInfo | BackupContent.Saves, fixture.Game);
        deviceB.Settings.AllGames = false;
        deviceB.Settings.SelectedGameIds = [fixture.Game.Id];
        deviceB.Settings.GameMappings = [new() { BackupGameId = originalId, LocalGameId = fixture.Game.Id }];
        deviceB.Settings.SaveConfigs = [new() { GameId = fixture.Game.Id, Locations = [fixture.Location] }];
        var snapshot = await deviceB.Coordinator.BackupAsync();
        Check(snapshot.Games.Single().Id == originalId, "软件快照没有使用统一备份身份");
        var savedManifest = JsonSerializer.Deserialize<SaveManifest>(deviceB.Provider.Files[snapshot.SaveManifestPaths.Single()], BackupJson.Options)!;
        Check(savedManifest.Game.Id == originalId && savedManifest.Game.SourceIds.Count == 0, "测试未覆盖无来源ID存档");
        var profile = deviceB.Settings.Profiles.Single(); var cRoot = Work();
        var settingsC = new BackupSettings { Profiles = [profile], ActiveProfileId = profile.Id };
        var hostC = new FakeHost([]); var pendingC = new PendingRestoreStore(cRoot);
        var coordinatorC = new BackupCoordinator(cRoot, hostC, settingsC, new SettingsStore(cRoot), pendingC, _ => deviceB.Provider);
        await coordinatorC.StageRestoreAsync(snapshot, BackupContent.GameInfo | BackupContent.Saves);
        var restoredRaw = hostC.ImportedGames!.Single()!;
        Check(restoredRaw["Uuid"]!.GetValue<string>() == originalId.ToString() &&
            restoredRaw["Ids"]!.AsArray().All(id => id is null), "新设备资料身份与存档断开或出现伪来源ID");
        var restoredGame = new GameIdentity { Id = originalId, Name = fixture.Game.Name, SourceIds = [] };
        pendingC.Refresh([restoredGame], settingsC.GameMappings);
        Check(pendingC.Items.Single().LocalGameId == originalId && pendingC.Items.Single().Status == RestoreStatus.PendingLink,
            "无来源ID存档没有匹配恢复后的资料条目");
        restoredGame.LocalDirectory = Work(); pendingC.Refresh([restoredGame], settingsC.GameMappings);
        Check(pendingC.Items.Single().Status == RestoreStatus.Ready, "关联本地目录后存档未就绪");
        await SaveArchiveService.RestoreAsync(pendingC.Items.Single(), pendingC.ArchivePath(pendingC.Items.Single().Manifest), restoredGame, Work());
        Check(await File.ReadAllTextAsync(Path.Combine(restoredGame.LocalDirectory, "savedata", "slot.dat")) == "original-progress",
            "跨设备映射后的真实存档内容没有恢复");
    }

    private static async Task CanonicalReferencesAsync()
    {
        var gameB = Game(); var backupId = Guid.NewGuid();
        var idText = gameB.Id.ToString(); var canonical = backupId.ToString();
        var library = new JsonArray(new JsonObject { ["Id"] = Guid.NewGuid().ToString(), ["Path"] = Work(),
            ["Galgames"] = new JsonArray(new JsonObject { ["Galgame"] = idText, ["Path"] = Work() }) });
        var categories = new JsonArray(new JsonObject { ["Id"] = Guid.NewGuid().ToString(),
            ["Categories"] = new JsonArray(new JsonObject { ["Id"] = Guid.NewGuid().ToString(), ["GalgamesX"] = new JsonArray(idText) }) });
        var staff = new JsonArray(new JsonObject { ["Id"] = Guid.NewGuid().ToString(),
            ["Games"] = new JsonArray(new JsonObject { ["GameId"] = idText }) });
        var home = new JsonArray(new JsonObject { ["_type"] = "GalgameManager.MultiStreamPage.Lists.GameListDbDto, GalgameManager",
            ["GameIds"] = new JsonArray(new JsonObject { ["$guid"] = idText }) });
        var settings = Settings(BackupContent.GameInfo | BackupContent.LibrariesAndCategories | BackupContent.Staff | BackupContent.Home);
        settings.AllGames = false; settings.SelectedGameIds = [gameB.Id];
        settings.GameMappings = [new() { BackupGameId = backupId, LocalGameId = gameB.Id }];
        var package = await SoftwareBackupCodec.CreateAsync(await NativeAsync([NativeGame(gameB)], categories, library, home, staff),
            settings, [gameB], Work());
        Check(package.Games.Single().Id == backupId, "选中范围按canonical ID过滤导致条目丢失");
        Check(ReadEntry(package.DataPath!, "games.json")[0]!["Identity"]!["Id"]!.GetValue<string>() == canonical, "资料包身份未规范化");
        foreach (var entry in new[] { "libraries.json", "categories.json", "staff.json", "home.json" })
        {
            var json = ReadEntry(package.DataPath!, entry).ToJsonString();
            Check(json.Contains(canonical) && !json.Contains(idText), entry + " 仍引用本机UUID");
        }
        Check(gameB.Id != backupId, "规范化改动了本机游戏身份");
    }

    private static async Task LearnedAliasAsync()
    {
        var remote = Game(); var local = Game(directory: Work());
        var package = await SoftwareBackupCodec.CreateAsync(await NativeAsync([NativeGame(remote)]), Settings(BackupContent.Reviews), [remote], Work());
        var settings = Settings(BackupContent.Reviews); var originalDirectory = local.LocalDirectory;
        await SoftwareBackupCodec.MergeAsync(await NativeAsync([NativeGame(local)]), package.DataPath!, package.MediaPath,
            BackupContent.Reviews, settings, [local], Work());
        Check(settings.GameMappings.Single().BackupGameId == remote.Id && settings.GameMappings.Single().LocalGameId == local.Id,
            "唯一来源匹配没有持久化别名");
        var next = await SoftwareBackupCodec.CreateAsync(await NativeAsync([NativeGame(local)]), settings, [local], Work());
        Check(next.Games.Single().Id == remote.Id && local.LocalDirectory == originalDirectory,
            "后续备份身份没有沿用别名或本机关联被改变");
    }

    private static async Task PendingOrphansAsync()
    {
        var fixture = await SaveFixtureAsync(); var root = Work(); var store = new PendingRestoreStore(root);
        await store.StageAsync(new ProviderProfile(), fixture.Manifest, fixture.Archive);
        var directory = Path.Combine(root, "pending", "archives");
        var orphan = Path.Combine(directory, new string('a', 64) + ".zip");
        var partial = Path.Combine(directory, "interrupted.zip.dead.tmp");
        await File.WriteAllTextAsync(orphan, "orphan"); await File.WriteAllTextAsync(partial, "partial");
        Check(store.GetStats().Bytes == fixture.Manifest.Length + 13, "占用没有包含临时或孤儿文件");
        var loaded = new PendingRestoreStore(root); await loaded.LoadAsync();
        Check(!File.Exists(orphan) && !File.Exists(partial) && File.Exists(loaded.ArchivePath(fixture.Manifest)), "启动清理误删引用包或漏清残留");
        await File.WriteAllTextAsync(orphan, "orphan");
        await loaded.ClearAsync(loaded.Items.Select(i => i.Id).ToList());
        Check(loaded.GetStats().Bytes == 0 && !File.Exists(orphan), "清理任务后仍残留无引用包");
        var cancelRoot = Work(); var cancelled = new PendingRestoreStore(cancelRoot); using var cts = new CancellationTokenSource(); cts.Cancel();
        await Throws<OperationCanceledException>(() => cancelled.StageAsync(new ProviderProfile(), fixture.Manifest, fixture.Archive, cts.Token));
        Check(cancelled.GetStats().Bytes == 0, "取消下载留下暂存占用");
    }

    private sealed class FakeHost(List<GameIdentity> games) : IBackupHost
    {
        public int ImportCalls { get; private set; }
        public JsonArray? ImportedGames { get; private set; }
        public IReadOnlyList<GameIdentity> GetGames() => games;
        public Task<HostExport> ExportAsync(string workDirectory, CancellationToken cancellationToken) => NativeAsync(games.Select(g => NativeGame(g)));
        public Task StageImportAsync(string mergedArchivePath, Dictionary<string, JsonNode?> settings, CancellationToken cancellationToken)
        { ImportCalls++; ImportedGames = ReadEntry(mergedArchivePath, "data.galgames.json").AsArray(); return Task.CompletedTask; }
    }
    private sealed class MemoryProvider : IBackupProvider
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public List<string> Uploads { get; } = [];
        public Func<string,bool>? FailUpload { get; set; }
        public void Dispose() { }
        public Task AuthorizeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ProbeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(Files.ContainsKey(path));
        public Task<IReadOnlyList<RemoteItem>> ListAsync(string directory, CancellationToken cancellationToken = default)
        {
            var prefix = directory.TrimEnd('/') + "/";
            IReadOnlyList<RemoteItem> items = Files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .Select(k => k[prefix.Length..].Split('/')[0]).Distinct().Select(name =>
                { var path = prefix + name; return new RemoteItem(path, !Files.ContainsKey(path), Files.TryGetValue(path, out var bytes) ? bytes.Length : 0); }).ToList();
            return Task.FromResult(items);
        }
        public async Task UploadAsync(string path, string localFile, CancellationToken cancellationToken = default)
        {
            if (FailUpload?.Invoke(path) == true) throw new IOException("injected upload failure");
            Files[path] = await File.ReadAllBytesAsync(localFile, cancellationToken); Uploads.Add(path);
        }
        public async Task DownloadAsync(string path, string localFile, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localFile)!);
            await File.WriteAllBytesAsync(localFile, Files[path], cancellationToken);
        }
        public Task DeleteAsync(string path, CancellationToken cancellationToken = default) { Files.Remove(path); return Task.CompletedTask; }
    }
}
