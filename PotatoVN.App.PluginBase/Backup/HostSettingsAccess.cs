using System;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace PotatoVN.App.PluginBase.Backup;

// 设置中的JArray/JObject同样属于宿主加载上下文，读写均使用宿主自己的Newtonsoft。
internal sealed class HostSettingsAccess(object settings, Assembly jsonAssembly)
{
    private readonly HostJsonWriter _json = new(jsonAssembly);

    public async Task<JsonNode?> ReadAsync(string key)
    {
        MethodInfo method = settings.GetType().GetMethods().Single(method => method.Name == "ReadSettingAsync" && method.IsGenericMethodDefinition);
        Task task = (Task)method.MakeGenericMethod(typeof(object)).Invoke(settings, [key, false, null, false])!;
        await task;
        return _json.Serialize(task.GetType().GetProperty("Result")!.GetValue(task));
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
