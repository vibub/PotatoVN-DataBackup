using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PotatoVN.App.PluginBase.Backup;

// 设置中的JArray/JObject同样属于宿主加载上下文，读写均使用宿主自己的Newtonsoft。
internal sealed class HostSettingsAccess(object settings, Assembly jsonAssembly)
{
    private readonly HostJsonWriter _json = new(jsonAssembly);

    public async Task<JsonNode?> ReadAsync(string key) => _json.Serialize(await ReadValueAsync(key, typeof(object)));

    public async Task<object?> ReadValueAsync(string key, Type type, bool isLarge = false)
    {
        MethodInfo method = settings.GetType().GetMethods().Single(method => method.Name == "ReadSettingAsync" && method.IsGenericMethodDefinition);
        Task task = (Task)method.MakeGenericMethod(type).Invoke(settings, [key, isLarge, null, false])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }

    // 商店版 v1.10.2.0 的本机导入会用这组字段恢复排序。手动顺序是大设置。
    public async Task<JsonObject> ReadImportPageSettingsAsync(CancellationToken ct)
    {
        (string Field, string Key, Type Type)[] fields =
        [
            ("PrimarySortKey", "primarySortKey", typeof(int)),
            ("PrimarySortDescending", "PrimarySortDescending", typeof(bool)),
            ("SecondarySortKey", "SecondarySortKey", typeof(int)),
            ("SecondarySortDescending", "SecondarySortDescending", typeof(bool)),
            ("CustomSortOrder", "customSortOrder", typeof(List<string>)),
            ("LibrarySortKey", "LibrarySortKey", typeof(int)),
            ("LibraryGameSortDescending", "LibraryGameSortDescending", typeof(bool)),
            ("LibraryFolderSortKey", "LibraryFolderSortKey", typeof(int)),
            ("LibraryFolderSortDescending", "LibraryFolderSortDescending", typeof(bool))
        ];
        var page = new JsonObject();
        foreach (var (field, key, type) in fields)
        {
            ct.ThrowIfCancellationRequested();
            page[field] = _json.Serialize(await ReadValueAsync(key, type, isLarge: key == "customSortOrder"));
        }
        return page;
    }

    public async Task WriteAsync(string key, JsonNode? value)
    {
        Task task;
        if (value == null)
        {
            // 未设置与JSON null语义不同：后者会令宿主读取非空枚举/布尔值时失败。
            MethodInfo method = settings.GetType().GetMethod("RemoveSettingAsync", [typeof(string), typeof(bool)])!;
            task = (Task)method.Invoke(settings, [key, false])!;
        }
        else
        {
            MethodInfo method = settings.GetType().GetMethods().Single(method => method.Name == "SaveSettingAsync" && method.IsGenericMethodDefinition);
            task = (Task)method.MakeGenericMethod(typeof(object)).Invoke(settings, [key, _json.Deserialize(value), false, false, null, false])!;
        }
        await task;
    }
}
