using System.Net;
using System.Net.Http.Headers;
using PotatoVN.Backup.Core;
using PotatoVN.Backup.Core.Providers;

internal static class WebDavReliabilityChecks
{
    public static async Task DirectoryReuse(string root)
    {
        string source = Path.Combine(root, "dav-cached-source");
        await File.WriteAllTextAsync(source, "synthetic cached payload");
        var directories = new List<string>();
        using var http = new HttpClient(new FakeHandler(request =>
        {
            if (request.Method.Method == "MKCOL")
            {
                directories.Add(request.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
        }));
        using var provider = new WebDavProvider(Profile(), new MemorySecrets(), http);
        await provider.UploadAsync("v2/objects/device/ab/a.gz", source);
        await provider.UploadAsync("v2/objects/device/ab/a.gz.complete", source);
        await provider.UploadAsync("v2/objects/device/ab/b.gz", source);
        Check(directories.Count == 5 && directories.Distinct().Count() == 5, "同目录载荷与提交标记重复执行MKCOL");
        await provider.UploadAsync("v2/objects/device/cd/c.gz", source);
        Check(directories.Count == 6, "新叶目录重新创建了所有祖先");
        await provider.DeleteAsync("v2/objects/device");
        await provider.UploadAsync("v2/objects/device/ab/a.gz", source);
        Check(directories.Count == 8, "删除目录没有撤销后代目录缓存");
    }

    public static async Task TransientRetry(string root)
    {
        byte[] payload = Enumerable.Range(0, 100000).Select(index => (byte)(index % 251)).ToArray();
        string source = Path.Combine(root, "dav-retry-source");
        await File.WriteAllBytesAsync(source, payload);
        foreach (string method in new[] { "MKCOL", "PROPFIND", "PUT", "GET", "DELETE" })
        {
            var clock = new FastTime();
            int attempts = 0, disposed = 0;
            var messages = new HashSet<HttpRequestMessage>();
            var uploads = new List<byte[]>();
            using var http = new HttpClient(new FakeHandler(async request =>
            {
                if (request.Method.Method != method) return new HttpResponseMessage(HttpStatusCode.Created);
                attempts++;
                Check(messages.Add(request), "重用了已经发送的HttpRequestMessage");
                if (method == "PUT") uploads.Add(await request.Content!.ReadAsByteArrayAsync());
                if (attempts <= 2)
                {
                    var response = new HttpResponseMessage(attempts == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.ServiceUnavailable)
                    { Content = new TrackedContent(() => disposed++) };
                    response.Headers.RetryAfter = attempts == 1
                        ? new RetryConditionHeaderValue(TimeSpan.FromSeconds(5))
                        : new RetryConditionHeaderValue(clock.GetUtcNow().AddSeconds(7));
                    return response;
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
            }));
            using var provider = new WebDavProvider(Profile(), new MemorySecrets(), http, clock);
            switch (method)
            {
                case "MKCOL": case "PUT": await provider.UploadAsync("sample.bin", source); break;
                case "PROPFIND": Check(await provider.ExistsAsync("sample.bin"), "重试后未返回存在"); break;
                case "GET":
                    string target = Path.Combine(root, "dav-retry-target");
                    await provider.DownloadAsync("sample.bin", target);
                    Check((await File.ReadAllBytesAsync(target)).SequenceEqual(payload), "GET重试后数据不完整");
                    break;
                case "DELETE": await provider.DeleteAsync("sample.bin"); break;
            }
            Check(attempts == 3 && disposed == 2, method + "重试次数错误或未释放失败响应");
            Check(clock.Delays.SequenceEqual(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(7) }), "没有遵循Retry-After秒数和日期");
            if (method == "PUT") Check(uploads.Count == 3 && uploads.All(bytes => bytes.SequenceEqual(payload)), "PUT重试读取了空流或部分数据");
        }
    }

    public static async Task RetryLimits()
    {
        foreach (HttpStatusCode status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.ServiceUnavailable, HttpStatusCode.TooManyRequests })
        {
            var clock = new FastTime();
            int requests = 0;
            using var http = new HttpClient(new FakeHandler(_ =>
            { requests++; return Task.FromResult(new HttpResponseMessage(status)); }));
            using var provider = new WebDavProvider(Profile(), new MemorySecrets(), http, clock);
            await Fails<HttpRequestException>(() => provider.ExistsAsync("sample.bin"));
            bool transient = status is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests;
            Check(requests == (transient ? 4 : 1), "授权错误被重试或瞬时错误超过重试上限");
            Check(clock.Delays.SequenceEqual(transient ? new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8) } : []), "默认退避间隔错误");
        }
        var longClock = new FastTime();
        int longRequests = 0;
        using (var http = new HttpClient(new FakeHandler(_ =>
        {
            longRequests++;
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return Task.FromResult(response);
        })))
        using (var provider = new WebDavProvider(Profile(), new MemorySecrets(), http, longClock))
            await Fails<HttpRequestException>(() => provider.ExistsAsync("sample.bin"));
        Check(longRequests == 1 && longClock.Delays.Count == 0, "长时间Retry-After被提前重试或无限等待");

        using var cancellation = new CancellationTokenSource();
        var cancelClock = new FastTime { CancelOnDelay = cancellation.Cancel };
        int cancelRequests = 0;
        using var cancelHttp = new HttpClient(new FakeHandler(_ =>
        { cancelRequests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); }));
        using var cancelProvider = new WebDavProvider(Profile(), new MemorySecrets(), cancelHttp, cancelClock);
        await Fails<OperationCanceledException>(() => cancelProvider.ExistsAsync("sample.bin", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)));
        Check(cancelRequests == 1, "取消退避后仍然发送了请求");
    }

    public static async Task NutstorePacing()
    {
        var clock = new FastTime();
        var sent = new List<DateTimeOffset>();
        using var http = new HttpClient(new FakeHandler(_ =>
        {
            sent.Add(clock.GetUtcNow());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));
        ProviderProfile profile = Profile();
        profile.Endpoint = "https://dav.jianguoyun.com/dav/";
        profile.Username = "synthetic-" + Guid.NewGuid().ToString("N");
        var secrets = new DelayedSecrets(clock);
        using var first = new WebDavProvider(profile, secrets, http, clock);
        using var second = new WebDavProvider(profile, secrets, http, clock);
        await first.ExistsAsync("a"); await second.ExistsAsync("b"); await first.ExistsAsync("c");
        Check(sent.Count == 3 && sent[1] - sent[0] >= TimeSpan.FromSeconds(3.1) && sent[2] - sent[1] >= TimeSpan.FromSeconds(3.1), "同账号跨实例未共享请求间隔");
        using var cancellation = new CancellationTokenSource();
        clock.CancelOnDelay = cancellation.Cancel;
        await Fails<OperationCanceledException>(() => first.ExistsAsync("cancel", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)));
        Check(sent.Count == 3, "取消限速等待后仍然发送了请求");
        clock.CancelOnDelay = null;
        await second.ExistsAsync("after-cancel");
        Check(sent.Count == 4, "取消限速等待后没有释放发送锁");
    }

    private static ProviderProfile Profile() => new() { Kind = ProviderKind.WebDav, Endpoint = "https://dav.test/webdav/" };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Fails<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private sealed class TrackedContent(Action disposed) : ByteArrayContent([])
    {
        protected override void Dispose(bool disposing) { if (disposing) disposed(); base.Dispose(disposing); }
    }
    private sealed class DelayedSecrets(FastTime clock) : ISecretStore
    {
        private int _reads;
        public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
        {
            if (++_reads == 1) clock.Advance(TimeSpan.FromSeconds(10));
            return Task.FromResult<string?>("synthetic-password");
        }
        public Task WriteAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class FastTime : TimeProvider
    {
        private DateTimeOffset _now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public List<TimeSpan> Delays { get; } = [];
        public Action? CancelOnDelay { get; set; }
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Delays.Add(dueTime);
            if (CancelOnDelay is { } cancel) cancel();
            else
            {
                _now += dueTime;
                ThreadPool.QueueUserWorkItem(_ => callback(state));
            }
            return new NoOpTimer();
        }
        private sealed class NoOpTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
