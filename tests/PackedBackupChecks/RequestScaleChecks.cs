using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using PotatoVN.Backup.Core;
using PotatoVN.Backup.Core.Engine;
using PotatoVN.Backup.Core.Providers;

internal static class RequestScaleChecks
{
    public static async Task Run(string parent)
    {
        string root = Path.Combine(parent, "scale");
        Directory.CreateDirectory(root);
        var games = new List<GameIdentity>();
        var configs = new List<GameSaveConfig>();
        var images = new Dictionary<string, string>();
        byte[] saveBytes = new byte[100 * 1024], imageBytes = new byte[200 * 1024];
        for (int game = 0; game < 100; game++)
        {
            string gameRoot = Path.Combine(root, "games", game.ToString());
            string saveRoot = Path.Combine(gameRoot, "save"); Directory.CreateDirectory(saveRoot);
            var identity = new GameIdentity { Id = Guid.NewGuid(), Name = "合成游戏" + game, LocalDirectory = gameRoot };
            games.Add(identity);
            for (int file = 0; file < 10; file++)
            {
                BitConverter.TryWriteBytes(saveBytes, game * 10 + file);
                await File.WriteAllBytesAsync(Path.Combine(saveRoot, file + ".dat"), saveBytes);
            }
            for (int image = 0; image < 5; image++)
            {
                string logical = $"Images/{game}-{image}.jpg", source = Path.Combine(gameRoot, image + ".jpg");
                BitConverter.TryWriteBytes(imageBytes, game * 5 + image + 10000);
                await File.WriteAllBytesAsync(source, imageBytes);
                images[logical] = source;
            }
            configs.Add(new GameSaveConfig { GameId = identity.Id, Locations = [new SaveLocation { Id = "save", BaseKind = SaveBaseKind.GameRoot, RelativePath = "save" }] });
        }
        var host = new ScaleHost(games, images);
        using var remote = new Dav();
        using var http = new HttpClient(remote);
        var profile = new ProviderProfile { Kind = ProviderKind.WebDav, Endpoint = "https://dav.test/" };
        var settings = new BackupSettings { Profiles = [profile], ActiveProfileId = profile.Id, SaveConfigs = configs };
        string state = Path.Combine(root, "state");
        BackupCoordinator Coordinator() => new(state, host, settings, new SettingsStore(state), new PendingRestoreStore(state), _ => new WebDavProvider(profile, new Secrets(), http));
        await Coordinator().BackupAsync();
        int initial = remote.Count, packs = remote.Methods.GetValueOrDefault("PUT") - 1;
        remote.Reset();
        await Coordinator().BackupAsync();
        int unchanged = remote.Count;
        Check(!remote.Methods.ContainsKey("PUT") && unchanged <= 6, "100游戏无变化仍逐文件查询或上传");
        await File.AppendAllTextAsync(Path.Combine(games[0].LocalDirectory!, "save", "0.dat"), "updated");
        host.PlayCount++;
        remote.Reset();
        await Coordinator().BackupAsync();
        int delta = remote.Count;
        Check(initial <= 40 && delta <= 10 && remote.Methods.GetValueOrDefault("PUT") == 2, "请求数未达到设计目标或未把两个变化文件合包");
        long localBytes = Directory.EnumerateFiles(state, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        Check(localBytes < 4 * 1024 * 1024 && !Directory.EnumerateFiles(Path.Combine(state, "work"), "*", SearchOption.AllDirectories).Any(), "本机留下了完整备份或过大缓存");
        Console.WriteLine($"REQUEST V3 100 games / 500 images / 1000 saves (~200MiB source): initial={initial}, unchanged={unchanged}, one-game-change={delta}; initial packs={packs}; local persistent metadata={localBytes:N0} bytes");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class ScaleHost(List<GameIdentity> games, Dictionary<string, string> images) : IBackupHost
    {
        public int PlayCount = 1;
        public IReadOnlyList<GameIdentity> GetGames() => games;
        public Task<HostExport> ExportAsync(string work, CancellationToken ct) => throw new InvalidOperationException("v3不得调用全量ZIP导出");
        public async Task<HostExport> ExportForBackupAsync(string work, CancellationToken ct)
        {
            Directory.CreateDirectory(work);
            var raw = new JsonArray();
            for (int i = 0; i < games.Count; i++) raw.Add(new JsonObject
            {
                ["Uuid"] = games[i].Id.ToString(), ["Name"] = new JsonObject { ["Value"] = games[i].Name },
                ["PlayCount"] = i == 0 ? PlayCount : 1, ["ImagePath"] = $"./Images/{i}-0.jpg", ["HeaderImagePath"] = $"./Images/{i}-1.jpg",
                ["Characters"] = new JsonArray(Enumerable.Range(2, 3).Select(image => (JsonNode)new JsonObject { ["ImagePath"] = $"./Images/{i}-{image}.jpg" }).ToArray())
            });
            await File.WriteAllTextAsync(Path.Combine(work, "data.galgames.json"), raw.ToJsonString(), ct);
            foreach (string name in new[] { "galgameSources", "categoryGroups", "staff", "homepage_list" })
                await File.WriteAllTextAsync(Path.Combine(work, "data." + name + ".json"), new JsonArray(new JsonObject { ["Name"] = name }).ToJsonString(), ct);
            return new HostExport { NativeDirectory = work, MediaPaths = images, Settings = new() { ["language"] = JsonValue.Create("zh-CN") } };
        }
        public Task StageImportAsync(string archive, Dictionary<string, JsonNode?> settings, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Secrets : ISecretStore
    {
        public Task<string?> ReadAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task WriteAsync(string key, string value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class Dav : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        private readonly HashSet<string> _directories = new(StringComparer.Ordinal) { "" };
        public Dictionary<string, int> Methods { get; } = [];
        public int Count => Methods.Values.Sum();
        public void Reset() => Methods.Clear();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string method = request.Method.Method;
            Methods[method] = Methods.GetValueOrDefault(method) + 1;
            string path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).TrimEnd('/');
            if (method == "MKCOL") return new(_directories.Add(path) ? HttpStatusCode.Created : HttpStatusCode.MethodNotAllowed);
            if (method == "PUT") { _files[path] = await request.Content!.ReadAsByteArrayAsync(ct); return new(HttpStatusCode.Created); }
            if (method == "GET") return _files.TryGetValue(path, out var data) ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) } : new(HttpStatusCode.NotFound);
            if (method == "DELETE") { _files.Remove(path); return new(HttpStatusCode.NoContent); }
            if (method != "PROPFIND") throw new InvalidOperationException(method);
            if (!_directories.Contains(path) && !_files.ContainsKey(path)) return new(HttpStatusCode.NotFound);
            var paths = new List<string> { path };
            if (request.Headers.GetValues("Depth").Single() == "1") paths.AddRange(_files.Keys.Concat(_directories).Where(item => item != path && item.Length > 0 && item[..item.LastIndexOf('/')] == path));
            XNamespace dav = "DAV:";
            var xml = new XDocument(new XElement(dav + "multistatus", paths.Select(item => new XElement(dav + "response",
                new XElement(dav + "href", string.Join('/', item.Split('/').Select(Uri.EscapeDataString))),
                new XElement(dav + "propstat", new XElement(dav + "prop", new XElement(dav + "resourcetype", _directories.Contains(item) ? new XElement(dav + "collection") : null),
                    new XElement(dav + "getcontentlength", _files.GetValueOrDefault(item)?.LongLength ?? 0)), new XElement(dav + "status", "HTTP/1.1 200 OK"))))));
            return new((HttpStatusCode)207) { Content = new StringContent(xml.ToString(), Encoding.UTF8, "application/xml") };
        }
    }
}
