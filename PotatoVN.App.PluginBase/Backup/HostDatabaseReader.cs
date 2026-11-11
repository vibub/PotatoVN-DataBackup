using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LiteDB;

namespace PotatoVN.App.PluginBase.Backup;

/// <summary>宿主数据库对象不跨加载上下文强转；BSON 通过宿主自己的扩展 JSON 序列化传递。</summary>
internal sealed class HostDatabaseReader(object database)
{
    private readonly Type _type = database.GetType();

    public bool CollectionExists(string name) => (bool)_type.GetMethod("CollectionExists", [typeof(string)])!
        .Invoke(database, [name])!;

    public IEnumerable<string> CollectionNames() => (IEnumerable<string>)_type.GetMethod("GetCollectionNames")!
        .Invoke(database, null)!;

    public IEnumerable<BsonDocument> ReadDocuments(string name)
    {
        MethodInfo getCollection = _type.GetMethods().Single(method => method.Name == "GetCollection" &&
            !method.IsGenericMethod && method.GetParameters().Length == 2);
        object collection = getCollection.Invoke(database, [name, getCollection.GetParameters()[1].DefaultValue])!;
        IEnumerable records = (IEnumerable)collection.GetType().GetMethod("FindAll")!.Invoke(collection, null)!;
        Type serializer = _type.Assembly.GetType("LiteDB.JsonSerializer", true)!;
        Type bsonValue = _type.Assembly.GetType("LiteDB.BsonValue", true)!;
        MethodInfo serialize = serializer.GetMethod("Serialize", [bsonValue, typeof(bool)])!;
        foreach (object document in records)
        {
            string json = (string)serialize.Invoke(null, [document, false])!;
            yield return LiteDB.JsonSerializer.Deserialize(json).AsDocument;
        }
    }
}
