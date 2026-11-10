using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace PotatoVN.Backup.Core.Providers;

public sealed class GoogleDriveProvider : OAuthProviderBase
{
    private const string Api = "https://www.googleapis.com/drive/v3/files";
    private const string UploadApi = "https://www.googleapis.com/upload/drive/v3/files";
    private const string FolderType = "application/vnd.google-apps.folder";
    private string? _rootId;
    private readonly Dictionary<string, string> _directories = new(StringComparer.Ordinal);
    protected override string Label => "Google Drive";
    protected override string AuthorizeEndpoint => "https://accounts.google.com/o/oauth2/v2/auth";
    protected override string TokenEndpoint => "https://oauth2.googleapis.com/token";
    protected override string Scope => "https://www.googleapis.com/auth/drive.file";
    protected override string ApiHost => "www.googleapis.com";
    protected override bool IsGoogle => true;

    public GoogleDriveProvider(ProviderProfile profile, ISecretStore secrets, HttpClient? http = null)
        : base(profile, secrets, http) { }

    public override async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        await RootAsync(cancellationToken).ConfigureAwait(false);
        await ListAsync("", cancellationToken).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<RemoteItem>> ListAsync(string directory, CancellationToken cancellationToken = default)
    {
        directory = RemotePaths.Normalize(directory);
        var folder = await FindAsync(directory, cancellationToken).ConfigureAwait(false);
        if (folder is null) return [];
        if (!IsFolder(folder)) throw new IOException("要列举的 Google Drive 路径不是文件夹。");
        var items = await QueryAsync("'" + EscapeQuery(ProviderHttp.RequiredString(folder, "id")) +
            "' in parents and trashed = false", cancellationToken).ConfigureAwait(false);
        return items.Select(item => new RemoteItem(RemotePaths.Join(directory, ProviderHttp.RequiredString(item, "name")),
            IsFolder(item), Size(item), ProviderHttp.Date(item["modifiedTime"]?.GetValue<string>()))).ToList();
    }

    public override async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
        await FindAsync(RemotePaths.Normalize(path), cancellationToken).ConfigureAwait(false) is not null;

    public override async Task UploadAsync(string path, string localFile, CancellationToken cancellationToken = default)
    {
        path = RemotePaths.Normalize(path);
        if (path.Length == 0) throw new ArgumentException("上传路径必须包含文件名。");
        var parent = await EnsureDirectoryAsync(RemotePaths.Parent(path), cancellationToken).ConfigureAwait(false);
        var name = RemotePaths.Name(path);
        var existing = await ChildAsync(parent, name, cancellationToken).ConfigureAwait(false);
        if (existing is not null && IsFolder(existing)) throw new IOException("Google Drive 目标被同名文件夹占用。");
        var metadata = new JsonObject { ["name"] = name };
        if (existing is null) metadata["parents"] = new JsonArray(parent);
        var length = new FileInfo(localFile).Length;
        var url = UploadApi + (existing is null ? "" : "/" + Uri.EscapeDataString(ProviderHttp.RequiredString(existing, "id"))) +
            "?uploadType=resumable&fields=id,size";
        using var initial = await SendApiAsync(() =>
        {
            var request = new HttpRequestMessage(existing is null ? HttpMethod.Post : HttpMethod.Patch, url)
            { Content = ProviderHttp.Json(metadata) };
            request.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
            request.Headers.Add("X-Upload-Content-Length", length.ToString(CultureInfo.InvariantCulture));
            return request;
        }, cancellationToken).ConfigureAwait(false);
        ProviderHttp.Check(initial, Label);
        var session = ProviderHttp.HttpsUri(initial.Headers.Location?.ToString()
            ?? throw new InvalidDataException("Google Drive 没有返回上传会话地址。"));
        await using var input = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[8 * 1024 * 1024]; // 32 × 256 KiB。
        long offset = 0;
        do
        {
            input.Position = offset;
            var count = await ProviderHttp.ReadChunkAsync(input, buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0 && length != 0) throw new IOException("上传时本地文件长度发生变化。");
            using var response = await SendApiAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Put, session)
                { Content = new ByteArrayContent(buffer, 0, count) };
                request.Content.Headers.ContentLength = count;
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                request.Content.Headers.ContentRange = length == 0 ? new ContentRangeHeaderValue(0) :
                    new ContentRangeHeaderValue(offset, offset + count - 1, length);
                return request;
            }, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                var completed = await ProviderHttp.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
                if (completed["id"] is null || Size(completed) != length)
                    throw new IOException("Google Drive 返回的已上传文件长度不匹配。");
                return;
            }
            if ((int)response.StatusCode != 308) ProviderHttp.Check(response, Label);
            if (!response.Headers.TryGetValues("Range", out var ranges) ||
                !long.TryParse(ranges.FirstOrDefault()?.Split('-').LastOrDefault(), out var last) ||
                last + 1 <= offset || last + 1 >= length)
                throw new IOException("Google Drive 上传进度无效或上传尚未提交。");
            offset = last + 1;
        } while (offset < length);
        throw new IOException("Google Drive 未确认上传完成。");
    }

    public override async Task DownloadAsync(string path, string localFile, CancellationToken cancellationToken = default)
    {
        var item = await FindAsync(RemotePaths.Normalize(path), cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("Google Drive 中找不到所选备份文件。");
        if (IsFolder(item)) throw new IOException("不能把 Google Drive 文件夹作为备份文件下载。");
        var url = Api + "/" + Uri.EscapeDataString(ProviderHttp.RequiredString(item, "id")) + "?alt=media";
        using var response = await SendApiAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken)
            .ConfigureAwait(false);
        ProviderHttp.Check(response, Label);
        await ProviderHttp.DownloadAsync(response, localFile, cancellationToken).ConfigureAwait(false);
    }

    public override async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        path = RemotePaths.Normalize(path);
        if (path.Length == 0) throw new ArgumentException("不能删除整个备份根目录。");
        var item = await FindAsync(path, cancellationToken).ConfigureAwait(false);
        if (item is null) return;
        var url = Api + "/" + Uri.EscapeDataString(ProviderHttp.RequiredString(item, "id"));
        using var response = await SendApiAsync(() => new HttpRequestMessage(HttpMethod.Delete, url), cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound) ProviderHttp.Check(response, Label);
        foreach (var key in _directories.Keys.Where(k => k == path || k.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
            _directories.Remove(key);
    }

    private async Task<string> RootAsync(CancellationToken token)
    {
        if (_rootId is not null) return _rootId;
        var parent = "root";
        foreach (var name in RemotePaths.Normalize(Profile.RemotePath + "/PotatoVN-DataBackup").Split('/'))
            parent = await EnsureChildDirectoryAsync(parent, name, token).ConfigureAwait(false);
        _directories[""] = parent;
        return _rootId = parent;
    }

    private async Task<JsonObject?> FindAsync(string path, CancellationToken token)
    {
        var parent = await RootAsync(token).ConfigureAwait(false);
        if (path.Length == 0) return new JsonObject { ["id"] = parent, ["mimeType"] = FolderType };
        var parts = path.Split('/');
        var relative = "";
        JsonObject? item = null;
        for (var index = 0; index < parts.Length; index++)
        {
            relative = RemotePaths.Join(relative, parts[index]);
            if (_directories.TryGetValue(relative, out var cached))
                item = new JsonObject { ["id"] = cached, ["name"] = parts[index], ["mimeType"] = FolderType };
            else
                item = await ChildAsync(parent, parts[index], token).ConfigureAwait(false);
            if (item is null) return null;
            if (index < parts.Length - 1 && !IsFolder(item)) return null;
            parent = ProviderHttp.RequiredString(item, "id");
            if (IsFolder(item)) _directories[relative] = parent;
        }
        return item;
    }

    private async Task<string> EnsureDirectoryAsync(string path, CancellationToken token)
    {
        var parent = await RootAsync(token).ConfigureAwait(false);
        var relative = "";
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            relative = RemotePaths.Join(relative, part);
            if (!_directories.TryGetValue(relative, out var id))
            {
                id = await EnsureChildDirectoryAsync(parent, part, token).ConfigureAwait(false);
                _directories[relative] = id;
            }
            parent = id;
        }
        return parent;
    }

    private async Task<string> EnsureChildDirectoryAsync(string parent, string name, CancellationToken token)
    {
        var folder = await ChildAsync(parent, name, token).ConfigureAwait(false);
        if (folder is null)
        {
            var metadata = new JsonObject { ["name"] = name, ["mimeType"] = FolderType, ["parents"] = new JsonArray(parent) };
            using var response = await SendApiAsync(() => new HttpRequestMessage(HttpMethod.Post, Api + "?fields=id,name,mimeType")
            { Content = ProviderHttp.Json(metadata) }, token).ConfigureAwait(false);
            ProviderHttp.Check(response, Label);
            folder = await ProviderHttp.ReadJsonAsync(response, token).ConfigureAwait(false);
        }
        if (!IsFolder(folder)) throw new IOException("Google Drive 备份路径被同名文件占用。");
        return ProviderHttp.RequiredString(folder, "id");
    }

    private async Task<JsonObject?> ChildAsync(string parent, string name, CancellationToken token)
    {
        var query = "'" + EscapeQuery(parent) + "' in parents and name = '" + EscapeQuery(name) + "' and trashed = false";
        var matches = await QueryAsync(query, token).ConfigureAwait(false);
        if (matches.Count > 1)
            throw new IOException("Google Drive 备份目录存在同名项目，请先在云盘中整理重复项目后重试。");
        return matches.SingleOrDefault();
    }

    private async Task<List<JsonObject>> QueryAsync(string query, CancellationToken token)
    {
        var result = new List<JsonObject>();
        string? pageToken = null;
        do
        {
            var url = Api + "?q=" + Uri.EscapeDataString(query) +
                "&fields=files(id,name,mimeType,size,modifiedTime),nextPageToken&pageSize=1000" +
                (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));
            using var response = await SendApiAsync(() => new HttpRequestMessage(HttpMethod.Get, url), token).ConfigureAwait(false);
            ProviderHttp.Check(response, Label);
            var page = await ProviderHttp.ReadJsonAsync(response, token).ConfigureAwait(false);
            if (page["files"] is not JsonArray items) throw new InvalidDataException("Google Drive 列表响应缺少 files。");
            result.AddRange(items.OfType<JsonObject>());
            pageToken = page["nextPageToken"]?.GetValue<string>();
        } while (!string.IsNullOrEmpty(pageToken));
        return result;
    }

    private static bool IsFolder(JsonObject item) => item["mimeType"]?.GetValue<string>() == FolderType;
    private static long Size(JsonObject item) => long.TryParse(item["size"]?.ToString(), NumberStyles.Integer,
        CultureInfo.InvariantCulture, out var size) ? size : 0;
    private static string EscapeQuery(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
}
