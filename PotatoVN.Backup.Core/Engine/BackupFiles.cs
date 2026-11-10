using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PotatoVN.Backup.Core.Engine;

public static class BackupFiles
{
    public static async Task WriteJsonAsync<T>(string path, T value, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, value, BackupJson.Options, ct);
            AtomicFiles.Commit(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static async Task<T> ReadJsonAsync<T>(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return await JsonSerializer.DeserializeAsync<T>(stream, BackupJson.Options, ct)
            ?? throw new InvalidDataException("JSON 内容为空：" + Path.GetFileName(path));
    }
    public static async Task<string> HashAsync(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    public static string Within(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) throw new InvalidDataException("备份中的相对路径为空。");
        relative = relative.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith('/') || relative.Contains(':') ||
            relative.Split('/').Any(p => p is ".." or "." || p.EndsWith(' ') || p.EndsWith('.')))
            throw new InvalidDataException("备份中含有不安全的相对路径：" + relative);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(root, relative));
        if (!target.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("路径超出了目标目录。");
        return target;
    }

    public static void RejectReparseAncestors(string path)
    {
        for (var cursor = Path.GetFullPath(path); !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
            if ((Directory.Exists(cursor) || File.Exists(cursor)) &&
                (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("目标路径经过符号链接，请在恢复预览中指定实际目标：" + cursor);
    }

    public static async Task ExtractAsync(string archive, string destination, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destination);
        using var zip = ZipFile.OpenRead(archive);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var target = Within(destination, name.TrimEnd('/'));
            if (!seen.Add(target)) throw new InvalidDataException("压缩包中有重复路径：" + name);
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000) throw new InvalidDataException("压缩包不允许符号链接。");
            RejectReparseAncestors(target);
            if (name.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, ct);
        }
    }

    public static async Task ZipAsync(string source, string archive, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(archive))!);
        using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(source, "*", new EnumerationOptions
                 { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var entry = zip.CreateEntry(Path.GetRelativePath(source, file).Replace('\\', '/'), CompressionLevel.Optimal);
            await using var input = File.OpenRead(file);
            await using var output = entry.Open();
            await input.CopyToAsync(output, ct);
        }
    }

    public static string TreeHash(IEnumerable<FileRecord> files) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\n', files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => $"{f.Path}\t{f.Length}\t{f.Sha256}")))))
        .ToLowerInvariant();
}

public sealed class SettingsStore(string localRoot)
{
    public string Root { get; } = Path.GetFullPath(localRoot);
    public Task<BackupSettings> LoadAsync(CancellationToken ct = default) => File.Exists(Path.Combine(Root, "settings.json"))
        ? LoadExistingAsync(ct) : Task.FromResult(new BackupSettings());
    private async Task<BackupSettings> LoadExistingAsync(CancellationToken ct)
    {
        var settings = await BackupFiles.ReadJsonAsync<BackupSettings>(Path.Combine(Root, "settings.json"), ct);
        if (settings.FormatVersion != 1) throw new InvalidDataException("不支持的插件配置版本，请先升级插件。");
        if (!double.IsFinite(settings.IntervalHours) || settings.IntervalHours < 0.05 || settings.IntervalHours > 8760 ||
            settings.ExitDelaySeconds is < 0 or > 3600 || settings.KeepSnapshots < 1 || settings.KeepSaves < 1 ||
            (settings.Content & ~BackupContent.All) != 0 || settings.Profiles is null || settings.SelectedGameIds is null ||
            settings.SaveConfigs is null || settings.GameMappings is null || settings.LibraryMappings is null || settings.CustomSavePaths is null ||
            settings.Profiles.Any(p => p is null || p.Id == Guid.Empty || !Enum.IsDefined(p.Kind)) ||
            settings.SaveConfigs.Any(c => c is null || c.Locations is null || c.Locations.Any(l => l is null || !Enum.IsDefined(l.BaseKind))))
            throw new InvalidDataException("插件配置中的间隔、保留数量或数据列表无效，请修正 settings.json 后重试。");
        return settings;
    }
    public Task SaveAsync(BackupSettings settings, CancellationToken ct = default) =>
        BackupFiles.WriteJsonAsync(Path.Combine(Root, "settings.json"), settings, ct);
}
