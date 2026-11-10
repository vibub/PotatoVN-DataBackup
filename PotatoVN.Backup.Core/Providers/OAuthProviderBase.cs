using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PotatoVN.Backup.Core.Providers;

public abstract class OAuthProviderBase : IBackupProvider
{
    protected readonly ProviderProfile Profile;
    protected readonly ISecretStore Secrets;
    protected readonly HttpClient Http;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private OAuthToken? _token;
    protected abstract string Label { get; }
    protected abstract string AuthorizeEndpoint { get; }
    protected abstract string TokenEndpoint { get; }
    protected abstract string Scope { get; }
    protected abstract string ApiHost { get; }
    protected virtual bool IsGoogle => false;

    protected OAuthProviderBase(ProviderProfile profile, ISecretStore secrets, HttpClient? http = null)
    {
        Profile = profile;
        Secrets = secrets;
        Http = http ?? ProviderHttp.CreateClient();
        _ownsHttp = http is null;
    }

    public async Task AuthorizeAsync(CancellationToken cancellationToken = default)
    {
        ValidateClient();
        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var state = Base64Url(RandomNumberGenerator.GetBytes(32));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirect = $"http://{(IsGoogle ? "127.0.0.1" : "localhost")}:{port}/";
            var parameters = new Dictionary<string, string>
            {
                ["client_id"] = Profile.ClientId, ["response_type"] = "code", ["redirect_uri"] = redirect,
                ["scope"] = Scope, ["state"] = state, ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256", ["prompt"] = IsGoogle ? "consent" : "select_account"
            };
            if (IsGoogle) parameters["access_type"] = "offline";
            var url = AuthorizeEndpoint + "?" + Query(parameters);
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            var code = await WaitForCodeAsync(listener, state, timeout.Token).ConfigureAwait(false);
            var form = new Dictionary<string, string>
            {
                ["client_id"] = Profile.ClientId, ["grant_type"] = "authorization_code", ["code"] = code,
                ["redirect_uri"] = redirect, ["code_verifier"] = verifier
            };
            await AddClientSecretAsync(form, timeout.Token).ConfigureAwait(false);
            _token = await ExchangeAsync(form, null, timeout.Token).ConfigureAwait(false);
            await SaveTokenAsync(timeout.Token).ConfigureAwait(false);
        }
        finally { _tokenGate.Release(); }
    }

    protected async Task<HttpResponseMessage> SendApiAsync(Func<HttpRequestMessage> create,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = create();
            var uri = request.RequestUri;
            if (uri is null || uri.Scheme != "https" || !uri.Host.Equals(ApiHost, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("拒绝向非渠道 API 地址发送访问令牌。");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                await AccessTokenAsync(cancellationToken).ConfigureAwait(false));
            var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt != 0) return response;
            response.Dispose();
            await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { if (_token is not null) _token.ExpiresUtc = DateTimeOffset.MinValue; }
            finally { _tokenGate.Release(); }
        }
    }

    private async Task<string> AccessTokenAsync(CancellationToken cancellationToken)
    {
        ValidateClient();
        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is null)
            {
                var stored = await Secrets.ReadAsync(Profile.Id + ":tokens", cancellationToken).ConfigureAwait(false);
                if (stored is not null) _token = JsonSerializer.Deserialize<OAuthToken>(stored, BackupJson.Options);
            }
            if (_token is null || _token.ClientId != Profile.ClientId || _token.Authority != TokenEndpoint ||
                _token.Scope != Scope)
                throw new InvalidOperationException($"请先在插件设置中登录 {Label}。");
            if (_token.ExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(2)) return _token.AccessToken;
            if (string.IsNullOrEmpty(_token.RefreshToken))
                throw new InvalidOperationException($"{Label} 缺少长期授权，请重新登录。");
            var form = new Dictionary<string, string>
            {
                ["client_id"] = Profile.ClientId, ["grant_type"] = "refresh_token",
                ["refresh_token"] = _token.RefreshToken
            };
            if (!IsGoogle) form["scope"] = Scope;
            await AddClientSecretAsync(form, cancellationToken).ConfigureAwait(false);
            _token = await ExchangeAsync(form, _token.RefreshToken, cancellationToken).ConfigureAwait(false);
            await SaveTokenAsync(cancellationToken).ConfigureAwait(false);
            return _token.AccessToken;
        }
        finally { _tokenGate.Release(); }
    }

    private void ValidateClient()
    {
        if (string.IsNullOrWhiteSpace(Profile.ClientId)) throw new InvalidOperationException($"请填写 {Label} 应用 ID。");
    }

    private async Task AddClientSecretAsync(Dictionary<string, string> form, CancellationToken token)
    {
        if (!IsGoogle) return;
        var secret = await Secrets.ReadAsync(Profile.Id + ":client-secret", token).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(secret)) form["client_secret"] = secret;
    }

    private async Task<OAuthToken> ExchangeAsync(Dictionary<string, string> form, string? previousRefresh,
        CancellationToken token)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await Http.PostAsync(TokenEndpoint, content, token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            throw new InvalidOperationException($"{Label} 授权未成功或已失效，请检查应用配置并重新登录。");
        ProviderHttp.Check(response, Label);
        var json = await ProviderHttp.ReadJsonAsync(response, token).ConfigureAwait(false);
        var seconds = int.TryParse(json["expires_in"]?.ToString(), out var lifetime) ? lifetime : 3600;
        var refresh = json["refresh_token"]?.GetValue<string>() ?? previousRefresh;
        if (string.IsNullOrEmpty(refresh))
            throw new InvalidOperationException($"{Label} 没有返回长期授权，请撤销旧授权后重新登录。");
        return new OAuthToken
        {
            AccessToken = ProviderHttp.RequiredString(json, "access_token"), RefreshToken = refresh,
            ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(seconds), ClientId = Profile.ClientId,
            Authority = TokenEndpoint, Scope = Scope
        };
    }

    private Task SaveTokenAsync(CancellationToken token) => Secrets.WriteAsync(Profile.Id + ":tokens",
        JsonSerializer.Serialize(_token, BackupJson.Options), token);

    private static async Task<string> WaitForCodeAsync(TcpListener listener, string state, CancellationToken token)
    {
        while (true)
        {
            using var connection = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
            var request = line?.Split(' ');
            var headerBytes = line?.Length ?? 0;
            while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { Length: > 0 } header)
            {
                headerBytes += header.Length;
                if (headerBytes > 16384) throw new InvalidDataException("授权回调请求过大。");
            }
            if (request is not { Length: >= 2 } || request[0] != "GET" || !request[1].StartsWith('/'))
            {
                await ReplyAsync(stream, "400 Bad Request", "无效的授权回调。", token).ConfigureAwait(false);
                continue;
            }
            var uri = new Uri("http://127.0.0.1" + request[1]);
            var values = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2)).ToDictionary(p => Decode(p[0]), p => p.Length > 1 ? Decode(p[1]) : "");
            if (uri.AbsolutePath != "/" || !values.TryGetValue("state", out var returnedState) || returnedState != state)
            {
                await ReplyAsync(stream, "400 Bad Request", "授权状态不匹配，请返回插件重试。", token).ConfigureAwait(false);
                continue;
            }
            if (values.ContainsKey("error"))
            {
                await ReplyAsync(stream, "200 OK", "授权已取消，可以关闭此页面。", token).ConfigureAwait(false);
                throw new InvalidOperationException("云盘登录已取消或未获授权。");
            }
            if (!values.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
                throw new InvalidDataException("授权回调缺少授权码。");
            await ReplyAsync(stream, "200 OK", "授权已收到，请返回 PotatoVN；此页面可以关闭。", token).ConfigureAwait(false);
            return code;
        }
    }

    private static async Task ReplyAsync(Stream stream, string status, string message, CancellationToken token)
    {
        var body = Encoding.UTF8.GetBytes("<!doctype html><meta charset=\"utf-8\"><title>PotatoVN 云备份</title><p>" + message + "</p>");
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: {body.Length}\r\n\r\n");
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
    private static string Query(Dictionary<string, string> values) => string.Join('&', values.Select(p =>
        Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public abstract Task ProbeAsync(CancellationToken cancellationToken = default);
    public abstract Task<IReadOnlyList<RemoteItem>> ListAsync(string directory, CancellationToken cancellationToken = default);
    public abstract Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);
    public abstract Task UploadAsync(string path, string localFile, CancellationToken cancellationToken = default);
    public abstract Task DownloadAsync(string path, string localFile, CancellationToken cancellationToken = default);
    public abstract Task DeleteAsync(string path, CancellationToken cancellationToken = default);
    public virtual void Dispose() { if (_ownsHttp) Http.Dispose(); }

    private sealed class OAuthToken
    {
        public OAuthToken() { }
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public DateTimeOffset ExpiresUtc { get; set; }
        public string ClientId { get; set; } = "";
        public string Authority { get; set; } = "";
        public string Scope { get; set; } = "";
    }
}
