using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace PotatoVN.Backup.Core.Providers;

public sealed class OneDriveProvider : OAuthProviderBase
{
    private const string Graph = "https://graph.microsoft.com/v1.0/";
    private string? _rootId;
    protected override string Label => "OneDrive";
    protected override string AuthorizeEndpoint => "https://login.microsoftonline.com/" + Tenant + "/oauth2/v2.0/authorize";
    protected override string TokenEndpoint => "https://login.microsoftonline.com/" + Tenant + "/oauth2/v2.0/token";
    protected override string Scope => "https://graph.microsoft.com/Files.ReadWrite.AppFolder offline_access";
    protected override string ApiHost => "graph.microsoft.com";
    private string Tenant => string.IsNullOrWhiteSpace(Profile.Tenant) ? "common" : Profile.Tenant;

    public OneDriveProvider(ProviderProfile profile, ISecretStore secrets, HttpClient? http = null) : base(profile, secrets, http)
    {
        if (Tenant.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.')))
            throw new ArgumentException("微软租户只能填写 common、consumers、organizations 或租户 ID。");
    }

    public override async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        await RootAsync(cancellationToken).ConfigureAwait(false);
        await ListAsync("", cancellationToken).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<RemoteItem>> ListAsync(string directory, CancellationToken cancellationToken = default)
    {
        directory = RemotePaths.Normalize(directory);
        var item = await FindAsync(directory, cancellationToken).ConfigureAwait(false);
        if (item is null) return [];
        if (item["folder"] is null) throw new IOException("要列举的 OneDrive 路径不是文件夹。");
        var result = new List<RemoteItem>();
        string? url = Graph + "me/drive/items/" + Uri.EscapeDataString(ProviderHttp.RequiredString(item, "id")) +
            "/children?$select=id,name,size,folder,lastModifiedDateTime&$top=200";
        while (url is not null)
        {
            using var response = await SendApiAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken)
                .ConfigureAwait(false);
            ProviderHttp.Check(response, Label);
            var page = await ProviderHttp.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            if (page["value"] is not JsonArray items) throw new InvalidDataException("OneDrive 列表响应缺少 value。");
            foreach (var entry in items.OfType<JsonObject>())
                result.Add(new RemoteItem(RemotePaths.Join(directory, ProviderHttp.RequiredString(entry, "name")),
                    entry["folder"] is not null, entry["size"]?.GetValue<long>() ?? 0,
                    ProviderHttp.Date(entry["lastModifiedDateTime"]?.GetValue<string>())));
            url = page["@odata.nextLink"]?.GetValue<string>();
        }
        return result;
    }

    public override async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
        await FindAsync(RemotePaths.Normalize(path), cancellationToken).ConfigureAwait(false) is not null;

    public override async Task UploadAsync(string path, string localFile, CancellationToken cancellationToken = default)
    {
        path = RemotePaths.Normalize(path);
        if (path.Length == 0) throw new ArgumentException("上传路径必须包含文件名。");
        var parent = await EnsureDirectoryAsync(RemotePaths.Parent(path), cancellationToken).ConfigureAwait(false);
        var address = Graph + "me/drive/items/" + Uri.EscapeDataString(parent) + ":/" +
            Uri.EscapeDataString(RemotePaths.Name(path));
        var length = new FileInfo(localFile).Length;
        if (length <= 4 * 1024 * 1024)
        {
            using var response = await SendApiAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Put, address + ":/content");
                request.Content = new StreamContent(new FileStream(localFile, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan));
                request.Content.Headers.ContentLength = length;
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                return request;
            }, cancellationToken).ConfigureAwait(false);
            ProviderHttp.Check(response, Label);
            await VerifyCompletedAsync(response, length, cancellationToken).ConfigureAwait(false);
            return;
        }

        var metadata = new JsonObject
        {
            ["item"] = new JsonObject { ["@microsoft.graph.conflictBehavior"] = "replace", ["name"] = RemotePaths.Name(path) }
        };
        using var create = await SendApiAsync(() => new HttpRequestMessage(HttpMethod.Post, address + ":/createUploadSession")
        { Content = ProviderHttp.Json(metadata) }, cancellationToken).ConfigureAwait(false);
        ProviderHttp.Check(create, Label);
        var session = await ProviderHttp.ReadJsonAsync(create, cancellationToken).ConfigureAwait(false);
        var uploadUri = ProviderHttp.HttpsUri(ProviderHttp.RequiredString(session, "uploadUrl"));
        await using var input = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[10 * 1024 * 1024]; // 32 × 320 KiB，符合 Graph 分片限制。
        long offset = 0;
        while (offset < length)
        {
            input.Position = offset;
            var count = await ProviderHttp.ReadChunkAsync(input, buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new IOException("上传时本地文件长度发生变化。");
            using var request = new HttpRequestMessage(HttpMethod.Put, uploadUri)
            {
                Content = new ByteArrayContent(buffer, 0, count)
            };
            request.Content.Headers.ContentLength = count;
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + count - 1, length);
            // uploadUrl 已含临时授权，不能再添加 Graph Authorization 头。
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            ProviderHttp.Check(response, Label);
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                await VerifyCompletedAsync(response, length, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (response.StatusCode != HttpStatusCode.Accepted)
                throw new IOException("OneDrive 没有确认上传分片。");
            var progress = await ProviderHttp.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var expected = progress["nextExpectedRanges"]?[0]?.GetValue<string>()?.Split('-')[0];
            if (!long.TryParse(expected, out var next) || next <= offset || next >= length)
                throw new IOException("OneDrive 上传进度无效或上传尚未提交。");
            offset = next;
        }
        throw new IOException("OneDrive 未确认上传完成。");
    }

    public override async Task DownloadAsync(string path, string localFile, CancellationToken cancellationToken = default)
    {
        var item = await FindAsync(RemotePaths.Normalize(path), cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("OneDrive 中找不到所选备份文件。");
        if (item["folder"] is not null) throw new IOException("不能把 OneDrive 文件夹作为备份文件下载。");
        var download = item["@microsoft.graph.downloadUrl"]?.GetValue<string>();
        if (download is null)
        {
            var url = Graph + "me/drive/items/" + Uri.EscapeDataString(ProviderHttp.RequiredString(item, "id")) + "/content";
            using var redirect = await SendApiAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken)
                .ConfigureAwait(false);
            if (redirect.IsSuccessStatusCode)
            {
                await ProviderHttp.DownloadAsync(redirect, localFile, cancellationToken).ConfigureAwait(false);
                return;
            }
            if ((int)redirect.StatusCode is not (301 or 302 or 303 or 307 or 308) || redirect.Headers.Location is null)
                ProviderHttp.Check(redirect, Label);
            download = redirect.Headers.Location?.ToString()
                ?? throw new InvalidDataException("OneDrive 没有返回下载地址。");
        }
        using var response = await Http.GetAsync(ProviderHttp.HttpsUri(download), HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        ProviderHttp.Check(response, Label);
        await ProviderHttp.DownloadAsync(response, localFile, cancellationToken).ConfigureAwait(false);
    }

    public override async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        path = RemotePaths.Normalize(path);
        if (path.Length == 0) throw new ArgumentException("不能删除整个备份根目录。");
        var item = await FindAsync(path, cancellationToken).ConfigureAwait(false);
        if (item is null) return;
        var url = Graph + "me/drive/items/" + Uri.EscapeDataString(ProviderHttp.RequiredString(item, "id"));
        using var response = await SendApiAsync(() => new HttpRequestMessage(HttpMethod.Delete, url), cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound) ProviderHttp.Check(response, Label);
    }

    private async Task<string> RootAsync(CancellationToken token)
    {
        if (_rootId is not null) return _rootId;
        using var response = await SendApiAsync(() => new HttpRequestMessage(HttpMethod.Get,
            Graph + "me/drive/special/approot"), token).ConfigureAwait(false);
        ProviderHttp.Check(response, Label);
        var item = await ProviderHttp.ReadJsonAsync(response, token).ConfigureAwait(false);
        var parent = ProviderHttp.RequiredString(item, "id");
        foreach (var part in RemotePaths.Normalize(Profile.RemotePath + "/" + Profile.RepositoryFolder).Split('/'))
            parent = await EnsureChildDirectoryAsync(parent, part, token).ConfigureAwait(false);
        return _rootId = parent;
    }

    private async Task<JsonObject?> FindAsync(string path, CancellationToken token)
    {
        var id = await RootAsync(token).ConfigureAwait(false);
        return await GetItemAsync(id, path, token).ConfigureAwait(false);
    }

    private async Task<JsonObject?> GetItemAsync(string parent, string path, CancellationToken token)
    {
        var url = Graph + "me/drive/items/" + Uri.EscapeDataString(parent);
        if (!string.IsNullOrEmpty(path)) url += ":/" + RemotePaths.Encode(path);
        using var response = await SendApiAsync(() => new HttpRequestMessage(HttpMethod.Get, url), token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        ProviderHttp.Check(response, Label);
        return await ProviderHttp.ReadJsonAsync(response, token).ConfigureAwait(false);
    }

    private async Task<string> EnsureDirectoryAsync(string path, CancellationToken token)
    {
        var parent = await RootAsync(token).ConfigureAwait(false);
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            parent = await EnsureChildDirectoryAsync(parent, part, token).ConfigureAwait(false);
        return parent;
    }

    private async Task<string> EnsureChildDirectoryAsync(string parent, string name, CancellationToken token)
    {
        var existing = await GetItemAsync(parent, name, token).ConfigureAwait(false);
        if (existing is null)
        {
            var metadata = new JsonObject { ["name"] = name, ["folder"] = new JsonObject(), ["@microsoft.graph.conflictBehavior"] = "fail" };
            using var response = await SendApiAsync(() => new HttpRequestMessage(HttpMethod.Post,
                Graph + "me/drive/items/" + Uri.EscapeDataString(parent) + "/children")
            { Content = ProviderHttp.Json(metadata) }, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Conflict)
                existing = await GetItemAsync(parent, name, token).ConfigureAwait(false)
                    ?? throw new IOException("OneDrive 文件夹创建冲突，请重试。");
            else
            {
                ProviderHttp.Check(response, Label);
                existing = await ProviderHttp.ReadJsonAsync(response, token).ConfigureAwait(false);
            }
        }
        if (existing["folder"] is null) throw new IOException("OneDrive 备份路径被同名文件占用。");
        return ProviderHttp.RequiredString(existing, "id");
    }

    private static async Task VerifyCompletedAsync(HttpResponseMessage response, long length, CancellationToken token)
    {
        var item = await ProviderHttp.ReadJsonAsync(response, token).ConfigureAwait(false);
        if (item["size"]?.GetValue<long>() != length || item["id"] is null)
            throw new IOException("OneDrive 返回的已上传文件长度不匹配。");
    }
}
