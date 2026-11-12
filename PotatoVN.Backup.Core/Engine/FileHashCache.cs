using System.Text.Json;

namespace PotatoVN.Backup.Core.Engine;

/// <summary>只缓存图片属性和摘要；存档始终计算内容哈希，缓存不是云端提交凭证。</summary>
public sealed class FileHashCache
{
    public sealed class Entry
    {
        public long Length { get; set; }
        public long WriteTicks { get; set; }
        public string Sha256 { get; set; } = "";
        public DateTimeOffset VerifiedUtc { get; set; }
    }
    private readonly string _file;
    private readonly TimeProvider _time;
    private Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    public int ComputedHashes { get; private set; }
    public int ReusedHashes { get; private set; }
    public FileHashCache(string root, TimeProvider? time = null)
    { _file = Path.Combine(root, "cache", "images.json"); _time = time ?? TimeProvider.System; }
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_file)) return;
        try
        {
            if (new FileInfo(_file).Length > 32 * 1024 * 1024) return;
            var data = await BackupFiles.ReadJsonAsync<Dictionary<string, Entry>>(_file, ct);
            if (data.Count <= 100000) _entries = new(data, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { _entries.Clear(); } // 这是可重建的摘要缓存，失败后重新读取源图片。
    }
    public async Task<PreparedFile> ReadAsync(string logicalPath, string source, CancellationToken ct = default)
    {
        source = Path.GetFullPath(source);
        _seen.Add(source);
        var info = new FileInfo(source);
        DateTimeOffset now = _time.GetUtcNow();
        if (_entries.TryGetValue(source, out var item) && item != null && item.Length == info.Length && item.WriteTicks == info.LastWriteTimeUtc.Ticks &&
            PackedFormat.IsHash(item.Sha256) && now >= item.VerifiedUtc && now - item.VerifiedUtc < TimeSpan.FromDays(7))
        {
            ReusedHashes++;
            return new PreparedFile { SourcePath = source, Record = new FileRecord { Path = logicalPath, Length = item.Length, Sha256 = item.Sha256 } };
        }
        PreparedFile file = await PreparedFile.ReadAsync(logicalPath, source, ct);
        ComputedHashes++;
        _entries[source] = new Entry { Length = file.Record.Length, WriteTicks = info.LastWriteTimeUtc.Ticks, Sha256 = file.Record.Sha256, VerifiedUtc = now };
        return file;
    }
    public Task SaveAsync(CancellationToken ct = default)
    {
        long budget = 0;
        _entries = _entries.Where(pair => _seen.Contains(pair.Key)).Take(100000)
            .TakeWhile(pair => (budget += pair.Key.Length * 6L + 512) <= 16 * 1024 * 1024)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return BackupFiles.WriteJsonAsync(_file, _entries, ct);
    }
}
