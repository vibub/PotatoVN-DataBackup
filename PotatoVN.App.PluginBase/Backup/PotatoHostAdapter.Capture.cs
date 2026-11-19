using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using LiteDB;
using PotatoVN.Backup.Core;
using PotatoVN.Backup.Core.Engine;

namespace PotatoVN.App.PluginBase.Backup;

public sealed partial class PotatoHostAdapter
{
    public Task<HostExport> ExportForBackupAsync(string workDirectory, CancellationToken cancellationToken) =>
        CaptureAsync(workDirectory, forImport: false, cancellationToken);

    public async Task<HostExport> ExportAsync(string workDirectory, CancellationToken cancellationToken)
    {
        HostExport export = await CaptureAsync(workDirectory, forImport: true, cancellationToken);
        foreach (var (relative, source) in export.MediaPaths)
            await CopySnapshotFileAsync(source, BackupFiles.Within(workDirectory, relative), cancellationToken);

        const string oldHomeLayout = "data.multiStreamPageList.json";
        string layout = Path.Combine(DataRoot, oldHomeLayout);
        if (File.Exists(layout))
            await CopySnapshotFileAsync(layout, Path.Combine(workDirectory, oldHomeLayout), cancellationToken);
        return export;
    }

    private async Task<HostExport> CaptureAsync(string workDirectory, bool forImport, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(workDirectory);
        var jsonAssembly = AssemblyLoadContext.GetLoadContext(_hostAssembly)!.Assemblies.Single(assembly => assembly.GetName().Name == "Newtonsoft.Json");
        var writer = new HostJsonWriter(jsonAssembly);
        object gameConverter = Activator.CreateInstance(_hostAssembly.GetType("GalgameManager.Helpers.GalgameAndUidConverter", true)!)!;
        var documents = new Dictionary<string, JsonNode?>();
        documents["data.galgames.json"] = await OnMainAsync(() => writer.Serialize(_api.GetAllGames()));
        documents["data.galgameSources.json"] = await OnMainAsync(() =>
        {
            return writer.Serialize(GetSources(SourceService()), gameConverter);
        });
        Task groups = await OnMainAsync(() =>
        {
            object service = Service("GalgameManager.Contracts.Services.ICategoryService");
            return (Task)service.GetType().GetMethod("GetCategoryGroupsAsync")!.Invoke(service, null)!;
        });
        await groups;
        documents["data.categoryGroups.json"] = await OnMainAsync(() => writer.Serialize(groups.GetType().GetProperty("Result")!.GetValue(groups), gameConverter));
        documents["data.staff.json"] = await OnMainAsync(() => writer.Serialize(_api.GetStaffs()));
        documents["data.homepage_list.json"] = await OnMainAsync<JsonNode?>(() =>
        {
            var database = new HostDatabaseReader(Property(_settings, "Database"));
            if (!database.CollectionExists("homepage_list")) return forImport ? null : new JsonArray();
            var array = new BsonArray(database.ReadDocuments("homepage_list").Select(document => (BsonValue)document));
            return JsonNode.Parse(LiteDB.JsonSerializer.Serialize(array))!;
        });
        if (forImport)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Type statusType = _hostAssembly.GetType("GalgameManager.Models.LocalSettingStatus", true)!;
            object? liveStatus = await _portableSettings.ReadValueAsync("dataStatus", statusType, isLarge: true);
            object importStatus = liveStatus != null ? statusType.GetMethod("Clone")!.Invoke(liveStatus, null)! :
                Activator.CreateInstance(statusType)!;
            statusType.GetMethod("SetToExport")!.Invoke(importStatus, null);
            documents["data.dataStatus.json"] = writer.Serialize(importStatus);

            if (_hostAssembly.GetType("GalgameManager.Models.PageSettings") != null)
                documents["data.pageSettings.json"] = await _portableSettings.ReadImportPageSettingsAsync(cancellationToken);
            else
                documents["data.customSortOrder.json"] = writer.Serialize(
                    await _portableSettings.ReadValueAsync("customSortOrder", typeof(List<string>), isLarge: true));
        }

        var export = new HostExport { NativeDirectory = workDirectory };
        foreach (var pair in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pair.Value == null) continue;
            RewriteMediaPaths(pair.Value, export.MediaPaths);
            await BackupFiles.WriteJsonAsync(Path.Combine(workDirectory, pair.Key), pair.Value, cancellationToken);
        }
        foreach (string key in SettingKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            export.Settings[key] = await _portableSettings.ReadAsync(key);
        }
        return export;
    }
    private static async Task CopySnapshotFileAsync(string source, string target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        await using var output = File.Create(target);
        await input.CopyToAsync(output, ct);
    }

    private void RewriteMediaPaths(JsonNode node, Dictionary<string, string> media, bool image = false)
    {
        if (node is JsonObject obj) foreach (var pair in obj.ToArray())
        {
            bool imageField = image || pair.Key.Contains("ImagePath", StringComparison.OrdinalIgnoreCase);
            if (imageField && pair.Value is JsonValue value && value.TryGetValue<string>(out string? text) && !string.IsNullOrWhiteSpace(text))
            {
                if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && !uri.IsFile) continue;
                string path = Path.GetFullPath(Path.IsPathRooted(text) ? text : Path.Combine(DataRoot, text));
                if (!File.Exists(path)) continue;
                string key = "Images/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))).ToLowerInvariant() + Path.GetExtension(path).ToLowerInvariant();
                media[key] = path;
                obj[pair.Key] = "./" + key;
            }
            else if (pair.Value != null) RewriteMediaPaths(pair.Value, media, imageField);
        }
        else if (node is JsonArray array) foreach (JsonNode child in array.OfType<JsonNode>()) RewriteMediaPaths(child, media, image);
    }
}
