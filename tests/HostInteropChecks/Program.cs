using System.IO.Compression;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Runtime.Loader;
using LiteDB;
using PotatoVN.App.PluginBase.Backup;

string root = Path.Combine(Path.GetTempPath(), "PotatoVNHostInteropChecks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    string databasePath = Path.Combine(root, "synthetic.db");
    BsonDocument sample = new()
    {
        ["_id"] = Guid.NewGuid(), ["_type"] = "GalgameManager.MultiStreamPage.Lists.GameListDbDto, GalgameManager",
        ["title"] = "合成主页：中文与表情 🎮", ["objectId"] = ObjectId.NewObjectId(),
        ["binary"] = new byte[] { 0, 2, 128, 255 }, ["long"] = long.MaxValue,
        ["decimal"] = 123.456789m, ["when"] = new DateTime(2026, 11, 12, 1, 2, 3, DateTimeKind.Utc),
        ["nested"] = new BsonDocument { ["items"] = new BsonArray { Guid.NewGuid(), BsonValue.Null, "中文" } }
    };
    using (var source = new LiteDatabase(databasePath))
    {
        source.GetCollection("homepage_list").Insert(sample);
        source.GetCollection("plugin_state").Insert(new BsonDocument { ["_id"] = "plugin", ["enabled"] = true, ["data"] = sample });
    }
    using (var source = new LiteDatabase(databasePath))
    {
        var reader = new HostDatabaseReader(source);
        Check(reader.CollectionExists("homepage_list") && !reader.CollectionExists("absent"), "同上下文集合检查失败");
        Check(reader.ReadDocuments("homepage_list").Single()["title"] == sample["title"], "同上下文读取失败");
    }
    Console.WriteLine("PASS same-context database access");

    var context = new AssemblyLoadContext("synthetic-host-LiteDB", isCollectible: true);
    try
    {
        using Stream bytes = File.OpenRead(typeof(LiteDatabase).Assembly.Location);
        Assembly hostAssembly = context.LoadFromStream(bytes);
        Type databaseType = hostAssembly.GetType("LiteDB.LiteDatabase", true)!;
        Type mapperType = hostAssembly.GetType("LiteDB.BsonMapper", true)!;
        using IDisposable hostDatabase = (IDisposable)databaseType.GetConstructor([typeof(string), mapperType])!.Invoke([databasePath, null]);
        bool castFailed = false;
        try { _ = (LiteDatabase)hostDatabase; }
        catch (InvalidCastException) { castFailed = true; }
        Check(castFailed, "未复现截图中的跨加载上下文类型转换错误");
        Console.WriteLine("PASS reproduced identical-version cross-context InvalidCastException");

        var reader = new HostDatabaseReader(hostDatabase);
        Check(reader.CollectionNames().Order().SequenceEqual(new[] { "homepage_list", "plugin_state" }), "跨上下文集合名称丢失");
        Check(reader.CollectionExists("homepage_list") && !reader.CollectionExists("absent"), "跨上下文集合检查失败");
        BsonDocument restored = reader.ReadDocuments("homepage_list").Single();
        Check(JsonSerializer.Serialize(restored) == JsonSerializer.Serialize(sample), "BSON 扩展类型、嵌套结构或中文发生变化");
        Console.WriteLine("PASS cross-context homepage and BSON values preserved");

        using var target = new LiteDatabase(Path.Combine(root, "prepared-import.db"));
        foreach (string name in reader.CollectionNames())
            target.GetCollection(name).InsertBulk(reader.ReadDocuments(name));
        Check(target.GetCollection("plugin_state").FindById("plugin")["enabled"].AsBoolean, "恢复准备丢失插件启用记录");
        Check(JsonSerializer.Serialize(target.GetCollection("homepage_list").FindAll().Single()) == JsonSerializer.Serialize(sample), "导入数据库主页不一致");
        Console.WriteLine("PASS cross-context import copies plugin records without modifying host database");
    }
    finally { context.Unload(); }
    var jsonContext = new JsonContext(typeof(Newtonsoft.Json.JsonConvert).Assembly.Location);
    try
    {
        Assembly models = jsonContext.LoadFromAssemblyPath(typeof(SyntheticJsonRecord).Assembly.Location);
        object model = Activator.CreateInstance(models.GetType(nameof(SyntheticJsonRecord), true)!)!;
        Assembly json = jsonContext.LoadFromAssemblyName(typeof(Newtonsoft.Json.JsonConvert).Assembly.GetName());
        var writer = new HostJsonWriter(json);
        var result = writer.Serialize(model)!;
        Check(result["Name"]!.GetValue<string>() == "合成资料" && result["Hidden"] == null && result["Self"] == null, "跨上下文JsonIgnore失效或发生循环引用");
        Console.WriteLine("PASS host Newtonsoft honors cross-context model JsonIgnore and avoids self reference");
        object foreignSettings = Activator.CreateInstance(models.GetType(nameof(SyntheticHostSettings), true)!)!;
        var values = (Dictionary<string, string>)foreignSettings.GetType().GetProperty("Values")!.GetValue(foreignSettings)!;
        var settings = new HostSettingsAccess(foreignSettings, json);
        values["language"] = "null";
        bool nullFailed = false;
        try { foreignSettings.GetType().GetMethod("ReadLanguage")!.Invoke(foreignSettings, null); }
        catch (TargetInvocationException error) when (error.InnerException?.GetType().FullName == "Newtonsoft.Json.JsonSerializationException") { nullFailed = true; }
        Check(nullFailed, "未复现JSON null令宿主非空语言枚举读取失败");
        await settings.WriteAsync("language", null);
        Check(!values.ContainsKey("language") && (int)foreignSettings.GetType().GetMethod("ReadLanguage")!.Invoke(foreignSettings, null)! == 0,
            "未配置语言应移除键并使用默认值，不能写入JSON null");
        Console.WriteLine("PASS reproduced null enum startup failure; unset restore removes key and keeps default readable");
        await settings.WriteAsync("sortKeys", JsonNode.Parse("[1,2]"));
        await settings.WriteAsync("ascending", JsonNode.Parse("[false,true]"));
        Check(values["sortKeys"] == "[1,2]" && values["ascending"] == "[false,true]", "跨上下文写入数组变成空数组");
        Console.WriteLine("PASS host-context restore preserves numeric and boolean arrays");
        values["nested"] = "{\"order\":[8,1,5],\"enabled\":{\"source\":true}}";
        JsonNode before = (await settings.ReadAsync("nested"))!;
        await settings.WriteAsync("nested", before);
        Check(JsonNode.DeepEquals(before, JsonNode.Parse(values["nested"])), "导出或回滚读取破坏嵌套JObject/JArray");
        Check((await settings.ReadAsync("absent")) == null, "未配置设置应保持空值语义");
        Console.WriteLine("PASS host-context export and rollback capture preserve nested settings and unset values");
        var largeValues = (Dictionary<string, string>)foreignSettings.GetType().GetProperty("LargeValues")!.GetValue(foreignSettings)!;
        largeValues["customSortOrder"] = "[\"game-b\",\"game-a\"]";
        values["primarySortKey"] = "3";
        values["PrimarySortDescending"] = "true";
        values["LibraryFolderSortKey"] = "1";
        JsonObject page = await settings.ReadImportPageSettingsAsync(CancellationToken.None);
        Check(page["PrimarySortKey"]?.GetValue<int>() == 3 && page["PrimarySortDescending"]?.GetValue<bool>() == true &&
            page["LibraryFolderSortKey"]?.GetValue<int>() == 1 &&
            JsonNode.DeepEquals(page["CustomSortOrder"], JsonNode.Parse(largeValues["customSortOrder"])),
            "本机页面排序底稿应保留真实枚举、降序值和大设置中的自定义顺序。");
        Console.WriteLine("PASS host-context import snapshot reads typed page settings and large custom order");
    }
    finally { jsonContext.Unload(); }
    CheckPluginStorage(root);
    int checks = CheckCrossVolumePluginStorage(root) ? 13 : 12;
    Console.WriteLine($"PASS {checks} host interop checks; synthetic data only");
    return 0;
}
finally
{
    string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PotatoVNHostInteropChecks")) + Path.DirectorySeparatorChar;
    if (Path.GetFullPath(root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
}
static void CheckPluginStorage(string root)
{
    string host = Path.Combine(root, "LocalState");
    string plugins = Path.Combine(host, "Plugins");
    string localAppData = Path.Combine(root, "AppData", "Local");
    string cache = Path.Combine(root, "Package", "LocalCache");
    string normalTemp = Path.Combine(root, "TempState");
    Guid pluginId = Guid.NewGuid();
    string nominal = PluginStorage.FindLegacyRoot(host, localAppData, cache);
    string legacy = Path.Combine(cache, "Local", "PotatoVN-DataBackup", Path.GetFileName(nominal));
    Put(legacy, "settings.json", "保留设备ID和渠道");
    Put(legacy, "secrets/fixture.bin", "合成DPAPI文件");
    Put(legacy, "pending/tasks.json", "待恢复索引");
    Put(legacy, "pending/archives/fixture.zip", "待恢复文件");
    Put(legacy, "rollback/save", "原始存档");
    Put(legacy, "import/withdrawn-import.zip", "撤回的原生导入文件");
    Put(legacy, "work/transient", "旧临时导出");
    Put(legacy, "host-temp/leave-here", "宿主兼容临时目录");
    Put(plugins, "插件安装目录/fixture.dll", "插件安装资源");
    Check(PluginStorage.FindLegacyRoot(host, localAppData, cache) == legacy, "未定位商店版重定向的旧目录");

    var storage = new PluginStorage(plugins, pluginId, legacy);
    string staging = storage.Root + ".migrating";
    Directory.CreateDirectory(staging);
    Directory.Move(Path.Combine(legacy, "pending"), Path.Combine(staging, "pending"));
    storage.Initialize(host, normalTemp);
    Check(storage.Root == Path.Combine(plugins, pluginId + ".data") &&
          File.ReadAllText(Path.Combine(storage.Root, "settings.json")) == "保留设备ID和渠道" &&
          File.Exists(Path.Combine(storage.Root, "secrets/fixture.bin")) &&
          File.Exists(Path.Combine(storage.Root, "pending/archives/fixture.zip")) &&
          File.Exists(Path.Combine(storage.Root, "rollback/save")) &&
          File.Exists(Path.Combine(storage.Root, "import/withdrawn-import.zip")),
        "迁移重试后缺失设置、凭据或恢复资料");
    Check(!File.Exists(Path.Combine(legacy, "settings.json")) &&
          Directory.Exists(Path.Combine(legacy, "work")) && Directory.Exists(Path.Combine(legacy, "host-temp")) &&
          !Directory.Exists(Path.Combine(storage.Root, "work")) && !Directory.Exists(staging),
        "迁移留下重复资料，或带入旧临时目录");
    storage.Initialize(host, normalTemp);
    Console.WriteLine("PASS stable host plugin directory and resumable migration of legacy MSIX state");

    Put(storage.Root, "settings-restore.json", "恢复标记");
    Put(storage.Root, "work/prepare-import/prepared.zip", "本次导入临时文件");
    string importPackage = Path.Combine(root, "plugin-installation-only.zip");
    using (var zip = ZipFile.Open(importPackage, ZipArchiveMode.Create))
        foreach (string file in storage.FilesForImport())
            zip.CreateEntryFromFile(file, "Plugins/" + Path.GetRelativePath(plugins, file).Replace('\\', '/'));
    using (var zip = ZipFile.OpenRead(importPackage))
        Check(zip.Entries.Single().FullName == "Plugins/插件安装目录/fixture.dll", "原生导入包含插件私有状态或遗漏安装文件");
    Console.WriteLine("PASS native import archive excludes private state and its own temporary files");

    // 模拟兼容重启器设置了外部 Temp，导入后又由平常的宿主进程启动。
    Directory.CreateDirectory(storage.ExternalTempRoot);
    string backup = Path.Combine(storage.ExternalTempRoot, "ImportBackup");
    Directory.Move(host, backup);
    ZipFile.ExtractToDirectory(importPackage, host);
    storage.Initialize(host, normalTemp);
    Check(File.ReadAllText(Path.Combine(storage.Root, "settings-restore.json")) == "恢复标记" &&
        File.Exists(Path.Combine(storage.Root, "pending/archives/fixture.zip")) &&
        File.Exists(Path.Combine(storage.Root, "secrets/fixture.bin")) &&
        File.Exists(Path.Combine(plugins, "插件安装目录/fixture.dll")) &&
        !File.Exists(Path.Combine(storage.Root, "work/prepare-import/prepared.zip")) &&
        File.Exists(Path.Combine(backup, "Plugins", pluginId + ".data", "work/prepare-import/prepared.zip")),
        "原生导入后没有接回数据，或把本次临时导出接回");
    Console.WriteLine("PASS private state survives native LocalState replacement and external Temp fallback");

    static void Put(string directory, string name, string value)
    {
        string path = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }
}
static bool CheckCrossVolumePluginStorage(string root)
{
    if (string.Equals(Path.GetPathRoot(root), Path.GetPathRoot(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
        return false;

    string portable = Path.Combine(AppContext.BaseDirectory, "plugin-storage-check-" + Guid.NewGuid().ToString("N"));
    string legacy = Path.Combine(root, "legacy-cross-volume");
    string pending = Path.Combine("pending", "archives", "fixture.zip");
    Directory.CreateDirectory(Path.Combine(legacy, "pending", "archives"));
    File.WriteAllText(Path.Combine(legacy, pending), "跨盘保留待恢复存档");
    try
    {
        var storage = new PluginStorage(Path.Combine(portable, "Plugins"), Guid.NewGuid(), legacy);
        storage.Initialize(portable, Path.Combine(root, "TempState"));
        Check(File.ReadAllText(Path.Combine(storage.Root, pending)) == "跨盘保留待恢复存档" &&
            !Directory.Exists(Path.Combine(legacy, "pending")), "跨盘迁移未完整保存，或发布后留下重复存档");
        Console.WriteLine("PASS cross-volume migration publishes all state before removing source copies");
        return true;
    }
    finally { if (Directory.Exists(portable)) Directory.Delete(portable, recursive: true); }
}
static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

public sealed class SyntheticJsonRecord
{
    public string Name => "合成资料";
    [Newtonsoft.Json.JsonIgnore] public string Hidden => "synthetic private field";
    [Newtonsoft.Json.JsonIgnore] public SyntheticJsonRecord Self => this;
}
sealed class JsonContext(string jsonPath) : AssemblyLoadContext(isCollectible: true)
{
    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name != "Newtonsoft.Json") return null;
        using Stream bytes = File.OpenRead(jsonPath);
        return LoadFromStream(bytes);
    }
}

public enum SyntheticLanguage { Auto, Chinese }
public sealed class SyntheticHostSettings
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> LargeValues { get; } = new(StringComparer.Ordinal);
    public async Task<T?> ReadSettingAsync<T>(string key, bool isLarge = false, List<Newtonsoft.Json.JsonConverter>? converters = null, bool typeNameHandling = false)
    {
        await Task.CompletedTask;
        return (isLarge ? LargeValues : Values).TryGetValue(key, out string? raw) ? Newtonsoft.Json.JsonConvert.DeserializeObject<T>(raw) : default;
    }
    public Task SaveSettingAsync<T>(string key, T value, bool isLarge = false, bool triggerEventWhenNull = false,
        List<Newtonsoft.Json.JsonConverter>? converters = null, bool typeNameHandling = false)
    { Values[key] = Newtonsoft.Json.JsonConvert.SerializeObject(value); return Task.CompletedTask; }
    public Task RemoveSettingAsync(string key, bool isLarge = false) { Values.Remove(key); return Task.CompletedTask; }
    public int ReadLanguage() => Values.TryGetValue("language", out string? raw) ? (int)Newtonsoft.Json.JsonConvert.DeserializeObject<SyntheticLanguage>(raw) : 0;
}
