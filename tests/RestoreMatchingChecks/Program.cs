using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using PotatoVN.Backup.Core;
using PotatoVN.Backup.Core.Engine;

return await RestoreMatchingChecks.RunAsync();

internal static class RestoreMatchingChecks
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "PotatoVNRestoreMatchingChecks", Guid.NewGuid().ToString("N"));
    private static int _passed;

    public static async Task<int> RunAsync()
    {
        Directory.CreateDirectory(Root);
        (string Name, Func<Task> Run)[] cases = [
            ("空设备与无云端候选不弹出对应关系", EmptyAnalysisAsync),
            ("全部匹配及同UUID更名无需人工确认", ExactAndRenamedAsync),
            ("同名无ID与多来源冲突不能自动匹配", NamesAndSourceConflictsAsync),
            ("两个云端条目竞争一个本地目标时需人工选择", ContestedAnalysisAsync),
            ("重复手选同一个云端条目被拒绝且配置不变", DuplicateChoicesAsync),
            ("预览和取消不修改持久映射", PreviewDoesNotMutateAsync),
            ("只恢复常规设置不需要游戏匹配", SettingsOnlyAsync),
            ("失效映射可以通过不关联解除", ExpiredMappingAsync),
            ("重复云端UUID明确拒绝", DuplicateCloudIdsAsync),
            ("历史别名可指向同一本地游戏", HistoricalAliasesAsync),
            ("重复条目重定向生成合并且阻止不同目录或运行游戏", ExplicitMergePlanAsync),
            ("不关联保留本机记录并新建非本地云端资料", ExplicitNoAssociationAsync),
            ("空设备的相同来源不同版本不被折叠", EmptyLibrarySameSourceAsync),
            ("未预览的重复目标阻止导入且不学习错误映射", CodecContestedTargetAsync),
            ("合并迁移别名、游戏范围、存档配置并保留无关配置", MappingMigrationAsync),
            ("原生包合并只删重复记录并迁移所有引用", NativeLocalMergeAsync),
            ("合并后资料覆盖只应用勾选类型", SelectedOverlayAfterMergeAsync),
            ("存档不关联排除跨重启保留且可显式重关联", PendingExclusionAsync),
            ("新关联条目只就绪存档而不自动写入", LinkThenManualRestoreAsync),
            ("待恢复区清理排除记录不影响存档与云端源包", ClearPendingExclusionAsync),
            ("未选游戏信息的不关联不会误迁移其他类型引用", DeclinedAncillaryReferencesAsync),
            ("只恢复设置绕过损坏游戏与临时合并", SettingsOnlyMalformedGamesAsync),
            ("协调器存档单独恢复尊重不关联并持久保存", CoordinatorSavesOnlyAsync),
            ("协调器下载校验失败不提交预览映射", CoordinatorFailedDownloadAsync),
            ("协调器取消恢复不提交预览映射", CoordinatorCancelledAsync),
            ("重新不关联清除旧的自定义恢复路径", RepeatedNoAssociationClearsOverrideAsync),
            ("只选存档也可确认合并重复本机资料", CoordinatorSavesOnlyMergeAsync),
            ("新设备全量恢复保留非本地条目与待恢复存档", CoordinatorNewDeviceAsync),
            ("全局改映射清除全部历史版本旧目标与恢复状态", PendingHistoryRetargetAsync),
            ("同目标刷新保留各历史版本自定义目标和恢复状态", PendingSameTargetPreservesOverridesAsync),
            ("宿主拒绝导入时不提交新的恢复映射", CoordinatorImportPreflightAsync)
        ];
        try
        {
            foreach (var test in cases)
            {
                await test.Run();
                _passed++;
                Console.WriteLine("PASS " + test.Name);
            }
            Console.WriteLine($"PASS {_passed} restore matching checks");
            Directory.Delete(Root, true);
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            Console.Error.WriteLine("测试数据保留于 " + Root);
            return 1;
        }
    }

    private static Task EmptyAnalysisAsync()
    {
        var cloud = Game();
        var empty = RestoreMatchingPreview.Analyze([cloud], [], []);
        Check(!empty.NeedsPrompt && empty.AutomaticTargets.Count == 0, "空设备不需要对不存在的本地条目提问。");
        var choice = RestoreMatchingPreview.Decide(empty, new Dictionary<Guid, Guid?>());
        Check(choice.Targets.Count == 1 && choice.Targets[cloud.Id] is null, "云端未分配条目应保留创建非本地资料的语义。");
        Check(!RestoreMatchingPreview.Analyze([], [Game()], []).NeedsPrompt, "没有云端候选时不应弹出空选择框。");
        return Task.CompletedTask;
    }

    private static Task ExactAndRenamedAsync()
    {
        var cloud = Game(name: "原名称");
        var local = Game(cloud.Id, "本机更名");
        var analysis = RestoreMatchingPreview.Analyze([cloud], [local], []);
        Check(!analysis.NeedsPrompt && analysis.AutomaticTargets[cloud.Id] == local.Id, "名称变化不能破坏UUID匹配。");
        var cloud2 = Game(source: "v2");
        var local2 = Game(source: "v2");
        var all = RestoreMatchingPreview.Analyze([cloud, cloud2], [local, local2], []);
        Check(!all.NeedsPrompt && all.AutomaticTargets.Count == 2, "全部明确匹配不应弹窗。");
        return Task.CompletedTask;
    }

    private static Task NamesAndSourceConflictsAsync()
    {
        var cloud = Game(name: "同名游戏", source: null);
        var local = Game(name: "同名游戏", source: null);
        var names = RestoreMatchingPreview.Analyze([cloud], [local], []);
        Check(names.NeedsPrompt && names.UnmatchedLocalGames.Single().Id == local.Id && names.AutomaticTargets.Count == 0,
            "同名不能作为自动合并依据。");
        var conflicting = Game(cloud.Id, source: "v2");
        cloud.SourceIds["Vndb"] = "v1";
        Check(RestoreMatchingPreview.Analyze([cloud], [conflicting], []).AutomaticTargets.Count == 0,
            "UUID相同但来源冲突必须重新确认。");
        return Task.CompletedTask;
    }

    private static Task ContestedAnalysisAsync()
    {
        var cloudA = Game(source: "v1"); var cloudB = Game(source: "v1"); var local = Game(source: "v1");
        var analysis = RestoreMatchingPreview.Analyze([cloudA, cloudB], [local], []);
        Check(analysis.NeedsPrompt && analysis.AutomaticTargets.Count == 0, "竞争目标不能由遍历顺序决定。");
        var decision = RestoreMatchingPreview.Decide(analysis, new Dictionary<Guid, Guid?> { [local.Id] = cloudA.Id });
        Check(decision.Targets[cloudA.Id] == local.Id && decision.Targets[cloudB.Id] is null,
            "用户指定一个版本后另一个版本仍应独立。");
        return Task.CompletedTask;
    }

    private static Task DuplicateChoicesAsync()
    {
        var cloud = Game(source: null); var first = Game(source: null); var second = Game(source: null);
        var settings = new BackupSettings { GameMappings = [Map(Guid.NewGuid(), first.Id)] };
        var before = JsonSerializer.Serialize(settings, BackupJson.Options);
        var analysis = RestoreMatchingPreview.Analyze([cloud], [first, second], settings.GameMappings);
        Throws<InvalidOperationException>(() => RestoreMatchingPreview.Decide(analysis,
            new Dictionary<Guid, Guid?> { [first.Id] = cloud.Id, [second.Id] = cloud.Id }));
        Check(JsonSerializer.Serialize(settings, BackupJson.Options) == before, "非法选择不能先改部分配置。");
        var duplicateTargets = new RestoreMatchingDecisions { Targets = { [cloud.Id] = first.Id, [Guid.NewGuid()] = first.Id } };
        Throws<InvalidOperationException>(() => RestoreMatchingPreview.Validate(duplicateTargets,
            duplicateTargets.Targets.Keys.Select(id => Game(id, source: null)).ToList(), [first, second]));
        return Task.CompletedTask;
    }

    private static Task PreviewDoesNotMutateAsync()
    {
        var cloud = Game(source: null); var local = Game(source: null); var previous = Guid.NewGuid();
        var settings = new BackupSettings { GameMappings = [Map(previous, local.Id)] };
        var before = JsonSerializer.Serialize(settings, BackupJson.Options);
        var analysis = RestoreMatchingPreview.Analyze([cloud], [local], settings.GameMappings);
        var decisions = RestoreMatchingPreview.Decide(analysis, new Dictionary<Guid, Guid?> { [local.Id] = cloud.Id });
        settings.ActiveRestoreMatching = decisions;
        Check(JsonSerializer.Serialize(settings, BackupJson.Options) == before,
            "预览和临时选择不能写入持久映射，临时决策也不能序列化。");
        return Task.CompletedTask;
    }

    private static Task SettingsOnlyAsync()
    {
        Check(!RestoreMatchingPreview.RequiresMatching(BackupContent.None), "空选择不需要匹配。");
        Check(!RestoreMatchingPreview.RequiresMatching(BackupContent.Settings), "设置恢复不需要游戏对应关系。");
        Check(RestoreMatchingPreview.RequiresMatching(BackupContent.Saves) &&
            RestoreMatchingPreview.RequiresMatching(BackupContent.Reviews | BackupContent.Settings), "游戏相关恢复仍需匹配。");
        return Task.CompletedTask;
    }

    private static Task ExpiredMappingAsync()
    {
        var cloud = Game(source: "v1"); var local = Game(source: "v1");
        var settings = new BackupSettings { GameMappings = [Map(cloud.Id, Guid.NewGuid())] };
        var analysis = RestoreMatchingPreview.Analyze([cloud], [local], settings.GameMappings);
        Check(analysis.NeedsPrompt && analysis.AutomaticTargets.Count == 0, "失效目标不能被当作已确认匹配。");
        var decisions = RestoreMatchingPreview.Decide(analysis, new Dictionary<Guid, Guid?> { [local.Id] = null });
        RestoreMatchingPreview.ApplyMappings(settings, decisions);
        Check(settings.GameMappings.All(m => m.BackupGameId != cloud.Id), "用户选择不关联后应清除旧的失效映射。");
        return Task.CompletedTask;
    }

    private static Task DuplicateCloudIdsAsync()
    {
        var cloud = Game();
        Throws<InvalidDataException>(() => RestoreMatchingPreview.Analyze([cloud, Game(cloud.Id)], [], []));
        return Task.CompletedTask;
    }

    private static Task HistoricalAliasesAsync()
    {
        var cloud = Game(); var historical = Game(source: "v2"); var local = Game();
        var maps = new List<GameMatchMapping> { Map(cloud.Id, local.Id), Map(historical.Id, local.Id) };
        var analysis = RestoreMatchingPreview.Analyze([cloud], [local], maps);
        var decision = RestoreMatchingPreview.Decide(analysis, new Dictionary<Guid, Guid?>());
        var settings = new BackupSettings { GameMappings = maps };
        RestoreMatchingPreview.ApplyMappings(settings, decision);
        Check(!analysis.NeedsPrompt && settings.GameMappings.Count == 2 && settings.GameMappings.All(m => m.LocalGameId == local.Id),
            "当前快照之外的历史别名不能被一对一约束误删。");
        return Task.CompletedTask;
    }

    private static Task ExplicitMergePlanAsync()
    {
        var cloud = Game(source: null); var restored = Game(cloud.Id, source: null); var installed = Game(directory: Work(), source: null);
        var analysis = RestoreMatchingPreview.Analyze([cloud], [restored, installed], []);
        var plan = RestoreMatchingPreview.Decide(analysis, new Dictionary<Guid, Guid?> { [installed.Id] = cloud.Id });
        Check(plan.Merges.SequenceEqual([new LocalGameMerge(restored.Id, installed.Id)]) && plan.Targets[cloud.Id] == installed.Id,
            "重定向应明确移除先前恢复的非本地重复条目。");
        restored.IsRunning = true;
        Throws<InvalidOperationException>(() => RestoreMatchingPreview.Validate(plan, [cloud], [restored, installed]));
        restored.IsRunning = false; restored.LocalDirectory = Work();
        Throws<InvalidOperationException>(() => RestoreMatchingPreview.Validate(plan, [cloud], [restored, installed]));
        restored.LocalDirectory = installed.LocalDirectory;
        RestoreMatchingPreview.Validate(plan, [cloud], [restored, installed]);
        return Task.CompletedTask;
    }

    private static async Task ExplicitNoAssociationAsync()
    {
        var cloud = Game(name: "云端游戏", source: null); var local = Game(name: "本机游戏", directory: Work(), source: null);
        var plan = RestoreMatchingPreview.Decide(RestoreMatchingPreview.Analyze([cloud], [local], []),
            new Dictionary<Guid, Guid?> { [local.Id] = null });
        var settings = new BackupSettings { Content = BackupContent.GameInfo, ActiveRestoreMatching = plan };
        var package = await SoftwareBackupCodec.CreateAsync(await NativeAsync([NativeGame(cloud)]), settings, [cloud], Work());
        var localRaw = NativeGame(local, "保留本机评价");
        var merged = await SoftwareBackupCodec.MergeAsync(await NativeAsync([localRaw]), package.DataPath!, null,
            BackupContent.GameInfo, settings, [local], Work());
        Check(merged.Conflicts.Count == 0, "不关联不应成为恢复冲突。");
        var games = ReadEntry(merged.ArchivePath, "data.galgames.json").AsArray();
        Check(games.Count == 2, "不关联应保留本机并创建独立云端条目。");
        var kept = FindGame(games, local.Id); var added = FindGame(games, cloud.Id);
        Check(JsonNode.DeepEquals(kept, localRaw), "不关联修改了原本本机条目。");
        Check(added["ExePath"] is null && added["DetectedSavePath"] is null, "云端条目不能继承旧设备的启动或存档路径。");
    }

    private static async Task EmptyLibrarySameSourceAsync()
    {
        var first = Game(name: "版本A", source: "v42"); var second = Game(name: "版本B", source: "v42");
        var settings = new BackupSettings { Content = BackupContent.GameInfo };
        var package = await SoftwareBackupCodec.CreateAsync(await NativeAsync([NativeGame(first), NativeGame(second)]), settings, [first, second], Work());
        var merged = await SoftwareBackupCodec.MergeAsync(await NativeAsync([]), package.DataPath!, null,
            BackupContent.GameInfo, settings, [], Work());
        var games = ReadEntry(merged.ArchivePath, "data.galgames.json").AsArray();
        Check(merged.Conflicts.Count == 0 && games.Count == 2 && FindGame(games, first.Id) is not null && FindGame(games, second.Id) is not null,
            "空设备不能用同批刚创建条目折叠另一个云端版本。");
    }

    private static async Task CodecContestedTargetAsync()
    {
        var first = Game(source: "v55"); var second = Game(source: "v55"); var local = Game(source: "v55");
        var settings = new BackupSettings { Content = BackupContent.GameInfo };
        var package = await SoftwareBackupCodec.CreateAsync(await NativeAsync([NativeGame(first), NativeGame(second)]), settings, [first, second], Work());
        var before = JsonSerializer.Serialize(settings, BackupJson.Options);
        var merged = await SoftwareBackupCodec.MergeAsync(await NativeAsync([NativeGame(local)]), package.DataPath!, null,
            BackupContent.GameInfo, settings, [local], Work());
        Check(merged.Conflicts.Count > 0 && string.IsNullOrEmpty(merged.ArchivePath), "重复目标必须在写原生导入包前拒绝。");
        Check(JsonSerializer.Serialize(settings, BackupJson.Options) == before, "冲突恢复不得学习最后一个覆盖的错误别名。");
    }

    private static Task MappingMigrationAsync()
    {
        var removed = Guid.NewGuid(); var kept = Guid.NewGuid(); var cloud = Guid.NewGuid(); var alias = Guid.NewGuid(); var unrelated = Guid.NewGuid();
        var settings = new BackupSettings
        {
            Profiles = [new ProviderProfile { Name = "本机渠道" }],
            GameMappings = [Map(alias, removed), Map(unrelated, unrelated)],
            SelectedGameIds = [removed, kept, unrelated],
            SaveConfigs = [
                new() { GameId = removed, Locations = [Location("old"), Location("shared")] },
                new() { GameId = kept, Locations = [Location("keep"), Location("shared")] },
                new() { GameId = unrelated, Locations = [Location("unrelated")] }
            ],
            CustomSavePaths = new() { ["custom"] = "local-setting" }
        };
        var decisions = new RestoreMatchingDecisions { Targets = { [cloud] = kept }, Merges = [new(removed, kept)] };
        RestoreMatchingPreview.ApplyMappings(settings, decisions);
        Check(settings.GameMappings.Single(m => m.BackupGameId == alias).LocalGameId == kept &&
            settings.GameMappings.Single(m => m.BackupGameId == removed).LocalGameId == kept &&
            settings.GameMappings.Single(m => m.BackupGameId == cloud).LocalGameId == kept, "旧别名和被移除条目的身份均需迁移。");
        Check(settings.SelectedGameIds.SequenceEqual([kept, unrelated]), "游戏范围必须迁移并去重。");
        Check(settings.SaveConfigs.Count == 2 && settings.SaveConfigs.Single(c => c.GameId == kept).Locations.Select(l => l.Id).ToHashSet().SetEquals(["old", "keep", "shared"]),
            "两个存档规则集必须合并且不重复位置ID。");
        Check(settings.GameMappings.Single(m => m.BackupGameId == unrelated).LocalGameId == unrelated &&
            settings.SaveConfigs.Single(c => c.GameId == unrelated).Locations.Single().Id == "unrelated" &&
            settings.Profiles.Single().Name == "本机渠道" && settings.CustomSavePaths["custom"] == "local-setting", "合并影响了无关配置。");
        return Task.CompletedTask;
    }

    private static async Task NativeLocalMergeAsync()
    {
        var fixture = await MergeFixtureAsync();
        var merged = await SoftwareBackupCodec.MergeLocalGamesAsync(fixture.Current, fixture.Settings,
            [fixture.Removed, fixture.Kept, fixture.Other], Work());
        Check(merged.Conflicts.Count == 0, "有效显式合并不应报冲突。");
        var games = ReadEntry(merged.ArchivePath, "data.galgames.json").AsArray();
        Check(games.Count == 2 && games.All(g => g!["Uuid"]!.ToString() != fixture.Removed.Id.ToString()), "仅重复记录应被移除。");
        Check(JsonNode.DeepEquals(FindGame(games, fixture.Kept.Id), fixture.KeptRaw), "保留条目的资料、启动路径和参数必须完整保持。");
        foreach (var entry in new[] { "data.galgameSources.json", "data.categoryGroups.json", "data.staff.json", "data.homepage_list.json" })
            Check(!ReadEntry(merged.ArchivePath, entry).ToJsonString().Contains(fixture.Removed.Id.ToString()), entry + " 仍然引用已移除条目。");
        var library = ReadEntry(merged.ArchivePath, "data.galgameSources.json")[0]!["Galgames"]!.AsArray();
        Check(library.Count == 2 && library.Single(g => g!["Galgame"]!.ToString() == fixture.Kept.Id.ToString())!["Path"]!.ToString() == fixture.Kept.LocalDirectory,
            "重复库关联需去重，并保留已安装条目的实际路径。");
        var category = ReadEntry(merged.ArchivePath, "data.categoryGroups.json")[0]!["Categories"]![0]!["GalgamesX"]!.AsArray();
        Check(category.Count == 2, "分类关联未去重或误删其他游戏。");
        var staff = ReadEntry(merged.ArchivePath, "data.staff.json")[0]!["Games"]!.AsArray();
        Check(staff.Count == 2 && staff.Single(g => g!["GameId"]!.ToString() == fixture.Kept.Id.ToString())!["Relation"]!.AsArray().Count == 2,
            "制作人员关联应去重并保留不同关系。");
        Check(ReadEntry(merged.ArchivePath, "data.homepage_list.json")[0]!["GameIds"]!.AsArray().Count == 2, "主页扩展JSON引用应去重。");
        Check(!fixture.Settings.LibraryMappings.Single().SourceJson.Contains(fixture.Removed.Id.ToString()), "待映射库也必须迁移引用。");
        Check(await File.ReadAllTextAsync(fixture.Sentinel) == "do-not-touch", "本机合并误改真实游戏文件。");
    }

    private static async Task SelectedOverlayAfterMergeAsync()
    {
        var fixture = await MergeFixtureAsync();
        var cloud = Game(fixture.Removed.Id, "云端新名称", source: null);
        var source = NativeGame(cloud, "云端评价"); source["PlayCount"] = 900;
        var package = await SoftwareBackupCodec.CreateAsync(await NativeAsync([source]),
            new BackupSettings { Content = BackupContent.GameInfo | BackupContent.Reviews | BackupContent.PlayTime }, [cloud], Work());
        var merged = await SoftwareBackupCodec.MergeAsync(fixture.Current, package.DataPath!, null, BackupContent.Reviews,
            fixture.Settings, [fixture.Removed, fixture.Kept, fixture.Other], Work());
        Check(merged.Conflicts.Count == 0, "合并与部分资料覆盖不能互相冲突。");
        var target = FindGame(ReadEntry(merged.ArchivePath, "data.galgames.json").AsArray(), fixture.Kept.Id);
        Check(target["Comment"]!.ToString() == "云端评价", "已选评价没有应用。");
        foreach (var key in new[] { "Name", "ExePath", "DetectedSavePath", "LaunchArguments", "PlayCount" })
            Check(JsonNode.DeepEquals(target[key], fixture.KeptRaw[key]), "未选字段或本机配置被覆盖：" + key);
    }

    private static async Task PendingExclusionAsync()
    {
        var fixture = await SaveFixtureAsync(); var old = Game(directory: Work(), source: "v-save"); var storeRoot = Work();
        var store = new PendingRestoreStore(storeRoot); var profile = new ProviderProfile();
        await store.StageAsync(profile, fixture.Manifest, fixture.Archive);
        store.Items.Single().ExcludedLocalGameIds = [old.Id];
        store.Refresh([old], []);
        Check(store.Items.Single().LocalGameId is null && store.Items.Single().Status == RestoreStatus.PendingLink,
            "不关联不能重新按相同来源绑定原本本机条目。");
        await store.SaveAsync();
        var loaded = new PendingRestoreStore(storeRoot); await loaded.LoadAsync(); loaded.Refresh([old], []);
        Check(loaded.Items.Single().ExcludedLocalGameIds.SequenceEqual([old.Id]) && loaded.Items.Single().LocalGameId is null,
            "排除决定需要跨重启持久化。");
        loaded.Refresh([old], [Map(fixture.Game.Id, old.Id)]);
        Check(loaded.Items.Single().LocalGameId == old.Id && loaded.Items.Single().Status == RestoreStatus.Ready,
            "后续用户显式映射必须能覆盖先前不关联。");
    }

    private static async Task LinkThenManualRestoreAsync()
    {
        var fixture = await SaveFixtureAsync(); var old = Game(directory: Work(), source: "v-save"); var pendingRoot = Work();
        var store = new PendingRestoreStore(pendingRoot);
        await store.StageAsync(new ProviderProfile(), fixture.Manifest, fixture.Archive);
        var task = store.Items.Single(); task.ExcludedLocalGameIds = [old.Id];
        var newEntry = Game(fixture.Game.Id, source: "v-save");
        store.Refresh([old, newEntry], []);
        Check(task.LocalGameId == newEntry.Id && task.Status == RestoreStatus.PendingLink, "新设备云端条目可以识别，但未关联时不能恢复。");
        newEntry.LocalDirectory = Work();
        store.Refresh([old, newEntry], []);
        var target = Path.Combine(newEntry.LocalDirectory, "savedata", "slot.dat");
        Check(task.Status == RestoreStatus.Ready && !File.Exists(target), "链接只应显示可恢复，不得自动写入。");
        Check(await SaveArchiveService.RestoreAsync(task, store.ArchivePath(fixture.Manifest), newEntry, Work()), "手动恢复未写入存档。");
        Check(await File.ReadAllTextAsync(target) == "cloud-progress", "恢复目标未映射到新游戏目录。");
        Check(!await SaveArchiveService.RestoreAsync(task, store.ArchivePath(fixture.Manifest), newEntry, Work()), "重复恢复相同内容应跳过。");
        await File.WriteAllTextAsync(target, "new-progress");
        Check(await SaveArchiveService.RestoreAsync(task, store.ArchivePath(fixture.Manifest), newEntry, Work()), "已恢复状态不得阻止用户再次明确回退。");
    }

    private static async Task ClearPendingExclusionAsync()
    {
        var fixture = await SaveFixtureAsync(); var store = new PendingRestoreStore(Work());
        await store.StageAsync(new ProviderProfile(), fixture.Manifest, fixture.Archive);
        store.Items.Single().ExcludedLocalGameIds = [Guid.NewGuid()];
        await store.SaveAsync();
        await store.ClearAsync(store.Items.Select(i => i.Id).ToList());
        Check(store.Items.Count == 0 && store.GetStats().Bytes == 0, "清理必须同时删除排除决定及最后引用的暂存包。");
        Check(File.Exists(fixture.Archive) && await File.ReadAllTextAsync(fixture.SourceFile) == "cloud-progress",
            "清理待恢复任务不得影响云端模拟源包或真实存档。");
    }

    private static async Task<(HostExport Current, BackupSettings Settings, GameIdentity Removed, GameIdentity Kept,
        GameIdentity Other, JsonObject KeptRaw, string Sentinel)> MergeFixtureAsync()
    {
        var removed = Game(name: "重复资料", source: null); var kept = Game(name: "已安装资料", directory: Work(), source: null);
        var other = Game(name: "其他游戏", source: "v-other");
        var sentinel = Path.Combine(kept.LocalDirectory!, "game-file.txt"); await File.WriteAllTextAsync(sentinel, "do-not-touch");
        var keptRaw = NativeGame(kept, "本机评价"); keptRaw["LaunchArguments"] = "--local-argument"; keptRaw["PlayCount"] = 44;
        var library = new JsonArray(new JsonObject { ["Id"] = Guid.NewGuid().ToString(), ["Name"] = "本机库", ["Path"] = Work(),
            ["Galgames"] = new JsonArray(Member(removed.Id, "old-unlinked-path"), Member(kept.Id, kept.LocalDirectory!), Member(other.Id, "other-path")) });
        var categories = new JsonArray(new JsonObject { ["Id"] = Guid.NewGuid().ToString(), ["Categories"] = new JsonArray(
            new JsonObject { ["Id"] = Guid.NewGuid().ToString(), ["GalgamesX"] = new JsonArray(removed.Id.ToString(), kept.Id.ToString(), other.Id.ToString()) }) });
        JsonObject Relation(Guid id, string role) => new() { ["GameId"] = id.ToString(), ["Relation"] = new JsonArray(role) };
        var staff = new JsonArray(new JsonObject { ["Id"] = Guid.NewGuid().ToString(),
            ["Games"] = new JsonArray(Relation(removed.Id, "编剧"), Relation(kept.Id, "原画"), Relation(other.Id, "音乐")) });
        var home = new JsonArray(new JsonObject { ["_type"] = "GalgameManager.MultiStreamPage.Lists.GameListDbDto, GalgameManager",
            ["Name"] = "主页", ["GameIds"] = new JsonArray(GuidNode(removed.Id), GuidNode(kept.Id), GuidNode(other.Id)) });
        var current = await NativeAsync([NativeGame(removed), keptRaw, NativeGame(other)], library, categories, staff, home);
        var settings = new BackupSettings { ActiveRestoreMatching = new RestoreMatchingDecisions {
            Targets = { [removed.Id] = kept.Id }, Merges = [new(removed.Id, kept.Id)] },
            LibraryMappings = [new() { Id = "pending", SourceJson = library[0]!.ToJsonString() }] };
        return (current, settings, removed, kept, other, keptRaw, sentinel);
    }

    private static async Task<(GameIdentity Game, SaveManifest Manifest, string Archive, string SourceFile)> SaveFixtureAsync()
    {
        var game = Game(directory: Work(), source: "v-save"); var saveDirectory = Path.Combine(game.LocalDirectory!, "savedata");
        Directory.CreateDirectory(saveDirectory); var source = Path.Combine(saveDirectory, "slot.dat");
        await File.WriteAllTextAsync(source, "cloud-progress");
        var location = new SaveLocation { Id = "save-root", BaseKind = SaveBaseKind.GameRoot, RelativePath = "savedata" };
        var archive = Path.Combine(Work(), "save.zip");
        var manifest = (await SaveArchiveService.CreateAsync(game, [location], archive, Guid.NewGuid()))!;
        manifest.Id = "fixture";
        manifest.ManifestPath = $"v1/saves/{game.Id:N}/{manifest.DeviceId:N}/fixture/manifest.json";
        manifest.ArchivePath = $"v1/saves/{game.Id:N}/{manifest.DeviceId:N}/fixture/save.zip";
        return (game, manifest, archive, source);
    }

    private static async Task<HostExport> NativeAsync(IEnumerable<JsonObject> games, JsonArray? libraries = null,
        JsonArray? categories = null, JsonArray? staff = null, JsonArray? home = null)
    {
        var directory = Work(); var native = Path.Combine(directory, "native"); Directory.CreateDirectory(native);
        var documents = new Dictionary<string, JsonNode> {
            ["data.galgames.json"] = new JsonArray(games.Select(g => g.DeepClone()).ToArray()),
            ["data.galgameSources.json"] = libraries ?? new JsonArray(),
            ["data.categoryGroups.json"] = categories ?? new JsonArray(),
            ["data.staff.json"] = staff ?? new JsonArray(),
            ["data.homepage_list.json"] = home ?? new JsonArray(),
            ["data.multiStreamPageList.json"] = new JsonArray(),
            ["data.dataStatus.json"] = new JsonObject { ["ImportGalgame"] = false, ["GameLiteDbUpgrade"] = false }
        };
        foreach (var entry in documents) await File.WriteAllTextAsync(Path.Combine(native, entry.Key), entry.Value.ToJsonString());
        var archive = Path.Combine(directory, "native.zip"); await BackupFiles.ZipAsync(native, archive);
        return new HostExport { ArchivePath = archive, Settings = new() { ["language"] = JsonValue.Create("zh-CN") } };
    }

    private static async Task DeclinedAncillaryReferencesAsync()
    {
        var id = Guid.NewGuid(); var cloud = Game(id, source: "v-cloud"); var local = Game(id, source: "v-local");
        var category = new JsonArray(new JsonObject { ["Id"] = "category-group", ["Categories"] = new JsonArray(
            new JsonObject { ["Id"] = "category", ["GalgamesX"] = new JsonArray(cloud.Id.ToString()) }) });
        var staff = new JsonArray(new JsonObject { ["Id"] = "staff", ["Games"] = new JsonArray(new JsonObject { ["GameId"] = cloud.Id.ToString() }) });
        var home = new JsonArray(new JsonObject { ["_type"] = "GalgameManager.MultiStreamPage.Lists.GameListDbDto, GalgameManager",
            ["GameIds"] = new JsonArray(GuidNode(cloud.Id)) });
        var types = BackupContent.LibrariesAndCategories | BackupContent.Staff | BackupContent.Home;
        var package = await SoftwareBackupCodec.CreateAsync(await NativeAsync([NativeGame(cloud)], categories: category, staff: staff, home: home),
            new BackupSettings { Content = types }, [cloud], Work());
        var settings = new BackupSettings { ActiveRestoreMatching = new RestoreMatchingDecisions { Targets = { [id] = null } } };
        var merged = await SoftwareBackupCodec.MergeAsync(await NativeAsync([NativeGame(local)]), package.DataPath!, null, types,
            settings, [local], Work());
        Check(merged.Conflicts.Count == 0, "未选游戏信息的不关联应跳过创建并继续所选数据。");
        Check(ReadEntry(merged.ArchivePath, "data.galgames.json").AsArray().Count == 1, "未选游戏信息不能额外创建资料。");
        Check(ReadEntry(merged.ArchivePath, "data.categoryGroups.json")[0]!["Categories"]![0]!["GalgamesX"]!.AsArray().Count == 0 &&
            ReadEntry(merged.ArchivePath, "data.staff.json")[0]!["Games"]!.AsArray().Count == 0 &&
            ReadEntry(merged.ArchivePath, "data.homepage_list.json")[0]!["GameIds"]!.AsArray().Count == 0,
            "拒绝关联的云端引用不能仅因相同UUID串入本机条目。");
    }

    private static async Task SettingsOnlyMalformedGamesAsync()
    {
        var fixture = await MergeFixtureAsync(); var cloud = Game(fixture.Removed.Id, source: null);
        var package = await SoftwareBackupCodec.CreateAsync(await NativeAsync([NativeGame(cloud)]),
            new BackupSettings { Content = BackupContent.Settings | BackupContent.GameInfo }, [cloud], Work());
        using (var zip = ZipFile.Open(package.DataPath!, ZipArchiveMode.Update))
        {
            zip.GetEntry("games.json")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("games.json").Open()); writer.Write("{malformed-games");
        }
        fixture.Settings.GameMappings.Add(Map(Guid.NewGuid(), fixture.Kept.Id));
        fixture.Settings.LibraryMappings[0].TargetPath = Work();
        var before = JsonSerializer.Serialize(fixture.Settings, BackupJson.Options);
        var merged = await SoftwareBackupCodec.MergeAsync(fixture.Current, package.DataPath!, null, BackupContent.Settings,
            fixture.Settings, [fixture.Removed, fixture.Kept, fixture.Other], Work());
        Check(merged.Conflicts.Count == 0 && merged.Settings["language"]!.ToString() == "zh-CN", "单独恢复设置不应解析无关损坏游戏负载。");
        Check(ReadEntry(merged.ArchivePath, "data.galgames.json").AsArray().Count == 3, "单独恢复设置不应执行临时游戏合并。");
        Check(JsonNode.DeepEquals(ReadEntry(merged.ArchivePath, "data.galgameSources.json"),
            ReadEntry(fixture.Current.ArchivePath, "data.galgameSources.json")), "单独恢复设置不应应用库目录映射。");
        Check(JsonSerializer.Serialize(fixture.Settings, BackupJson.Options) == before, "单独恢复设置修改了持久游戏或游戏库映射。");
    }

    private static async Task CoordinatorSavesOnlyAsync()
    {
        var source = await SaveFixtureAsync(); var remote = new MemoryProvider();
        var original = Context([source.Game], remote);
        original.Settings.Content = BackupContent.GameInfo | BackupContent.Saves;
        original.Settings.SaveConfigs = [new() { GameId = source.Game.Id, Locations = source.Manifest.Locations }];
        var snapshot = await original.Coordinator.BackupAsync();
        var old = Game(name: "原本已安装条目", directory: Work(), source: "v-save");
        var target = Context([old], remote);
        target.Settings.GameMappings.Add(Map(source.Game.Id, old.Id));
        await target.Store.SaveAsync(target.Settings);
        var decisions = new RestoreMatchingDecisions { Targets = { [source.Game.Id] = null } };
        await target.Coordinator.StageRestoreAsync(snapshot, BackupContent.Saves, default, decisions);
        var pending = target.Pending.Items.Single();
        Check(pending.ExcludedLocalGameIds.Contains(old.Id) && pending.LocalGameId is null && pending.Status == RestoreStatus.PendingLink,
            "协调器必须把不关联决定应用到存档单独恢复。");
        Check(target.Host.ImportCalls == 0 && target.Settings.GameMappings.All(m => m.BackupGameId != source.Game.Id),
            "未请求资料导入时不能创建条目或重新学回拒绝的映射。");
        var afterRestart = new PendingRestoreStore(target.Root); await afterRestart.LoadAsync();
        afterRestart.Refresh([old], (await target.Store.LoadAsync()).GameMappings);
        Check(afterRestart.Items.Single().LocalGameId is null, "重启不能遗忘不关联决定。");
        Check(!File.Exists(Path.Combine(old.LocalDirectory!, "savedata", "slot.dat")), "协调器把暂存存档写入了被拒绝的本地目标。");
    }

    private static async Task CoordinatorFailedDownloadAsync()
    {
        var source = await SaveFixtureAsync(); var remote = new MemoryProvider();
        var original = Context([source.Game], remote); original.Settings.Content = BackupContent.GameInfo | BackupContent.Saves;
        original.Settings.SaveConfigs = [new() { GameId = source.Game.Id, Locations = source.Manifest.Locations }];
        var snapshot = await original.Coordinator.BackupAsync();
        var saveManifest = JsonSerializer.Deserialize<SaveManifest>(remote.Files[snapshot.SaveManifestPaths.Single()], BackupJson.Options)!;
        string saveObject = IncrementalArchiveStore.ObjectPaths(saveManifest.Recipe!).First();
        remote.Files[saveObject] = [0x01, 0x02];
        var local = Game(directory: Work(), source: null); var target = Context([local], remote);
        var unrelated = Map(Guid.NewGuid(), local.Id); target.Settings.GameMappings = [unrelated];
        await target.Store.SaveAsync(target.Settings);
        var before = JsonSerializer.Serialize(target.Settings, BackupJson.Options);
        var decisions = new RestoreMatchingDecisions { Targets = { [source.Game.Id] = local.Id } };
        await ThrowsAsync<InvalidDataException>(() => target.Coordinator.StageRestoreAsync(snapshot, snapshot.Content, default, decisions));
        Check(JsonSerializer.Serialize(target.Settings, BackupJson.Options) == before &&
            JsonSerializer.Serialize(await target.Store.LoadAsync(), BackupJson.Options) == before,
            "校验失败不能提交预览产生的别名或合并配置。");
        Check(target.Host.ImportCalls == 0, "存档校验失败前不能先提交软件导入。");
    }

    private static async Task CoordinatorCancelledAsync()
    {
        var source = await SaveFixtureAsync(); var remote = new MemoryProvider();
        var original = Context([source.Game], remote); original.Settings.Content = BackupContent.GameInfo | BackupContent.Saves;
        original.Settings.SaveConfigs = [new() { GameId = source.Game.Id, Locations = source.Manifest.Locations }];
        var snapshot = await original.Coordinator.BackupAsync();
        var local = Game(directory: Work(), source: null); var target = Context([local], remote);
        target.Settings.GameMappings = [Map(Guid.NewGuid(), local.Id)]; await target.Store.SaveAsync(target.Settings);
        var before = JsonSerializer.Serialize(target.Settings, BackupJson.Options);
        using var cancellation = new CancellationTokenSource();
        var cloudSave = JsonSerializer.Deserialize<SaveManifest>(remote.Files[snapshot.SaveManifestPaths.Single()], BackupJson.Options)!;
        HashSet<string> saveObjects = IncrementalArchiveStore.ObjectPaths(cloudSave.Recipe!).ToHashSet(StringComparer.Ordinal);
        remote.BeforeDownload = path => { if (saveObjects.Contains(path)) cancellation.Cancel(); };
        var decisions = new RestoreMatchingDecisions { Targets = { [source.Game.Id] = local.Id } };
        await ThrowsAsync<OperationCanceledException>(() => target.Coordinator.StageRestoreAsync(snapshot, snapshot.Content, cancellation.Token, decisions));
        Check(JsonSerializer.Serialize(target.Settings, BackupJson.Options) == before &&
            JsonSerializer.Serialize(await target.Store.LoadAsync(), BackupJson.Options) == before && target.Host.ImportCalls == 0,
            "取消恢复不能留下新的持久映射或提交宿主导入。");
    }

    private static (BackupCoordinator Coordinator, BackupSettings Settings, PendingRestoreStore Pending,
        SettingsStore Store, FakeHost Host, string Root) Context(List<GameIdentity> games, MemoryProvider remote)
    {
        var root = Work(); var profile = new ProviderProfile(); var settings = new BackupSettings { Profiles = [profile], ActiveProfileId = profile.Id };
        var host = new FakeHost(games); var store = new SettingsStore(root); var pending = new PendingRestoreStore(root);
        return (new BackupCoordinator(root, host, settings, store, pending, _ => remote), settings, pending, store, host, root);
    }

    private sealed class FakeHost(List<GameIdentity> games) : IBackupHost
    {
        public int ImportCalls { get; private set; }
        public JsonArray? ImportedGames { get; private set; }
        public IReadOnlyList<GameIdentity> GetGames() => games;
        public Task<HostExport> ExportAsync(string workDirectory, CancellationToken cancellationToken) => NativeAsync(games.Select(g => NativeGame(g)));
        public Task StageImportAsync(string mergedArchivePath, Dictionary<string, JsonNode?> settings, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); ImportedGames = ReadEntry(mergedArchivePath, "data.galgames.json").AsArray(); ImportCalls++; return Task.CompletedTask; }
    }

    private sealed class MemoryProvider : IBackupProvider
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public Action<string>? BeforeDownload { get; set; }
        public void Dispose() { }
        public Task AuthorizeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ProbeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(Files.ContainsKey(path));
        public Task<IReadOnlyList<RemoteItem>> ListAsync(string directory, CancellationToken cancellationToken = default)
        {
            var prefix = directory.TrimEnd('/') + "/";
            IReadOnlyList<RemoteItem> result = Files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .Select(k => k[prefix.Length..].Split('/')[0]).Distinct().Select(name => {
                    var path = prefix + name; return new RemoteItem(path, !Files.ContainsKey(path), Files.TryGetValue(path, out var data) ? data.Length : 0);
                }).ToList();
            return Task.FromResult(result);
        }
        public async Task UploadAsync(string path, string localFile, CancellationToken cancellationToken = default) =>
            Files[path] = await File.ReadAllBytesAsync(localFile, cancellationToken);
        public async Task DownloadAsync(string path, string localFile, CancellationToken cancellationToken = default)
        {
            BeforeDownload?.Invoke(path); cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(localFile)!);
            await File.WriteAllBytesAsync(localFile, Files[path], cancellationToken);
        }
        public Task DeleteAsync(string path, CancellationToken cancellationToken = default) { Files.Remove(path); return Task.CompletedTask; }
    }

    private static async Task RepeatedNoAssociationClearsOverrideAsync()
    {
        var fixture = await SaveFixtureAsync(); var store = new PendingRestoreStore(Work()); var profile = new ProviderProfile();
        await store.StageAsync(profile, fixture.Manifest, fixture.Archive);
        var pending = store.Items.Single();
        pending.LocalGameId = null;
        pending.TargetOverride = Path.Combine(Work(), "old-save-target");
        pending.TargetPath = pending.TargetOverride;
        pending.Error = "old-error";
        pending.Status = RestoreStatus.Failed;
        await store.StageAsync(profile, fixture.Manifest, fixture.Archive, default, [Guid.NewGuid()], null);
        Check(pending.TargetOverride is null && pending.TargetPath is null && pending.Error is null && pending.Status == RestoreStatus.PendingLink,
            "再次选择不关联必须清除可能属于旧关联的路径，即使旧LocalGameId已经丢失。");
        var newEntry = Game(fixture.Game.Id, directory: Work(), source: "v-save");
        store.Refresh([newEntry], []);
        Check(pending.TargetPath == Path.Combine(newEntry.LocalDirectory!, "savedata"), "新关联不得继承旧游戏的自定义目标。");
    }

    private static async Task CoordinatorSavesOnlyMergeAsync()
    {
        var source = await SaveFixtureAsync(); var remote = new MemoryProvider();
        var original = Context([source.Game], remote); original.Settings.Content = BackupContent.Saves;
        original.Settings.SaveConfigs = [new() { GameId = source.Game.Id, Locations = source.Manifest.Locations }];
        var snapshot = await original.Coordinator.BackupAsync();
        var removed = Game(source.Game.Id, "先前恢复的非本地资料", source: null);
        var kept = Game(name: "原有已安装条目", directory: Work(), source: null);
        var sentinel = Path.Combine(kept.LocalDirectory!, "game.exe"); await File.WriteAllTextAsync(sentinel, "local-executable");
        var target = Context([removed, kept], remote);
        var historical = Guid.NewGuid();
        target.Settings.GameMappings = [Map(historical, removed.Id)];
        target.Settings.SelectedGameIds = [removed.Id];
        target.Settings.SaveConfigs = [new() { GameId = removed.Id, Locations = [Location("migrate")] }];
        var decisions = RestoreMatchingPreview.Decide(RestoreMatchingPreview.Analyze(snapshot.Games, [removed, kept], target.Settings.GameMappings),
            new Dictionary<Guid, Guid?> { [kept.Id] = source.Game.Id });
        await target.Coordinator.StageRestoreAsync(snapshot, BackupContent.Saves, default, decisions);
        Check(target.Host.ImportCalls == 1 && target.Host.ImportedGames!.Count == 1 && FindGame(target.Host.ImportedGames, kept.Id) is not null,
            "存档单独恢复确认了合并时仍应生成去重资料导入。");
        var pending = target.Pending.Items.Single();
        Check(pending.LocalGameId == kept.Id && pending.Status == RestoreStatus.Ready && pending.TargetOverride is null,
            "已确认合并后的待恢复存档应关联保留的本机条目。");
        Check(target.Settings.GameMappings.Single(m => m.BackupGameId == historical).LocalGameId == kept.Id &&
            target.Settings.SelectedGameIds.SequenceEqual([kept.Id]) && target.Settings.SaveConfigs.Single().GameId == kept.Id,
            "协调器没有提交合并的别名、游戏范围和存档规则迁移。");
        Check(await File.ReadAllTextAsync(sentinel) == "local-executable" && !File.Exists(Path.Combine(kept.LocalDirectory!, "savedata", "slot.dat")),
            "合并资料不应删除游戏文件或自动恢复暂存存档。");
    }

    private static async Task CoordinatorNewDeviceAsync()
    {
        var source = await SaveFixtureAsync(); var remote = new MemoryProvider();
        var original = Context([source.Game], remote); original.Settings.Content = BackupContent.GameInfo | BackupContent.Saves;
        original.Settings.SaveConfigs = [new() { GameId = source.Game.Id, Locations = source.Manifest.Locations }];
        var snapshot = await original.Coordinator.BackupAsync();
        var fresh = Context([], remote);
        var decisions = RestoreMatchingPreview.Decide(RestoreMatchingPreview.Analyze(snapshot.Games, [], []), new Dictionary<Guid, Guid?>());
        await fresh.Coordinator.StageRestoreAsync(snapshot, snapshot.Content, default, decisions);
        Check(fresh.Host.ImportCalls == 1 && fresh.Host.ImportedGames!.Count == 1, "新设备应正常导入未关联的云端游戏资料。");
        var game = FindGame(fresh.Host.ImportedGames!, source.Game.Id);
        Check(game["ExePath"] is null && game["DetectedSavePath"] is null, "新设备不能复制原机启动与存档绝对路径。");
        Check(fresh.Pending.Items.Single().Status == RestoreStatus.PendingLink && fresh.Pending.Items.Single().LocalGameId is null,
            "未关联游戏的存档必须留在待恢复区。");
        var loaded = new PendingRestoreStore(fresh.Root); await loaded.LoadAsync();
        loaded.Refresh([Game(source.Game.Id, source: "v-save")], []);
        Check(loaded.Items.Single().LocalGameId == source.Game.Id && loaded.Items.Single().Status == RestoreStatus.PendingLink,
            "重启导入后云端条目可识别，但尚未链接游戏目录仍不能恢复。");
    }
    private static async Task PendingHistoryRetargetAsync()
    {
        var fixture = await SaveFixtureAsync(); var storeRoot = Work(); var store = new PendingRestoreStore(storeRoot);
        var profile = new ProviderProfile();
        await store.StageAsync(profile, fixture.Manifest, fixture.Archive);
        var older = JsonSerializer.Deserialize<SaveManifest>(JsonSerializer.Serialize(fixture.Manifest, BackupJson.Options), BackupJson.Options)!;
        older.Id = "older";
        older.ManifestPath = $"v1/saves/{older.Game.Id:N}/{older.DeviceId:N}/older/manifest.json";
        older.ArchivePath = $"v1/saves/{older.Game.Id:N}/{older.DeviceId:N}/older/save.zip";
        await store.StageAsync(profile, older, fixture.Archive);
        var localA = Game(directory: Work(), source: null); var localB = Game(directory: Work(), source: null);
        store.Refresh([localA, localB], [Map(fixture.Game.Id, localA.Id)]);
        for (var i = 0; i < store.Items.Count; i++)
        {
            var item = store.Items[i];
            item.TargetOverride = Path.Combine(localA.LocalDirectory!, "custom-version-" + i);
            item.TargetPath = item.TargetOverride;
            item.Error = "old-target-error";
            item.Status = i == 0 ? RestoreStatus.Restored : RestoreStatus.Failed;
        }
        store.Refresh([localA, localB], [Map(fixture.Game.Id, localB.Id)]);
        Check(store.Items.Count == 2 && store.Items.All(i => i.LocalGameId == localB.Id && i.TargetOverride is null &&
            i.TargetPath == Path.Combine(localB.LocalDirectory!, "savedata") && i.Error is null && i.Status == RestoreStatus.Ready),
            "全局游戏映射改到B后，每个历史版本均需遗忘A路径和旧恢复结果。");
        await store.SaveAsync();
        var loaded = new PendingRestoreStore(storeRoot); await loaded.LoadAsync();
        Check(loaded.Items.All(i => i.LocalGameId == localB.Id && i.TargetOverride is null && i.Status == RestoreStatus.Ready),
            "新的关联目标与已清理路径必须跨重启保持。");
    }

    private static async Task PendingSameTargetPreservesOverridesAsync()
    {
        var fixture = await SaveFixtureAsync(); var store = new PendingRestoreStore(Work()); var profile = new ProviderProfile();
        await store.StageAsync(profile, fixture.Manifest, fixture.Archive);
        var other = JsonSerializer.Deserialize<SaveManifest>(JsonSerializer.Serialize(fixture.Manifest, BackupJson.Options), BackupJson.Options)!;
        other.Id = "other-history";
        other.ManifestPath = $"v1/saves/{other.Game.Id:N}/{other.DeviceId:N}/other-history/manifest.json";
        other.ArchivePath = $"v1/saves/{other.Game.Id:N}/{other.DeviceId:N}/other-history/save.zip";
        await store.StageAsync(profile, other, fixture.Archive);
        var local = Game(directory: Work(), source: null); var mappings = new List<GameMatchMapping> { Map(fixture.Game.Id, local.Id) };
        store.Refresh([local], mappings);
        var expectedPaths = new Dictionary<Guid, string>(); var expectedStatuses = new Dictionary<Guid, RestoreStatus>();
        for (var i = 0; i < store.Items.Count; i++)
        {
            var item = store.Items[i];
            item.TargetOverride = Path.Combine(local.LocalDirectory!, "chosen-version-" + i);
            item.TargetPath = item.TargetOverride;
            item.Status = i == 0 ? RestoreStatus.Restored : RestoreStatus.Failed;
            expectedPaths[item.Id] = item.TargetOverride;
            expectedStatuses[item.Id] = item.Status;
        }
        store.Refresh([local], mappings);
        Check(store.Items.All(i => i.LocalGameId == local.Id && i.TargetOverride == expectedPaths[i.Id] &&
            i.TargetPath == expectedPaths[i.Id] && i.Status == expectedStatuses[i.Id]),
            "关联游戏没有改变时，刷新不能擦除用户为各版本指定的路径或历史结果。");
    }
    private static async Task CoordinatorImportPreflightAsync()
    {
        var cloud = Game(source: "v-cloud"); var remote = new MemoryProvider(); var original = Context([cloud], remote);
        original.Settings.Content = BackupContent.GameInfo;
        var snapshot = await original.Coordinator.BackupAsync();
        var local = Game(directory: Work(), source: null); var unrelatedRunning = Game(source: "v-unrelated");
        unrelatedRunning.IsRunning = true;
        var target = Context([local, unrelatedRunning], remote);
        target.Settings.GameMappings = [Map(Guid.NewGuid(), local.Id)];
        await target.Store.SaveAsync(target.Settings);
        var before = JsonSerializer.Serialize(target.Settings, BackupJson.Options);
        var decisions = new RestoreMatchingDecisions { Targets = { [cloud.Id] = local.Id } };
        await ThrowsAsync<InvalidOperationException>(() => target.Coordinator.StageRestoreAsync(snapshot, BackupContent.GameInfo, default, decisions));
        Check(target.Host.ImportCalls == 0 && JsonSerializer.Serialize(target.Settings, BackupJson.Options) == before &&
            JsonSerializer.Serialize(await target.Store.LoadAsync(), BackupJson.Options) == before,
            "宿主因运行中的游戏拒绝导入时，预览映射不能提前提交。");
    }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static JsonObject NativeGame(GameIdentity game, string comment = "云端评价") => new()
    {
        ["Uuid"] = game.Id.ToString(), ["Name"] = new JsonObject { ["Value"] = game.Name, ["IsLock"] = false },
        ["Ids"] = new JsonArray(game.SourceIds.GetValueOrDefault("Vndb"), game.SourceIds.GetValueOrDefault("Bangumi"), null, null, null, null, null, null),
        ["Comment"] = comment, ["PlayCount"] = 4, ["TotalPlayTime"] = 120,
        ["ExePath"] = Path.Combine(game.LocalDirectory ?? "C:\\old", "game.exe"),
        ["DetectedSavePath"] = new JsonObject { ["ParsedPath"] = "%GameRoot%\\savedata" }
    };
    private static JsonObject FindGame(JsonArray games, Guid id) => games.OfType<JsonObject>().Single(g => g["Uuid"]!.ToString() == id.ToString());
    private static JsonObject GuidNode(Guid id) => new() { ["$guid"] = id.ToString() };
    private static JsonObject Member(Guid id, string path) => new() { ["Galgame"] = id.ToString(), ["Path"] = path };
    private static SaveLocation Location(string id) => new() { Id = id, BaseKind = SaveBaseKind.GameRoot, RelativePath = "savedata/" + id };
    private static GameMatchMapping Map(Guid backup, Guid local) => new() { BackupGameId = backup, LocalGameId = local };
    private static GameIdentity Game(Guid? id = null, string name = "测试游戏", string? directory = null, string? source = "v1") => new()
    { Id = id ?? Guid.NewGuid(), Name = name, LocalDirectory = directory, SourceIds = source is null ? [] : new() { ["Vndb"] = source } };
    private static string Work() { var path = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static JsonNode ReadEntry(string archive, string path)
    {
        using var zip = ZipFile.OpenRead(archive); using var reader = new StreamReader(zip.GetEntry(path)!.Open());
        return JsonNode.Parse(reader.ReadToEnd())!;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
