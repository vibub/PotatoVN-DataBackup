using System.Text.Json;

namespace PotatoVN.Backup.Core.Engine;

public sealed record RemoteCommitInfo(long Length, string Sha256);

/// <summary>v2 文件只有完整载荷及最后写入的提交标记同时存在时才可复用。</summary>
public static class RemoteCommit
{
    private const int MaximumMarkerBytes = 8 * 1024;

    public static string MarkerPath(string path) => path + ".complete";

    public static async Task UploadAsync(IBackupProvider provider, string path, string local, string work,
        CancellationToken ct = default)
    {
        ValidatePath(path);
        long length = new FileInfo(local).Length;
        string hash = await BackupFiles.HashAsync(local, ct);
        byte[] markerBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            FormatVersion = 2, Path = path, Length = length, Sha256 = hash
        }, BackupJson.Options);
        if (markerBytes.Length > MaximumMarkerBytes) throw new InvalidDataException("提交标记超过 8 KB。");
        string marker = MarkerPath(path);
        // 重传前撤销旧提交，防止失败的覆盖上传仍被旧标记误认为完整文件。
        if (await provider.ExistsAsync(marker, ct)) await provider.DeleteAsync(marker, ct);
        await provider.UploadAsync(path, local, ct);
        string temporary = TemporaryMarker(work);
        try
        {
            await File.WriteAllBytesAsync(temporary, markerBytes, ct);
            await provider.UploadAsync(marker, temporary, ct);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<RemoteCommitInfo?> ReadMarkerAsync(IBackupProvider provider, string path, string work,
        CancellationToken ct = default)
    {
        ValidatePath(path);
        string marker = MarkerPath(path);
        if (!await provider.ExistsAsync(marker, ct)) return null;
        string temporary = TemporaryMarker(work);
        try
        {
            await provider.DownloadAsync(marker, temporary, ct);
            if (new FileInfo(temporary).Length > MaximumMarkerBytes) return null;
            await using FileStream stream = File.OpenRead(temporary);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("FormatVersion", out JsonElement format) || format.ValueKind != JsonValueKind.Number ||
                !format.TryGetInt32(out int version) || version != 2 ||
                !root.TryGetProperty("Path", out JsonElement remote) || remote.ValueKind != JsonValueKind.String ||
                !string.Equals(remote.GetString(), path, StringComparison.Ordinal) ||
                !root.TryGetProperty("Length", out JsonElement bytes) || bytes.ValueKind != JsonValueKind.Number ||
                !bytes.TryGetInt64(out long length) || length < 0 ||
                !root.TryGetProperty("Sha256", out JsonElement checksum) || checksum.ValueKind != JsonValueKind.String)
                return null;
            string? hash = checksum.GetString();
            if (string.IsNullOrEmpty(hash) || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) return null;
            return new RemoteCommitInfo(length, hash.ToLowerInvariant());
        }
        catch (JsonException) { return null; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<bool> IsReadyAsync(IBackupProvider provider, string path, string work,
        CancellationToken ct = default) =>
        await ReadMarkerAsync(provider, path, work, ct) != null && await provider.ExistsAsync(path, ct);

    public static async Task<bool> DownloadIfCommittedAsync(IBackupProvider provider, string path, string local, string work,
        CancellationToken ct = default)
    {
        RemoteCommitInfo? marker = await ReadMarkerAsync(provider, path, work, ct);
        if (marker == null) return false;
        string destination = Path.GetFullPath(local);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".remote-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await provider.DownloadAsync(path, temporary, ct);
            if (new FileInfo(temporary).Length != marker.Length ||
                !string.Equals(await BackupFiles.HashAsync(temporary, ct), marker.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("云端文件与提交标记不符，文件可能尚未完整上传。");
            ct.ThrowIfCancellationRequested();
            AtomicFiles.Commit(temporary, destination);
            return true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string TemporaryMarker(string work)
    {
        string root = Path.GetFullPath(work);
        Directory.CreateDirectory(root);
        return Path.Combine(root, "remote-commit-" + Guid.NewGuid().ToString("N") + ".json");
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("v2/", StringComparison.Ordinal) ||
            path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl) ||
            path.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("提交标记仅支持安全的 v2 云端路径。");
    }
}