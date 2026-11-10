using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace PotatoVN.Backup.Core.Providers;

public sealed class WebDavProvider : IBackupProvider
{
    private static readonly XNamespace Dav = "DAV:";
    private readonly ProviderProfile _profile;
    private readonly ISecretStore _secrets;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Uri _endpoint;
    private readonly string _root;

    public WebDavProvider(ProviderProfile profile, ISecretStore secrets, HttpClient? http = null)
    {
        if (!Uri.TryCreate(profile.Endpoint.TrimEnd('/') + "/", UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException("请输入不含账号、查询参数或片段的 HTTP(S) WebDAV 服务器地址。");
        _profile = profile;
        _secrets = secrets;
        _endpoint = endpoint;
        _root = RemotePaths.Normalize(profile.RemotePath + "/PotatoVN-DataBackup");
        _http = http ?? ProviderHttp.CreateClient();
        _ownsHttp = http is null;
    }

    public Task AuthorizeAsync(CancellationToken cancellationToken = default) => ProbeAsync(cancellationToken);

    public async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDirectoryAsync("", cancellationToken).ConfigureAwait(false);
        await ListAsync("", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemoteItem>> ListAsync(string directory, CancellationToken cancellationToken = default)
    {
        directory = RemotePaths.Normalize(directory);
        using var response = await PropFindAsync(directory, "1", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return [];
        ProviderHttp.Check(response, "WebDAV");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
        var result = new List<RemoteItem>();
        var collectionPath = Uri.UnescapeDataString(UriFor(directory).AbsolutePath).TrimEnd('/');
        var rootPath = Uri.UnescapeDataString(UriFor("").AbsolutePath).TrimEnd('/') + "/";
        foreach (var item in document.Descendants(Dav + "response"))
        {
            var href = item.Element(Dav + "href")?.Value;
            if (href is null || !Uri.TryCreate(_endpoint, href, out var uri) || uri.Authority != _endpoint.Authority ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidDataException("WebDAV 返回的文件地址不属于当前服务器。");
            var itemPath = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');
            if (itemPath == collectionPath) continue;
            if (!itemPath.StartsWith(rootPath, StringComparison.Ordinal))
                throw new InvalidDataException("WebDAV 返回的文件超出备份目录。");
            var relative = RemotePaths.Normalize(itemPath[rootPath.Length..]);
            if (RemotePaths.Parent(relative) != directory) continue;
            var propstat = item.Elements(Dav + "propstat").FirstOrDefault(p =>
                p.Element(Dav + "status")?.Value.Contains(" 200 ", StringComparison.Ordinal) == true);
            if (propstat?.Element(Dav + "prop") is not { } properties) continue;
            var isDirectory = properties.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null;
            _ = long.TryParse(properties.Element(Dav + "getcontentlength")?.Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var length);
            result.Add(new RemoteItem(relative, isDirectory, length,
                ProviderHttp.Date(properties.Element(Dav + "getlastmodified")?.Value)));
        }
        return result;
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        using var response = await PropFindAsync(RemotePaths.Normalize(path), "0", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        ProviderHttp.Check(response, "WebDAV");
        return true;
    }

    public async Task UploadAsync(string path, string localFile, CancellationToken cancellationToken = default)
    {
        path = RemotePaths.Normalize(path);
        await EnsureDirectoryAsync(RemotePaths.Parent(path), cancellationToken).ConfigureAwait(false);
        using var request = await RequestAsync(HttpMethod.Put, UriFor(path), cancellationToken).ConfigureAwait(false);
        var stream = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        request.Content = new StreamContent(stream);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentLength = stream.Length;
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        ProviderHttp.Check(response, "WebDAV");
    }

    public async Task DownloadAsync(string path, string localFile, CancellationToken cancellationToken = default)
    {
        using var request = await RequestAsync(HttpMethod.Get, UriFor(path), cancellationToken).ConfigureAwait(false);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        ProviderHttp.Check(response, "WebDAV");
        await ProviderHttp.DownloadAsync(response, localFile, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        path = RemotePaths.Normalize(path);
        if (path.Length == 0) throw new ArgumentException("不能删除整个备份根目录。");
        using var request = await RequestAsync(HttpMethod.Delete, UriFor(path), cancellationToken).ConfigureAwait(false);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        ProviderHttp.Check(response, "WebDAV");
        if ((int)response.StatusCode == 207)
        {
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (xml.Descendants(Dav + "status").Any(s => !s.Value.Contains(" 200 ") && !s.Value.Contains(" 204 ")))
                throw new IOException("WebDAV 未能完整删除所选云端版本。");
        }
    }

    private async Task EnsureDirectoryAsync(string relative, CancellationToken token)
    {
        var path = "";
        foreach (var part in RemotePaths.Normalize(_root + "/" + relative).Split('/'))
        {
            path = RemotePaths.Join(path, part);
            using var request = await RequestAsync(new HttpMethod("MKCOL"),
                new Uri(_endpoint, RemotePaths.Encode(path) + "/"), token).ConfigureAwait(false);
            using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
            // RFC 4918: 对已存在的集合执行 MKCOL 返回 405。
            if (response.StatusCode != HttpStatusCode.MethodNotAllowed) ProviderHttp.Check(response, "WebDAV");
        }
    }

    private async Task<HttpResponseMessage> PropFindAsync(string path, string depth, CancellationToken token)
    {
        var uri = depth == "1" ? new Uri(UriFor(path).AbsoluteUri.TrimEnd('/' ) + "/") : UriFor(path);
        using var request = await RequestAsync(new HttpMethod("PROPFIND"), uri, token).ConfigureAwait(false);
        request.Headers.Add("Depth", depth);
        request.Content = new StringContent("<d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/><d:getcontentlength/><d:getlastmodified/></d:prop></d:propfind>",
            Encoding.UTF8, "application/xml");
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
    }

    private Uri UriFor(string path) => new(_endpoint, RemotePaths.Encode(_root + "/" + path));
    private async Task<HttpRequestMessage> RequestAsync(HttpMethod method, Uri uri, CancellationToken token)
    {
        var request = new HttpRequestMessage(method, uri);
        if (!string.IsNullOrEmpty(_profile.Username))
        {
            var password = await _secrets.ReadAsync(_profile.Id + ":password", token).ConfigureAwait(false) ?? "";
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(_profile.Username + ":" + password)));
        }
        return request;
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
