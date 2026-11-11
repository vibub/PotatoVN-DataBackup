using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using PotatoVN.Backup.Core;
using PotatoVN.Backup.Core.Providers;

var artifactRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "provider-test-artifacts"));
Directory.CreateDirectory(artifactRoot);
var taskRoot = Path.Combine(artifactRoot, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(taskRoot);
var tests = new (string Name, Func<Task> Run)[]
{
    ("新渠道固定目录及旧配置目录兼容", RepositoryDirectoryCompatibility),
    ("WebDAV 完整往返、Unicode 路径、越界拒绝", WebDavRoundTrip),
    ("WebDAV 复用已确认目录并在删除后失效", () => WebDavReliabilityChecks.DirectoryReuse(taskRoot)),
    ("WebDAV 各方法429/503重试及完整上传流", () => WebDavReliabilityChecks.TransientRetry(taskRoot)),
    ("WebDAV 重试上限、授权错误及等待取消", WebDavReliabilityChecks.RetryLimits),
    ("坚果云同账号跨实例请求间隔与取消", WebDavReliabilityChecks.NutstorePacing),
    ("OneDrive 自动刷新令牌与 401 后刷新", OAuthRefresh),
    ("OneDrive 分片及预授权传输不携带 Bearer", OneDriveChunks),
    ("Google Drive 分片、分页与完整往返", GoogleChunks),
    ("Google Drive 同名目录冲突不擅自选择", GoogleDuplicates),
    ("DPAPI 本机加密、重启读取与清理", DpapiRoundTrip),
    ("Coordinator 清单最后发布及未变化存档去重", () => CoordinatorBoundaryChecks.ManifestLastAndDedup(taskRoot)),
    ("Coordinator 上传中断不提交清单", () => CoordinatorBoundaryChecks.InterruptedUploadNeverCommits(taskRoot)),
    ("Coordinator 新设备保留旧设备云端存档引用", () => CoordinatorBoundaryChecks.NewDeviceRetainsOldSaveReference(taskRoot)),
    ("Coordinator 相同内容改变存档位置规则", () => CoordinatorBoundaryChecks.LocationChangeCreatesNewRecipe(taskRoot)),
    ("Coordinator 不复用缺失存档包", () => CoordinatorBoundaryChecks.MissingArchiveIsNotReused(taskRoot)),
    ("Coordinator 其他设备引用保护旧存档", () => CoordinatorBoundaryChecks.OtherDeviceReferencesProtectHistory(taskRoot)),
    ("Coordinator 非法远端路径及 null 清单拒绝", () => CoordinatorBoundaryChecks.MalformedRemotePathsRejected(taskRoot))
};
var failures = 0;
try
{
    foreach (var test in tests)
    {
        try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
        catch (Exception error) { failures++; Console.WriteLine("FAIL " + test.Name + ": " + error); }
    }
}
finally
{
    if (!Path.GetFullPath(taskRoot).StartsWith(artifactRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("测试清理目录越界。");
    Directory.Delete(taskRoot, true);
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} provider protocol checks passed.");
return failures == 0 ? 0 : 1;

async Task RepositoryDirectoryCompatibility()
{
    var fresh = new ProviderProfile { Kind = ProviderKind.WebDav, Endpoint = "https://dav.test/webdav/" };
    Equal("PotatoVN_Data", fresh.RepositoryFolder, "new channels use fixed data directory");
    var legacy = JsonSerializer.Deserialize<ProviderProfile>("{\"Kind\":0,\"Endpoint\":\"https://dav.test/webdav/\",\"RemotePath\":\"旧目录\"}", BackupJson.Options)!;
    Equal("PotatoVN-DataBackup", legacy.RepositoryFolder, "legacy missing field preserves old repository folder");
    Equal("旧目录", legacy.RemotePath, "legacy custom parent retained");
    var roundTrip = JsonSerializer.Deserialize<ProviderProfile>(JsonSerializer.Serialize(fresh, BackupJson.Options), BackupJson.Options)!;
    Equal("PotatoVN_Data", roundTrip.RepositoryFolder, "new directory persists across restart");
    foreach (var profile in new[] { fresh, legacy })
    {
        var requested = new List<string>();
        using var http = new HttpClient(new FakeHandler(request =>
        {
            string path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            requested.Add(path);
            return Task.FromResult(request.Method.Method == "MKCOL" ? new HttpResponseMessage(HttpStatusCode.Created) :
                new HttpResponseMessage((HttpStatusCode)207) { Content = new StringContent("<d:multistatus xmlns:d=\"DAV:\"/>") });
        }));
        using var provider = new WebDavProvider(profile, new MemorySecrets(), http);
        await provider.ProbeAsync();
        string repository = "/webdav/" + (profile.RemotePath.Length == 0 ? "" : profile.RemotePath + "/") + profile.RepositoryFolder + "/";
        True(requested.Contains(repository), "WebDAV creates and probes selected repository directory");
        True(requested.All(path => !path.Contains(profile == fresh ? "PotatoVN-DataBackup" : "PotatoVN_Data")), "no implicit directory relocation");
    }
}
async Task WebDavRoundTrip()
{
    var profile = new ProviderProfile { Kind = ProviderKind.WebDav, Endpoint = "https://dav.test/webdav/", RemotePath = "用户/备份", Username = "test-user" };
    var secrets = new MemorySecrets();
    await secrets.WriteAsync(profile.Id + ":password", "test-password");
    var files = new Dictionary<string, byte[]>();
    var directories = new HashSet<string> { "/webdav" };
    var malicious = false;
    XNamespace dav = "DAV:";
    using var http = new HttpClient(new FakeHandler(async request =>
    {
        Equal("Basic", request.Headers.Authorization?.Scheme, "WebDAV authentication scheme");
        var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).TrimEnd('/');
        if (request.Method.Method == "MKCOL")
            return new(directories.Add(path) ? HttpStatusCode.Created : HttpStatusCode.MethodNotAllowed);
        if (request.Method == HttpMethod.Put)
        {
            files[path] = await request.Content!.ReadAsByteArrayAsync();
            return new(HttpStatusCode.Created);
        }
        if (request.Method == HttpMethod.Get)
            return files.TryGetValue(path, out var bytes) ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) } : new(HttpStatusCode.NotFound);
        if (request.Method == HttpMethod.Delete)
        {
            files.Remove(path);
            return new(HttpStatusCode.NoContent);
        }
        if (request.Method.Method != "PROPFIND") throw new Exception("Unexpected WebDAV request");
        if (!directories.Contains(path) && !files.ContainsKey(path)) return new(HttpStatusCode.NotFound);
        var items = new List<string> { path };
        if (request.Headers.GetValues("Depth").Single() == "1")
            items.AddRange(files.Keys.Concat(directories).Where(p => p != path && p[..p.LastIndexOf('/')] == path));
        if (malicious) items.Add("/outside/secret");
        var xml = new XDocument(new XElement(dav + "multistatus", items.Select(p => new XElement(dav + "response",
            new XElement(dav + "href", string.Join('/', p.Split('/').Select(Uri.EscapeDataString))), new XElement(dav + "propstat",
                new XElement(dav + "prop", new XElement(dav + "resourcetype", directories.Contains(p) ? new XElement(dav + "collection") : null),
                    new XElement(dav + "getcontentlength", files.GetValueOrDefault(p)?.Length ?? 0),
                    new XElement(dav + "getlastmodified", "Mon, 09 Nov 2026 12:00:00 GMT")),
                new XElement(dav + "status", "HTTP/1.1 200 OK"))))));
        return new((HttpStatusCode)207) { Content = new StringContent(xml.ToString()) };
    }));
    using var provider = new WebDavProvider(profile, secrets, http);
    await provider.ProbeAsync();
    var source = Path.Combine(taskRoot, "dav-source");
    var target = Path.Combine(taskRoot, "dav-target");
    await File.WriteAllTextAsync(source, "unicode-存档");
    const string remote = "v1/存档 #1.bin";
    await provider.UploadAsync(remote, source);
    True(await provider.ExistsAsync(remote), "WebDAV uploaded object exists");
    var listed = await provider.ListAsync("v1");
    Equal(remote, listed.Single().Path, "WebDAV path returned relative to repository");
    True(listed.Single().ModifiedUtc.HasValue, "RFC1123 date parsed");
    await provider.DownloadAsync(remote, target);
    Equal(await File.ReadAllTextAsync(source), await File.ReadAllTextAsync(target), "WebDAV download bytes");
    await provider.DeleteAsync(remote);
    True(!await provider.ExistsAsync(remote), "WebDAV delete");
    await Throws<ArgumentException>(() => provider.UploadAsync("../outside", source));
    malicious = true;
    await Throws<InvalidDataException>(() => provider.ListAsync("v1"));
}

async Task OAuthRefresh()
{
    foreach (var initiallyExpired in new[] { true, false })
    {
        var profile = new ProviderProfile { Kind = ProviderKind.OneDrive, ClientId = "test-client" };
        var secrets = await Tokens(profile, initiallyExpired);
        var refreshes = 0;
        var rejected = false;
        using var http = new HttpClient(new FakeHandler(async request =>
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
            {
                refreshes++;
                var body = await request.Content!.ReadAsStringAsync();
                True(body.Contains("grant_type=refresh_token"), "OAuth refresh grant");
                True(!body.Contains("client_secret"), "Microsoft desktop application has no secret");
                return Json("{\"access_token\":\"refreshed\",\"expires_in\":3600}");
            }
            if (!initiallyExpired && !rejected)
            {
                rejected = true;
                return new(HttpStatusCode.Unauthorized);
            }
            Equal("refreshed", request.Headers.Authorization?.Parameter, "refreshed token used");
            var path = Uri.UnescapeDataString(request.RequestUri.AbsolutePath);
            if (path.EndsWith("/special/approot")) return Json("{\"id\":\"app\",\"folder\":{}}");
            if (path.Contains("PotatoVN_Data")) return Json("{\"id\":\"backup\",\"folder\":{}}");
            return Json("{\"id\":\"file\",\"size\":3}");
        }));
        using var provider = new OneDriveProvider(profile, secrets, http);
        True(await provider.ExistsAsync("v1/file"), "OneDrive object found after refresh");
        Equal(1, refreshes, "one refresh only");
        var stored = JsonNode.Parse((await secrets.ReadAsync(profile.Id + ":tokens"))!)!;
        Equal("refresh-test", stored["RefreshToken"]?.GetValue<string>(), "refresh token retained when omitted by provider");
    }
}

async Task OneDriveChunks()
{
    var profile = new ProviderProfile { Kind = ProviderKind.OneDrive, ClientId = "test-client" };
    var secrets = await Tokens(profile);
    var source = Path.Combine(taskRoot, "onedrive-source");
    var bytes = RandomNumberGenerator.GetBytes(11 * 1024 * 1024);
    await File.WriteAllBytesAsync(source, bytes);
    var transferred = new MemoryStream();
    var chunks = 0;
    using var http = new HttpClient(new FakeHandler(async request =>
    {
        var uri = request.RequestUri!;
        if (uri.Host == "upload.test")
        {
            True(request.Headers.Authorization is null, "OneDrive upload session must not receive bearer token");
            var range = request.Content!.Headers.ContentRange!;
            Equal(transferred.Length, range.From!.Value, "OneDrive sequential offsets");
            var data = await request.Content.ReadAsByteArrayAsync();
            await transferred.WriteAsync(data);
            chunks++;
            if (transferred.Length == bytes.Length)
                return Json($"{{\"id\":\"file\",\"size\":{bytes.Length}}}", HttpStatusCode.Created);
            Equal(0, data.Length % (320 * 1024), "Graph fragment unit");
            return Json($"{{\"nextExpectedRanges\":[\"{transferred.Length}-\"]}}", HttpStatusCode.Accepted);
        }
        if (uri.Host == "download.test")
        {
            True(request.Headers.Authorization is null, "OneDrive download URL must not receive bearer token");
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
        Equal("access-test", request.Headers.Authorization?.Parameter, "Graph API bearer token");
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (path.EndsWith("/special/approot")) return Json("{\"id\":\"app\",\"folder\":{}}");
        if (path.Contains("PotatoVN_Data")) return Json("{\"id\":\"backup\",\"folder\":{}}");
        if (path.EndsWith("createUploadSession")) return Json("{\"uploadUrl\":\"https://upload.test/session?sig=fake\"}");
        if (path.EndsWith(":/v1")) return Json("{\"id\":\"v1\",\"folder\":{}}");
        return Json($"{{\"id\":\"file\",\"size\":{bytes.Length},\"@microsoft.graph.downloadUrl\":\"https://download.test/file?sig=fake\"}}");
    }));
    using var provider = new OneDriveProvider(profile, secrets, http);
    await provider.UploadAsync("v1/large.zip", source);
    Equal(2, chunks, "Graph chunk count");
    True(bytes.SequenceEqual(transferred.ToArray()), "OneDrive uploaded bytes match");
    var target = Path.Combine(taskRoot, "onedrive-target");
    await provider.DownloadAsync("v1/large.zip", target);
    True(bytes.SequenceEqual(await File.ReadAllBytesAsync(target)), "OneDrive download bytes match");
}

async Task GoogleChunks()
{
    var profile = new ProviderProfile { Kind = ProviderKind.GoogleDrive, ClientId = "google-client" };
    var secrets = await Tokens(profile);
    var source = Path.Combine(taskRoot, "google-source");
    var bytes = RandomNumberGenerator.GetBytes(9 * 1024 * 1024);
    await File.WriteAllBytesAsync(source, bytes);
    var transferred = new MemoryStream();
    var chunks = 0;
    var uploaded = false;
    var pageRequests = 0;
    using var http = new HttpClient(new FakeHandler(async request =>
    {
        Equal("access-test", request.Headers.Authorization?.Parameter, "Google API bearer token");
        var uri = request.RequestUri!;
        var query = Uri.UnescapeDataString(uri.Query);
        if (uri.AbsolutePath.StartsWith("/upload/") && request.Method == HttpMethod.Post)
        {
            Equal(bytes.Length.ToString(), request.Headers.GetValues("X-Upload-Content-Length").Single(), "Google declared size");
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Location = new Uri("https://www.googleapis.com/upload/session");
            return response;
        }
        if (request.Method == HttpMethod.Put)
        {
            var data = await request.Content!.ReadAsByteArrayAsync();
            Equal(transferred.Length, request.Content.Headers.ContentRange!.From!.Value, "Google sequential offsets");
            await transferred.WriteAsync(data);
            chunks++;
            if (transferred.Length == bytes.Length)
            {
                uploaded = true;
                return Json($"{{\"id\":\"file\",\"size\":\"{bytes.Length}\"}}");
            }
            Equal(0, data.Length % (256 * 1024), "Google fragment unit");
            var response = new HttpResponseMessage((HttpStatusCode)308);
            response.Headers.TryAddWithoutValidation("Range", $"bytes=0-{transferred.Length - 1}");
            return response;
        }
        if (request.Method == HttpMethod.Delete)
        {
            uploaded = false;
            return new(HttpStatusCode.NoContent);
        }
        if (query.Contains("alt=media")) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        if (query.Contains("name = 'PotatoVN_Data'")) return Json("{\"files\":[{\"id\":\"backup\",\"name\":\"PotatoVN_Data\",\"mimeType\":\"application/vnd.google-apps.folder\"}]}");
        if (query.Contains("name = 'v1'")) return Json("{\"files\":[{\"id\":\"v1\",\"name\":\"v1\",\"mimeType\":\"application/vnd.google-apps.folder\"}]}");
        if (query.Contains("name = 'large.zip'")) return Json(uploaded ? $"{{\"files\":[{{\"id\":\"file\",\"name\":\"large.zip\",\"size\":\"{bytes.Length}\"}}]}}" : "{\"files\":[]}");
        pageRequests++;
        return query.Contains("pageToken=next")
            ? Json("{\"files\":[{\"id\":\"second\",\"name\":\"second.json\",\"size\":\"7\"}]}")
            : Json($"{{\"files\":[{{\"id\":\"file\",\"name\":\"large.zip\",\"size\":\"{bytes.Length}\"}}],\"nextPageToken\":\"next\"}}");
    }));
    using var provider = new GoogleDriveProvider(profile, secrets, http);
    await provider.UploadAsync("v1/large.zip", source);
    Equal(2, chunks, "Google chunk count");
    True(bytes.SequenceEqual(transferred.ToArray()), "Google uploaded bytes match");
    var listed = await provider.ListAsync("v1");
    Equal(2, listed.Count, "Google list traverses all pages");
    Equal(2, pageRequests, "Google pagination requests");
    Equal(7L, listed[1].Length, "Google string size parsed");
    var target = Path.Combine(taskRoot, "google-target");
    await provider.DownloadAsync("v1/large.zip", target);
    True(bytes.SequenceEqual(await File.ReadAllBytesAsync(target)), "Google download bytes match");
    await provider.DeleteAsync("v1/large.zip");
    True(!await provider.ExistsAsync("v1/large.zip"), "Google delete");
}

async Task GoogleDuplicates()
{
    var profile = new ProviderProfile { Kind = ProviderKind.GoogleDrive, ClientId = "google-client" };
    var secrets = await Tokens(profile);
    using var http = new HttpClient(new FakeHandler(_ => Task.FromResult(Json("{\"files\":[{\"id\":\"a\",\"name\":\"PotatoVN_Data\"},{\"id\":\"b\",\"name\":\"PotatoVN_Data\"}]}"))));
    using var provider = new GoogleDriveProvider(profile, secrets, http);
    await Throws<IOException>(() => provider.ProbeAsync());
}

async Task DpapiRoundTrip()
{
    if (!OperatingSystem.IsWindows()) { Console.WriteLine("SKIP DPAPI requires Windows"); return; }
    var directory = Path.Combine(taskRoot, "credentials");
    var store = new DpapiSecretStore(directory);
    const string clear = "fake-provider-令牌-value";
    await store.WriteAsync("test-key", clear);
    var encrypted = await File.ReadAllBytesAsync(Directory.GetFiles(directory).Single());
    True(!Encoding.UTF8.GetString(encrypted).Contains(clear), "DPAPI plaintext absent");
    Equal(clear, await new DpapiSecretStore(directory).ReadAsync("test-key"), "DPAPI persistence");
    await store.DeleteAsync("test-key");
    True(await store.ReadAsync("test-key") is null, "DPAPI deletion");
}

static async Task<MemorySecrets> Tokens(ProviderProfile profile, bool expired = false)
{
    var store = new MemorySecrets();
    var microsoft = profile.Kind == ProviderKind.OneDrive;
    await store.WriteAsync(profile.Id + ":tokens", JsonSerializer.Serialize(new
    {
        AccessToken = "access-test", RefreshToken = "refresh-test", ClientId = profile.ClientId,
        ExpiresUtc = expired ? DateTimeOffset.UtcNow.AddHours(-1) : DateTimeOffset.UtcNow.AddHours(1),
        Authority = microsoft ? "https://login.microsoftonline.com/common/oauth2/v2.0/token" : "https://oauth2.googleapis.com/token",
        Scope = microsoft ? "https://graph.microsoft.com/Files.ReadWrite.AppFolder offline_access" : "https://www.googleapis.com/auth/drive.file"
    }));
    return store;
}
static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK) => new(status)
{ Content = new StringContent(text, Encoding.UTF8, "application/json") };
static void True(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Equal<T>(T expected, T actual, string message)
{ if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{message}: expected {expected}, actual {actual}"); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return handle(request); }
}
sealed class MemorySecrets : ISecretStore
{
    private readonly Dictionary<string, string> _values = [];
    public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(_values.GetValueOrDefault(key));
    public Task WriteAsync(string key, string value, CancellationToken cancellationToken = default) { _values[key] = value; return Task.CompletedTask; }
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) { _values.Remove(key); return Task.CompletedTask; }
}
