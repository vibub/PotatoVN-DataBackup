using System;
using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;

namespace PotatoVN.App.PluginBase.Backup;

/// <summary>使用宿主自己的Newtonsoft，保证宿主模型的JsonIgnore与转换器跨加载上下文仍生效。</summary>
internal sealed class HostJsonWriter(Assembly jsonAssembly)
{
    public JsonNode? Serialize(object? value, params object[] converters)
    {
        if (value == null) return null;
        Type settingsType = jsonAssembly.GetType("Newtonsoft.Json.JsonSerializerSettings", true)!;
        object settings = Activator.CreateInstance(settingsType)!;
        IList list = (IList)settingsType.GetProperty("Converters")!.GetValue(settings)!;
        foreach (object converter in converters) list.Add(converter);
        MethodInfo method = jsonAssembly.GetType("Newtonsoft.Json.JsonConvert", true)!.GetMethod("SerializeObject", [typeof(object), settingsType])!;
        string json = (string)method.Invoke(null, [value, settings])!;
        return JsonNode.Parse(json);
    }
}
