using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace PotatoVN.Backup.Core.Providers;

public static class BackupProviderFactory
{
    public static IBackupProvider Create(ProviderProfile profile, ISecretStore secrets) => profile.Kind switch
    {
        ProviderKind.WebDav => new WebDavProvider(profile, secrets),
        ProviderKind.OneDrive => new OneDriveProvider(profile, secrets),
        ProviderKind.GoogleDrive => new GoogleDriveProvider(profile, secrets),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), "不支持的备份渠道。")
    };
}

internal static class RemotePaths
{
    public static string Normalize(string path)
    {
        var parts = path.Replace('\\', '/').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or ".." || p.Any(char.IsControl)))
            throw new ArgumentException("云端路径不能包含上级目录或控制字符。", nameof(path));
        return string.Join('/', parts);
    }

    public static string Join(string parent, string name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('/') || name.Contains('\\') || name is "." or "..")
            throw new InvalidDataException("云端返回了非法文件名。");
        return Normalize(string.IsNullOrEmpty(parent) ? name : parent + "/" + name);
    }

    public static string Encode(string path) => string.Join('/', Normalize(path).Split('/').Select(Uri.EscapeDataString));
    public static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
    public static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];
}

internal static class ProviderHttp
{
    public static HttpClient CreateClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public static void Check(HttpResponseMessage response, string provider)
    {
        if (response.IsSuccessStatusCode) return;
        var help = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "授权失效，请重新登录或检查账号密码。",
            HttpStatusCode.Forbidden => "没有访问权限，请检查应用授权和目录权限。",
            HttpStatusCode.InsufficientStorage => "云端可用空间不足。",
            HttpStatusCode.TooManyRequests => "请求过于频繁，请稍后重试。",
            HttpStatusCode.ServiceUnavailable => "服务暂时不可用或请求过于频繁，请稍后重试。",
            _ => "操作未完成，请检查网络或服务状态。"
        };
        // 不把带令牌的上传/下载 URL 或服务端原始响应写入异常。
        throw new HttpRequestException($"{provider} 返回 HTTP {(int)response.StatusCode}：{help}", null,
            response.StatusCode);
    }

    public static async Task<JsonObject> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await JsonNode.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false) as JsonObject
            ?? throw new InvalidDataException("云端返回的 JSON 格式不正确。");
    }

    public static StringContent Json(JsonObject value) => new(value.ToJsonString(), Encoding.UTF8, "application/json");
    public static string RequiredString(JsonObject value, string key) => value[key]?.GetValue<string>()
        ?? throw new InvalidDataException($"云端响应缺少 {key}。");
    public static DateTimeOffset? Date(string? text) => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind, out var date) ? date : null;

    public static Uri HttpsUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("云端返回了无效的传输地址。");
        return uri;
    }

    public static async Task DownloadAsync(HttpResponseMessage response, string localFile, CancellationToken token)
    {
        var target = Path.GetFullPath(localFile);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".download-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await response.Content.CopyToAsync(output, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            AtomicFiles.Commit(temporary, target);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static async Task<int> ReadChunkAsync(FileStream stream, byte[] buffer, CancellationToken token)
    {
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), token).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        return count;
    }
}
