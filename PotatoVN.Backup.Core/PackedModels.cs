using System.Text.Json;
using PotatoVN.Backup.Core.Engine;

namespace PotatoVN.Backup.Core;

public enum PackedObjectKind { Zip, GZip }
public sealed class PackedObject
{
    public string Sha256 { get; set; } = "";
    public long Length { get; set; }
    public int Bucket { get; set; }
    public PackedObjectKind Kind { get; set; }
}
public sealed class PackedLocation
{
    public string ObjectId { get; set; } = "";
    public long Length { get; set; }
}
public sealed class PackedState
{
    public int FormatVersion { get; set; } = 3;
    public Guid DeviceId { get; set; }
    public long Generation { get; set; }
    public List<int> Buckets { get; set; } = [0];
    public List<SnapshotManifest> Snapshots { get; set; } = [];
    public List<SaveManifest> Saves { get; set; } = [];
    public List<PackedObject> RetiredObjects { get; set; } = [];
    public Dictionary<string, PackedLocation> Files { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, PackedObject> Objects { get; set; } = new(StringComparer.Ordinal);
}
public sealed class PreparedFile
{
    public FileRecord Record { get; init; } = new();
    public string SourcePath { get; init; } = "";
    public static async Task<PreparedFile> ReadAsync(string logicalPath, string source, CancellationToken ct = default)
    {
        var before = new FileInfo(source);
        long length = before.Length;
        DateTime written = before.LastWriteTimeUtc;
        await using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        string hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        var after = new FileInfo(source);
        if (after.Length != length || after.LastWriteTimeUtc != written) throw new IOException("读取时文件发生变化，请稍后重试：" + source);
        return new PreparedFile { SourcePath = source, Record = new FileRecord { Path = logicalPath, Length = length, Sha256 = hash } };
    }
}
public sealed record PreparedSave(SaveManifest Manifest, IReadOnlyList<PreparedFile> Files);

public static class PackedFormat
{
    public static JsonSerializerOptions Json { get; } = new(BackupJson.Options) { WriteIndented = false };
    public static string DeviceRoot(Guid device) => $"v3/devices/{device:N}";
    public static string IndexPath(Guid device, int slot) => $"{DeviceRoot(device)}/state-{(slot == 0 ? "a" : "b")}.json.gz";
    public static string SnapshotPath(Guid device, string id) => $"{DeviceRoot(device)}/snapshots/{id}";
    public static string SavePath(Guid device, string id) => $"{DeviceRoot(device)}/saves/{id}";
    public static string BucketPath(Guid device, int bucket) => $"v3/objects/{device:N}/{bucket:D4}";
    public static string ObjectPath(Guid device, PackedObject item) => $"{BucketPath(device, item.Bucket)}/{item.Sha256}.{(item.Kind == PackedObjectKind.Zip ? "zip" : "gz")}";
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, Json), Json)!;
    public static bool IsHash(string value) => value is { Length: 64 } && value.All(c => char.IsAsciiHexDigit(c) && !char.IsUpper(c));
    public static bool IsId(string value) => !string.IsNullOrEmpty(value) && value.Length <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    public static IEnumerable<ArchiveRecipe> Recipes(SnapshotManifest snapshot)
    {
        if (snapshot.DataRecipe != null) yield return snapshot.DataRecipe;
        if (snapshot.MediaRecipe != null) yield return snapshot.MediaRecipe;
    }
    public static IEnumerable<FileRecord> ReferencedFiles(PackedState state) => state.Snapshots.SelectMany(Recipes).Concat(state.Saves.Select(save => save.Recipe!)).SelectMany(recipe => recipe.Files);
    public static void ValidateSnapshot(SnapshotManifest value, Guid device)
    {
        if (value == null || value.FormatVersion != 3 || value.DeviceId != device || device == Guid.Empty || !IsId(value.Id) ||
            value.ManifestPath != SnapshotPath(device, value.Id) || value.Games == null || value.SaveManifestPaths == null || value.Notices == null ||
            value.Content == BackupContent.None || (value.Content & ~BackupContent.All) != 0 || value.Data != null || value.Media != null ||
            value.Games.Any(game => game == null || game.Id == Guid.Empty || game.SourceIds == null) || value.Games.Select(game => game.Id).Distinct().Count() != value.Games.Count)
            throw new InvalidDataException("v3快照格式无效。");
        foreach (ArchiveRecipe recipe in Recipes(value)) ValidateRecipe(recipe, device);
        if ((value.Content & ~BackupContent.Saves) != 0 && value.DataRecipe == null) throw new InvalidDataException("v3快照缺少资料清单。");
    }
    public static void ValidateSave(SaveManifest value, Guid device)
    {
        if (value == null || value.FormatVersion != 3 || value.DeviceId != device || device == Guid.Empty || !IsId(value.Id) ||
            value.ManifestPath != SavePath(device, value.Id) || value.Game == null || value.Game.Id == Guid.Empty || value.Game.SourceIds == null ||
            value.Locations == null || value.Locations.Count == 0 || value.Files == null || value.Recipe == null || value.ArchivePath != "" || value.Sha256 != "")
            throw new InvalidDataException("v3存档格式无效。");
        ValidateRecipe(value.Recipe, device);
        if (value.Locations.Any(location => location == null || !IsId(location.Id) || !Enum.IsDefined(location.BaseKind) || location.RelativePath == null) ||
            value.Locations.Select(location => location.Id).Distinct().Count() != value.Locations.Count || value.Files.Count != value.Recipe.Files.Count ||
            BackupFiles.TreeHash(value.Files) != BackupFiles.TreeHash(value.Recipe.Files) || value.ContentHash != BackupFiles.TreeHash(value.Files) ||
            value.Length != value.Files.Sum(file => file.Length) || value.Files.Any(file => !value.Locations.Any(location => file.Path.StartsWith($"roots/{location.Id}/", StringComparison.Ordinal))))
            throw new InvalidDataException("v3存档文件与路径规则不一致。");
    }
    public static void ValidateRecipe(ArchiveRecipe recipe, Guid device)
    {
        IncrementalArchiveStore.Validate(recipe);
        if (recipe.DeviceId != device || recipe.Files.Any(file => !IsHash(file.Sha256))) throw new InvalidDataException("v3不允许跨设备对象引用或无效摘要。");
    }
    public static void Validate(PackedState state, Guid device)
    {
        if (state.FormatVersion != 3 || state.DeviceId != device || device == Guid.Empty || state.Generation < 0 || state.Buckets == null ||
            state.Buckets.Count is 0 or > 512 || state.Buckets.Any(bucket => bucket is < 0 or > 511) || state.Buckets.Distinct().Count() != state.Buckets.Count ||
            state.Snapshots == null || state.Saves == null || state.RetiredObjects == null || state.Files == null || state.Objects == null || state.Snapshots.Count > 10000 || state.Saves.Count > 100000)
            throw new InvalidDataException("v3索引格式或数量无效。");
        foreach (SnapshotManifest snapshot in state.Snapshots) ValidateSnapshot(snapshot, device);
        foreach (SaveManifest save in state.Saves) ValidateSave(save, device);
        if (state.Snapshots.Select(item => item.ManifestPath).Distinct().Count() != state.Snapshots.Count || state.Saves.Select(item => item.ManifestPath).Distinct().Count() != state.Saves.Count)
            throw new InvalidDataException("v3索引含重复版本。");
        var saves = state.Saves.ToDictionary(save => save.ManifestPath, StringComparer.Ordinal);
        foreach (SnapshotManifest snapshot in state.Snapshots)
            if (snapshot.SaveManifestPaths.Distinct().Count() != snapshot.SaveManifestPaths.Count || snapshot.SaveManifestPaths.Any(path =>
                !saves.TryGetValue(path, out var save) || !snapshot.Games.Any(game => game.Id == save.Game.Id)))
                throw new InvalidDataException("v3快照引用的存档版本缺失或不属于该快照游戏。");
        foreach (var pair in state.Objects)
            if (!IsHash(pair.Key) || pair.Value == null || pair.Value.Sha256 != pair.Key || pair.Value.Length <= 0 ||
                !state.Buckets.Contains(pair.Value.Bucket) || !Enum.IsDefined(pair.Value.Kind)) throw new InvalidDataException("v3对象记录无效。");
        if (state.RetiredObjects.Any(item => item == null || !IsHash(item.Sha256) || item.Length <= 0 || !state.Buckets.Contains(item.Bucket) || !Enum.IsDefined(item.Kind)))
            throw new InvalidDataException("v3过期对象记录无效。");
        foreach (var pair in state.Files)
            if (!IsHash(pair.Key) || pair.Value == null || pair.Value.Length < 0 || !state.Objects.ContainsKey(pair.Value.ObjectId)) throw new InvalidDataException("v3文件对象映射无效。");
        foreach (FileRecord file in ReferencedFiles(state))
            if (!state.Files.TryGetValue(file.Sha256, out var location) || location.Length != file.Length) throw new InvalidDataException("v3索引缺少被引用的文件。");
    }
}
