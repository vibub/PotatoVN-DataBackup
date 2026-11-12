using System.IO.Compression;
using System.Security.Cryptography;

namespace PotatoVN.Backup.Core.Engine;

/// <summary>文件配方引用内容哈希；本轮新增内容合包，索引提交前不删除既有对象。</summary>
public sealed class PackedArchiveStore
{
    public const long MediaPackBytes = 32L * 1024 * 1024;
    public const long SavePackBytes = 8L * 1024 * 1024;
    private const int MaximumEntries = 256;
    private readonly IBackupProvider _provider;
    private readonly PackedCatalog _catalog;
    private PackedState State => _catalog.State;
    private readonly string _work;
    private readonly Dictionary<string, (PreparedFile File, bool Media)> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<int, List<RemoteItem>> _listed = [];
    private readonly HashSet<string> _missing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PackedObject> _previousObjects;
    public bool HasMaintenanceChanges { get; private set; }
    private readonly Dictionary<string, string> _downloads = new(StringComparer.Ordinal);
    public int UploadedObjects { get; private set; }
    public int UploadedFiles { get; private set; }
    public int ReusedFiles { get; private set; }
    public long UploadedBytes { get; private set; }
    public event Action<int, int>? UploadProgress;
    public event Action<int, int>? RestoreProgress;
    public PackedArchiveStore(IBackupProvider provider, PackedCatalog catalog, string work)
    {
        _provider = provider; _catalog = catalog;
        _work = Path.Combine(work, "packed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
        _previousObjects = catalog.ProtectedStates.SelectMany(state => state.Objects.Values)
            .GroupBy(item => PackedFormat.ObjectPath(State.DeviceId, item), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => PackedFormat.Clone(group.First()), StringComparer.Ordinal);
    }
    public async Task CheckObjectsAsync(CancellationToken ct = default)
    {
        // 先发现分桶，再逐桶列一次。单桶最多500个对象，避开坚果云750项列表限制。
        foreach (RemoteItem directory in await _provider.ListAsync($"v3/objects/{State.DeviceId:N}", ct))
        {
            string prefix = $"v3/objects/{State.DeviceId:N}/";
            if (!directory.Path.StartsWith(prefix, StringComparison.Ordinal) || directory.Path[prefix.Length..].Contains('/'))
                throw new InvalidDataException("v3对象目录越界。");
            if (directory.IsDirectory && int.TryParse(directory.Path[prefix.Length..], out int bucket) && bucket is >= 0 and <= 511 && !State.Buckets.Contains(bucket)) State.Buckets.Add(bucket);
        }
        foreach (int bucket in State.Buckets.Concat(_catalog.ProtectedStates.SelectMany(state => state.Buckets)).Distinct().Order()) await ListBucketAsync(bucket, ct);
        foreach (var pair in State.Objects)
        {
            PackedObject item = pair.Value;
            if (!_listed[item.Bucket].Any(file => !file.IsDirectory && file.Path == PackedFormat.ObjectPath(State.DeviceId, item) && file.Length == item.Length)) _missing.Add(pair.Key);
        }
    }
    private async Task ListBucketAsync(int bucket, CancellationToken ct)
    {
        if (_listed.ContainsKey(bucket)) return;
        string prefix = PackedFormat.BucketPath(State.DeviceId, bucket) + "/";
        var items = (await _provider.ListAsync(prefix.TrimEnd('/'), ct)).ToList();
        if (items.Any(item => !item.Path.StartsWith(prefix, StringComparison.Ordinal) || item.Path[prefix.Length..].Contains('/'))) throw new InvalidDataException("v3分桶目录越界。");
        if (items.Count >= 750) throw new IOException("v3分桶项目过多，无法确认列表完整性，已停止写入与清理。");
        _listed[bucket] = items;
    }
    public ArchiveRecipe Register(IReadOnlyList<PreparedFile> files, bool media = false)
    {
        var recipe = new ArchiveRecipe { DeviceId = State.DeviceId, Files = files.Select(file => PackedFormat.Clone(file.Record)).ToList() };
        PackedFormat.ValidateRecipe(recipe, State.DeviceId);
        foreach (PreparedFile file in files)
        {
            string hash = file.Record.Sha256;
            if (State.Files.TryGetValue(hash, out var existing) && !_missing.Contains(existing.ObjectId))
            {
                if (existing.Length != file.Record.Length) throw new InvalidDataException("同一内容摘要的文件长度不一致。");
                ReusedFiles++;
            }
            else if (_pending.TryGetValue(hash, out var pending))
            {
                if (pending.File.Record.Length != file.Record.Length) throw new InvalidDataException("重复摘要的文件长度不一致。");
                ReusedFiles++;
            }
            else _pending.Add(hash, (file, media));
        }
        return recipe;
    }
    public bool IsAvailable(ArchiveRecipe recipe) => recipe.DeviceId == State.DeviceId && recipe.Files.All(file =>
        State.Files.TryGetValue(file.Sha256, out var item) && item.Length == file.Length && !_missing.Contains(item.ObjectId));
    public async Task UploadPendingAsync(CancellationToken ct = default)
    {
        foreach (FileRecord file in PackedFormat.ReferencedFiles(State))
            if (!_pending.ContainsKey(file.Sha256) && (!State.Files.TryGetValue(file.Sha256, out var location) || location.Length != file.Length || _missing.Contains(location.ObjectId)))
                throw new InvalidDataException("云端历史内容缺失且本机无法补齐，已停止上传与清理，请先修复历史。");
        int total = _pending.Count;
        foreach (bool media in new[] { false, true })
        {
            long limit = media ? MediaPackBytes : SavePackBytes;
            var batch = new List<PreparedFile>();
            long bytes = 0;
            foreach (var entry in _pending.Values.Where(item => item.Media == media).OrderBy(item => item.File.Record.Sha256, StringComparer.Ordinal).ToArray())
            {
                ct.ThrowIfCancellationRequested();
                PreparedFile file = entry.File;
                if (file.Record.Length >= limit)
                {
                    if (batch.Count > 0) { await UploadBatchAsync(batch, false, ct); batch.Clear(); bytes = 0; UploadProgress?.Invoke(UploadedFiles, total); }
                    await UploadBatchAsync([file], true, ct);
                    UploadProgress?.Invoke(UploadedFiles, total);
                }
                else
                {
                    if (batch.Count > 0 && (batch.Count >= MaximumEntries || bytes + file.Record.Length > limit))
                    { await UploadBatchAsync(batch, false, ct); batch.Clear(); bytes = 0; UploadProgress?.Invoke(UploadedFiles, total); }
                    batch.Add(file); bytes += file.Record.Length;
                }
            }
            if (batch.Count > 0) { await UploadBatchAsync(batch, false, ct); UploadProgress?.Invoke(UploadedFiles, total); }
        }
        _pending.Clear();
    }
    private async Task<int> ChooseBucketAsync(CancellationToken ct)
    {
        foreach (int bucket in State.Buckets.Order())
        {
            await ListBucketAsync(bucket, ct);
            if (_listed[bucket].Count < 500) return bucket;
        }
        int next = State.Buckets.Max() + 1;
        if (next > 511) throw new IOException("v3对象数量超过当前分桶容量，请先整理历史。");
        State.Buckets.Add(next);
        await ListBucketAsync(next, ct);
        return next;
    }
    private async Task UploadBatchAsync(IReadOnlyList<PreparedFile> files, bool blob, CancellationToken ct)
    {
        string local = Path.Combine(_work, Guid.NewGuid().ToString("N") + (blob ? ".gz" : ".zip"));
        try
        {
            if (blob)
            {
                await using var input = File.OpenRead(files[0].SourcePath);
                await using var output = File.Create(local);
                await using var gzip = new GZipStream(output, CompressionLevel.Optimal);
                await CopyVerifiedAsync(input, gzip, files[0].Record, ct);
            }
            else
            {
                using var zip = ZipFile.Open(local, ZipArchiveMode.Create);
                foreach (PreparedFile file in files)
                {
                    ZipArchiveEntry entry = zip.CreateEntry(file.Record.Sha256, CompressionLevel.Optimal);
                    entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    await using var input = File.OpenRead(file.SourcePath);
                    await using var output = entry.Open();
                    await CopyVerifiedAsync(input, output, file.Record, ct);
                }
            }
            string hash = (await BackupFiles.HashAsync(local, ct)).ToLowerInvariant();
            var item = new PackedObject { Sha256 = hash, Length = new FileInfo(local).Length, Bucket = await ChooseBucketAsync(ct), Kind = blob ? PackedObjectKind.GZip : PackedObjectKind.Zip };
            await _provider.UploadAsync(PackedFormat.ObjectPath(State.DeviceId, item), local, ct);
            State.Objects[hash] = item;
            _missing.Remove(hash);
            foreach (PreparedFile file in files) State.Files[file.Record.Sha256] = new PackedLocation { ObjectId = hash, Length = file.Record.Length };
            _listed[item.Bucket].RemoveAll(file => file.Path == PackedFormat.ObjectPath(State.DeviceId, item));
            _listed[item.Bucket].Add(new RemoteItem(PackedFormat.ObjectPath(State.DeviceId, item), false, item.Length, DateTimeOffset.UtcNow));
            UploadedObjects++; UploadedFiles += files.Count; UploadedBytes += item.Length;
        }
        finally { if (File.Exists(local)) File.Delete(local); }
    }
    public void TrimUnreferenced()
    {
        HashSet<string> needed = PackedFormat.ReferencedFiles(State).Select(file => file.Sha256).ToHashSet(StringComparer.Ordinal);
        foreach (string hash in State.Files.Keys.Where(hash => !needed.Contains(hash)).ToArray()) State.Files.Remove(hash);
        HashSet<string> objects = State.Files.Values.Select(file => file.ObjectId).ToHashSet(StringComparer.Ordinal);
        foreach (string hash in State.Objects.Keys.Where(hash => !objects.Contains(hash)).ToArray()) State.Objects.Remove(hash);
        HashSet<string> livePaths = State.Objects.Values.Select(item => PackedFormat.ObjectPath(State.DeviceId, item)).ToHashSet(StringComparer.Ordinal);
        string before = string.Join("|", State.RetiredObjects.Select(item => PackedFormat.ObjectPath(State.DeviceId, item)).Order(StringComparer.Ordinal));
        State.RetiredObjects = State.RetiredObjects.Concat(_previousObjects.Where(pair => !livePaths.Contains(pair.Key)).Select(pair => pair.Value))
            .Where(item => !livePaths.Contains(PackedFormat.ObjectPath(State.DeviceId, item)))
            .GroupBy(item => PackedFormat.ObjectPath(State.DeviceId, item), StringComparer.Ordinal).Select(group => group.First())
            .Where(item => _listed.TryGetValue(item.Bucket, out var listed) && listed.Any(file => file.Path == PackedFormat.ObjectPath(State.DeviceId, item)))
            .ToList();
        HashSet<string> oldSlotPaths = _catalog.ProtectedStates.SelectMany(state => state.Objects.Values.Select(item => PackedFormat.ObjectPath(State.DeviceId, item))).ToHashSet(StringComparer.Ordinal);
        HasMaintenanceChanges = before != string.Join("|", State.RetiredObjects.Select(item => PackedFormat.ObjectPath(State.DeviceId, item)).Order(StringComparer.Ordinal)) ||
            State.RetiredObjects.Any(item => oldSlotPaths.Contains(PackedFormat.ObjectPath(State.DeviceId, item)));
        foreach (FileRecord file in PackedFormat.ReferencedFiles(State))
            if (!State.Files.TryGetValue(file.Sha256, out var location) || _missing.Contains(location.ObjectId))
                throw new InvalidDataException("云端备份包缺失，且本机没有其全部历史内容；已停止发布与清理，请保留现有文件并修复。");
        PackedFormat.Validate(State, State.DeviceId);
    }
    public async Task CleanupAsync(CancellationToken ct = default)
    {
        HashSet<string> retained = _catalog.ProtectedStates.SelectMany(state => state.Objects.Values.Select(item => PackedFormat.ObjectPath(state.DeviceId, item))).ToHashSet(StringComparer.Ordinal);
        foreach (PackedObject item in State.RetiredObjects)
        {
            // 过期候选保存在索引中，删除失败可在下次重试；两个有效槽共同保护引用。
            string path = PackedFormat.ObjectPath(State.DeviceId, item);
            if (retained.Contains(path) || !_listed.TryGetValue(item.Bucket, out var listed) || !listed.Any(file => file.Path == path)) continue;
            await _provider.DeleteAsync(path, ct);
            listed.RemoveAll(file => file.Path == path);
        }
    }
    public async Task RestoreAsync(ArchiveRecipe recipe, string destination, CancellationToken ct = default)
    {
        PackedFormat.ValidateRecipe(recipe, State.DeviceId);
        string target = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temporary = target + ".packed-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            int restored = 0;
            using (var outputZip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            foreach (var group in recipe.Files.GroupBy(file => State.Files.TryGetValue(file.Sha256, out var location) ? location.ObjectId : throw new InvalidDataException("索引缺少文件对象。")))
            {
                PackedObject item = State.Objects[group.Key];
                string downloaded = await DownloadObjectAsync(item, ct);
                using ZipArchive? packed = item.Kind == PackedObjectKind.Zip ? ZipFile.OpenRead(downloaded) : null;
                if (packed != null && (packed.Entries.Count > MaximumEntries || packed.Entries.Any(entry => !PackedFormat.IsHash(entry.FullName) ||
                    ((entry.ExternalAttributes >> 16) & 0xF000) is 0xA000 or 0x4000) || packed.Entries.Select(entry => entry.FullName).Distinct().Count() != packed.Entries.Count))
                    throw new InvalidDataException("云端包的文件条目无效。");
                foreach (FileRecord file in group)
                {
                    PackedLocation location = State.Files[file.Sha256];
                    if (location.Length != file.Length) throw new InvalidDataException("索引文件长度不一致。");
                    var entry = outputZip.CreateEntry(file.Path, CompressionLevel.Optimal);
                    entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    await using Stream input = packed != null ? (packed.GetEntry(file.Sha256)?.Open() ?? throw new InvalidDataException("云端包缺少文件。")) : new GZipStream(File.OpenRead(downloaded), CompressionMode.Decompress);
                    await using Stream output = entry.Open();
                    await CopyVerifiedAsync(input, output, file, ct);
                    RestoreProgress?.Invoke(++restored, recipe.Files.Count);
                }
            }
            ct.ThrowIfCancellationRequested();
            AtomicFiles.Commit(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private async Task<string> DownloadObjectAsync(PackedObject item, CancellationToken ct)
    {
        if (_downloads.TryGetValue(item.Sha256, out string? cached)) return cached;
        string local = Path.Combine(_work, item.Sha256 + ".download");
        await _provider.DownloadAsync(PackedFormat.ObjectPath(State.DeviceId, item), local, ct);
        if (new FileInfo(local).Length != item.Length || !string.Equals(await BackupFiles.HashAsync(local, ct), item.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("云端包长度或SHA-256校验失败，已停止恢复。");
        _downloads.Add(item.Sha256, local);
        return local;
    }
    private static async Task CopyVerifiedAsync(Stream input, Stream output, FileRecord file, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long copied = 0;
        byte[] buffer = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (count > file.Length - copied) throw new InvalidDataException("文件长度超过清单，内容可能在备份过程中变化。");
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
            copied += count;
        }
        if (copied != file.Length || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("文件内容与摘要不一致，请重新检查后备份或恢复。");
    }
}
