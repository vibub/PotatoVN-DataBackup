using System.Reflection;
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
    }
    finally { jsonContext.Unload(); }
    Console.WriteLine("PASS 5 host interop checks; synthetic data only");
    return 0;
}
finally
{
    string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PotatoVNHostInteropChecks")) + Path.DirectorySeparatorChar;
    if (Path.GetFullPath(root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
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
