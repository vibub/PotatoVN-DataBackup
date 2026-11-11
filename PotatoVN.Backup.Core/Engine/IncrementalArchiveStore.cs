using System.IO.Compression;
using System.Security.Cryptography;

namespace PotatoVN.Backup.Core.Engine;

/// <summary>将 ZIP 中的文件保存为独立 GZip 对象；只复用已经发布或本轮完整上传的对象。</summary>
public sealed class IncrementalArchiveStore
{
    private readonly IBackupProvider _provider;
    private readonly string _work;
    private readonly IReadOnlySet<string>? _committedObjects;
    private readonly HashSet<string> _completedObjects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _downloadedObjects = new(StringComparer.Ordinal);
    private static readonly DateTimeOffset ZipEpoch = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public int UploadedFiles { get; private set; }
    public int ReusedFiles { get; private set; }
    public long UploadedBytes { get; private set; }

    public IncrementalArchiveStore(IBackupProvider provider, string work, IReadOnlySet<string>? committedObjects = null)
    {
        _provider = provider;
        _committedObjects = committedObjects;
        _work = Path.Combine(Path.GetFullPath(work), "incremental-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
    }

    public async Task<ArchiveRecipe> UploadAsync(string archive, Guid deviceId, CancellationToken ct = default)
    {
        if (deviceId == Guid.Empty) throw new InvalidDataException("增量备份缺少设备标识。");
        using var zip = ZipFile.OpenRead(archive);
        ZipArchiveEntry[] entries = zip.Entries.OrderBy(entry => entry.FullName, StringComparer.Ordinal).ToArray();
        if (entries.Length == 0) throw new InvalidDataException("增量备份不能使用空压缩包。");
        foreach (ZipArchiveEntry entry in entries)
        {
            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (entry.FullName.EndsWith('/') || entry.Name.Length == 0 ||
                (entry.ExternalAttributes & (int)FileAttributes.Directory) != 0 ||
                unixType is 0x4000 or 0xA000)
                throw new InvalidDataException("增量备份只接受普通文件，不接受目录或符号链接条目。");
        }
        ValidatePaths(entries.Select(entry => entry.FullName));
        var recipe = new ArchiveRecipe { DeviceId = deviceId };
        foreach (ZipArchiveEntry entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            string compressed = Temporary(".gz");
            try
            {
                string hash;
                await using (Stream input = entry.Open())
                await using (FileStream output = File.Create(compressed))
                await using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
                    hash = await CopyAndHashAsync(input, gzip, entry.Length, ct);
                string remote = ObjectPath(deviceId, hash);
                if (_completedObjects.Contains(remote) ||
                    (_committedObjects?.Contains(remote) == true && await RemoteCommit.IsReadyAsync(_provider, remote, _work, ct)))
                {
                    ReusedFiles++;
                    _completedObjects.Add(remote);
                }
                else
                {
                    await RemoteCommit.UploadAsync(_provider, remote, compressed, _work, ct);
                    _completedObjects.Add(remote);
                    UploadedFiles++;
                    UploadedBytes += new FileInfo(compressed).Length;
                }
                recipe.Files.Add(new FileRecord { Path = entry.FullName, Length = entry.Length, Sha256 = hash });
            }
            finally { if (File.Exists(compressed)) File.Delete(compressed); }
        }
        return recipe;
    }

    public async Task RestoreAsync(ArchiveRecipe recipe, string archive, CancellationToken ct = default)
    {
        Validate(recipe);
        string destination = Path.GetFullPath(archive);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".incremental-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                foreach (FileRecord file in recipe.Files.OrderBy(file => file.Path, StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    string source = await DownloadObjectAsync(recipe.DeviceId, file, ct);
                    ZipArchiveEntry entry = zip.CreateEntry(file.Path, CompressionLevel.Optimal);
                    entry.LastWriteTime = ZipEpoch;
                    await using Stream input = File.OpenRead(source);
                    await using Stream output = entry.Open();
                    await input.CopyToAsync(output, ct);
                }
            }
            ct.ThrowIfCancellationRequested();
            AtomicFiles.Commit(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<bool> IsAvailableAsync(ArchiveRecipe recipe, CancellationToken ct = default)
    {
        foreach (string path in ObjectPaths(recipe))
            if (!await RemoteCommit.IsReadyAsync(_provider, path, _work, ct)) return false;
        return true;
    }

    public static void Validate(ArchiveRecipe recipe)
    {
        if (recipe is null || recipe.DeviceId == Guid.Empty || recipe.Files is null || recipe.Files.Count == 0)
            throw new InvalidDataException("增量文件清单缺少设备标识或文件。");
        var lengths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (FileRecord file in recipe.Files)
        {
            if (file is null || file.Length < 0) throw new InvalidDataException("增量文件清单中的长度无效。");
            ValidateHash(file.Sha256);
            if (lengths.TryGetValue(file.Sha256, out long length) && length != file.Length)
                throw new InvalidDataException("相同内容的增量对象具有不一致的长度。");
            lengths[file.Sha256] = file.Length;
        }
        ValidatePaths(recipe.Files.Select(file => file.Path));
    }

    public static IEnumerable<string> ObjectPaths(ArchiveRecipe recipe)
    {
        Validate(recipe);
        return recipe.Files.Select(file => ObjectPath(recipe.DeviceId, file.Sha256)).Distinct(StringComparer.Ordinal);
    }

    public static string ObjectPath(Guid deviceId, string sha256)
    {
        if (deviceId == Guid.Empty) throw new InvalidDataException("增量对象缺少设备标识。");
        ValidateHash(sha256);
        string hash = sha256.ToLowerInvariant();
        return $"v2/objects/{deviceId:N}/{hash[..2]}/{hash}.gz";
    }

    private async Task<string> DownloadObjectAsync(Guid deviceId, FileRecord file, CancellationToken ct)
    {
        string remote = ObjectPath(deviceId, file.Sha256);
        if (_downloadedObjects.TryGetValue(remote, out string? cached))
        {
            if (new FileInfo(cached).Length != file.Length) throw new InvalidDataException("增量对象的文件长度不一致。");
            return cached;
        }
        string compressed = Temporary(".gz");
        string raw = Temporary(".bin");
        bool complete = false;
        try
        {
            if (!await RemoteCommit.DownloadIfCommittedAsync(_provider, remote, compressed, _work, ct))
                throw new InvalidDataException("增量对象尚未完整提交，请重新备份后再恢复。");
            string actual;
            await using (FileStream input = File.OpenRead(compressed))
            await using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            await using (FileStream output = File.Create(raw))
                actual = await CopyAndHashAsync(gzip, output, file.Length, ct);
            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("增量对象校验失败，未恢复软件资料或游戏存档。");
            _downloadedObjects.Add(remote, raw);
            complete = true;
            return raw;
        }
        finally
        {
            if (File.Exists(compressed)) File.Delete(compressed);
            if (!complete && File.Exists(raw)) File.Delete(raw);
        }
    }

    private static async Task<string> CopyAndHashAsync(Stream input, Stream output, long expectedLength, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long length = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            if (read > expectedLength - length) throw new InvalidDataException("增量文件长度超过清单记录。");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            length += read;
        }
        if (length != expectedLength) throw new InvalidDataException("增量文件内容不完整，长度与清单不符。");
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void ValidateHash(string hash)
    {
        if (string.IsNullOrEmpty(hash) || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("增量对象的 SHA-256 无效。");
    }

    private static void ValidatePaths(IEnumerable<string> paths)
    {
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Any(char.IsControl) ||
                path.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                    part.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0 || IsReservedName(part)))
                throw new InvalidDataException("增量清单含有不安全的文件路径。");
            if (!unique.Add(path)) throw new InvalidDataException("增量清单含有重复文件路径。");
        }
        foreach (string path in unique)
            for (int slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                if (unique.Contains(path[..slash])) throw new InvalidDataException("增量清单中的文件与子目录路径冲突。");
    }

    private static bool IsReservedName(string part)
    {
        string name = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) &&
            name[3] is >= '1' and <= '9';
    }

    private string Temporary(string extension) => Path.Combine(_work, Guid.NewGuid().ToString("N") + extension);
}