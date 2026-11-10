using System.IO.Compression;

namespace PotatoVN.Backup.Core.Engine;

public static class SaveArchiveService
{
    private static IEnumerable<(SaveBaseKind Kind, string Root)> UserRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return (SaveBaseKind.AppData, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        yield return (SaveBaseKind.LocalAppData, local);
        yield return (SaveBaseKind.LocalLow, Path.Combine(Path.GetDirectoryName(local)!, "LocalLow"));
        yield return (SaveBaseKind.Documents, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
    }

    public static SaveLocation Describe(string path, string? gameRoot, bool isFile)
    {
        path = Path.GetFullPath(path);
        var bases = UserRoots().ToList();
        if (!string.IsNullOrWhiteSpace(gameRoot)) bases.Insert(0, (SaveBaseKind.GameRoot, Path.GetFullPath(gameRoot)));
        foreach (var (kind, root) in bases)
        {
            if (string.IsNullOrEmpty(root)) continue;
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return new SaveLocation { BaseKind = kind, RelativePath = Path.GetRelativePath(root, path).Replace('\\', '/'), IsFile = isFile };
        }
        return new SaveLocation { BaseKind = SaveBaseKind.Custom, RelativePath = "", CustomPath = path, IsFile = isFile };
    }

    public static string? Resolve(SaveLocation location, GameIdentity? game, string? targetOverride = null)
    {
        // 无论存档在哪个系统目录，都需要先确认本机游戏关联有效。
        if (game?.LocalDirectory is not { } root || !Directory.Exists(root)) return null;
        if (!string.IsNullOrWhiteSpace(targetOverride)) return ValidateTarget(targetOverride, root);
        if (location.BaseKind == SaveBaseKind.Custom)
            return location.CustomPath is { Length: > 0 } custom ? ValidateTarget(custom, root) : null;
        var basePath = location.BaseKind == SaveBaseKind.GameRoot ? root :
            UserRoots().First(p => p.Kind == location.BaseKind).Root;
        if (string.IsNullOrEmpty(basePath)) return null;
        return BackupFiles.Within(basePath, location.RelativePath);
    }

    private static string ValidateTarget(string path, string? gameRoot = null)
    {
        var full = Path.GetFullPath(path);
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("不能将磁盘根目录作为存档目标。");
        var protectedRoots = UserRoots().Select(p => p.Root).Concat(new[]
        {
            gameRoot ?? "", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        });
        if (protectedRoots.Where(p => !string.IsNullOrWhiteSpace(p)).Any(p => string.Equals(
                Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar), full.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("请指定游戏专用的存档文件或子目录，不能覆盖整个游戏根目录或用户基础目录。");
        return full;
    }

    public static async Task<SaveManifest?> CreateAsync(GameIdentity game, IReadOnlyList<SaveLocation> locations,
        string archivePath, Guid deviceId, CancellationToken ct = default)
    {
        if (game.IsRunning) throw new InvalidOperationException("游戏仍在运行，存档备份已延后：" + game.Name);
        if (locations.Count == 0) return null;
        var roots = new List<(SaveLocation Location, string Path)>();
        foreach (var location in locations)
        {
            if (location.Id.Length == 0 || location.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
                throw new InvalidDataException("存档位置标识无效。");
            // 备份时允许读取非本地游戏遗留的系统目录；恢复时 Resolve 仍要求关联。
            string? path;
            if (location.BaseKind == SaveBaseKind.Custom) path = location.CustomPath;
            else if (location.BaseKind == SaveBaseKind.GameRoot)
                path = game.LocalDirectory is { } root && Directory.Exists(root)
                    ? BackupFiles.Within(root, location.RelativePath) : null;
            else
            {
                var basePath = UserRoots().First(p => p.Kind == location.BaseKind).Root;
                path = string.IsNullOrEmpty(basePath) ? null : BackupFiles.Within(basePath, location.RelativePath);
            }
            if (path is null || (location.IsFile ? !File.Exists(path) : !Directory.Exists(path))) return null;
            roots.Add((location, path));
        }
        var files = new List<(string Source, string Entry)>();
        foreach (var (location, path) in roots)
        {
            if (location.IsFile) files.Add((path, $"roots/{location.Id}/{Path.GetFileName(path)}"));
            else foreach (var file in EnumerateFiles(path))
                files.Add((file, $"roots/{location.Id}/{Path.GetRelativePath(path, file).Replace('\\', '/')}"));
        }
        // 空目录或任何一个位置缺失，不创建空版本替代已有云端存档。
        if (files.Count == 0 || files.Sum(f => new FileInfo(f.Source).Length) == 0 || roots.Any(r => !files.Any(f => f.Entry.StartsWith($"roots/{r.Location.Id}/", StringComparison.Ordinal)))) return null;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(archivePath))!);
        var records = new List<FileRecord>();
        using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            foreach (var (source, entryName) in files.OrderBy(f => f.Entry, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var before = new FileInfo(source);
                var length = before.Length;
                var written = before.LastWriteTimeUtc;
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                await using (var output = entry.Open()) await input.CopyToAsync(output, ct);
                var after = new FileInfo(source);
                if (after.Length != length || after.LastWriteTimeUtc != written)
                    throw new IOException("备份过程中存档发生变化，请退出游戏后重试：" + source);
                // 对实际写入的内容计算校验值，不再次读取可能变化的源文件。
                records.Add(new FileRecord { Path = entryName, Length = length });
            }
        }
        using (var zip = ZipFile.OpenRead(archivePath))
        {
            foreach (var record in records)
            {
                await using var input = zip.GetEntry(record.Path)!.Open();
                record.Sha256 = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input, ct)).ToLowerInvariant();
            }
        }
        return new SaveManifest
        {
            Id = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "_" + Guid.NewGuid().ToString("N"),
            DeviceId = deviceId, Game = game, CapturedUtc = DateTimeOffset.UtcNow,
            Sha256 = await BackupFiles.HashAsync(archivePath, ct), Length = new FileInfo(archivePath).Length,
            ContentHash = BackupFiles.TreeHash(records), Locations = locations.ToList(), Files = records
        };
    }

    public static async Task<bool> RestoreAsync(PendingRestore pending, string archivePath, GameIdentity game,
        string rollbackRoot, CancellationToken ct = default)
    {
        if (game.IsRunning) throw new InvalidOperationException("请先退出游戏再恢复存档。");
        var location = pending.Manifest.Locations.Single(l => l.Id == pending.LocationId);
        var target = Resolve(location, game, pending.TargetOverride) ?? throw new InvalidOperationException("请先关联本地游戏并指定存档位置。");
        BackupFiles.RejectReparseAncestors(target);
        if (!string.Equals(await BackupFiles.HashAsync(archivePath, ct), pending.Manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("本地暂存包校验失败，请重新下载。");
        var prefix = $"roots/{location.Id}/";
        var expected = pending.Manifest.Files.Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        if (expected.Count == 0 || (location.IsFile && expected.Count != 1)) throw new InvalidDataException("存档位置内容无效。");
        var current = await CurrentRecordsAsync(location, target, prefix, expected, ct);
        if (BackupFiles.TreeHash(current) == BackupFiles.TreeHash(expected))
        {
            pending.Status = RestoreStatus.Restored;
            pending.TargetPath = target;
            pending.Error = null;
            return false;
        }
        var token = Guid.NewGuid().ToString("N");
        var workspace = Path.Combine(rollbackRoot, token);
        Directory.CreateDirectory(workspace);
        var extracted = Path.Combine(workspace, "extracted");
        await BackupFiles.ExtractAsync(archivePath, extracted, ct);
        foreach (var record in pending.Manifest.Files)
        {
            var file = BackupFiles.Within(extracted, record.Path);
            if (!File.Exists(file) || new FileInfo(file).Length != record.Length ||
                !string.Equals(await BackupFiles.HashAsync(file, ct), record.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("存档包中的文件校验失败：" + record.Path);
        }
        var actualCount = Directory.EnumerateFiles(extracted, "*", SearchOption.AllDirectories).Count();
        if (actualCount != pending.Manifest.Files.Count) throw new InvalidDataException("存档包含有未登记的文件。");
        var incoming = target + ".pvn-incoming-" + token;
        var old = target + ".pvn-old-" + token;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var existed = File.Exists(target) || Directory.Exists(target);
        var oldIsFile = File.Exists(target);
        var originalMoved = false;
        try
        {
            if (existed)
            {
                var backup = Path.Combine(workspace, "original");
                if (oldIsFile) File.Copy(target, backup); else CopyDirectory(target, backup);
                await BackupFiles.WriteJsonAsync(Path.Combine(workspace, "rollback.json"), new { Target = target, IsFile = oldIsFile, Backup = backup }, ct);
            }
            if (location.IsFile) File.Copy(BackupFiles.Within(extracted, expected[0].Path), incoming);
            else CopyDirectory(BackupFiles.Within(extracted, $"roots/{location.Id}"), incoming);
            ct.ThrowIfCancellationRequested();
            if (existed)
            {
                if (oldIsFile) File.Move(target, old); else Directory.Move(target, old);
                originalMoved = true;
            }
            if (location.IsFile) File.Move(incoming, target); else Directory.Move(incoming, target);
            if (originalMoved) { if (oldIsFile) File.Delete(old); else Directory.Delete(old, true); }
            pending.TargetPath = target;
            pending.Status = RestoreStatus.Restored;
            pending.Error = null;
            return true;
        }
        catch
        {
            if (originalMoved)
            {
                if (File.Exists(target)) File.Delete(target);
                if (Directory.Exists(target)) Directory.Delete(target, true);
                if (oldIsFile) File.Copy(Path.Combine(workspace, "original"), target); else CopyDirectory(Path.Combine(workspace, "original"), target);
            }
            throw;
        }
        finally
        {
            if (File.Exists(incoming)) File.Delete(incoming);
            if (Directory.Exists(incoming)) Directory.Delete(incoming, true);
            // 原始文件副本保留在 rollbackRoot；解压数据不进入待恢复区的统计。
            if (Directory.Exists(extracted)) Directory.Delete(extracted, true);
        }
    }

    private static async Task<List<FileRecord>> CurrentRecordsAsync(SaveLocation location, string target,
        string prefix, List<FileRecord> expected, CancellationToken ct)
    {
        var records = new List<FileRecord>();
        if (location.IsFile)
        {
            if (File.Exists(target)) records.Add(new FileRecord { Path = expected[0].Path,
                Length = new FileInfo(target).Length, Sha256 = await BackupFiles.HashAsync(target, ct) });
        }
        else if (Directory.Exists(target))
            foreach (var file in EnumerateFiles(target)) records.Add(new FileRecord {
                Path = prefix + Path.GetRelativePath(target, file).Replace('\\', '/'),
                Length = new FileInfo(file).Length, Sha256 = await BackupFiles.HashAsync(file, ct) });
        return records;
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        foreach (var item in new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("存档目录包含符号链接，请将实际存档目录单独添加：" + item.FullName);
            if (item is DirectoryInfo directory)
                foreach (var file in EnumerateFiles(directory.FullName)) yield return file;
            else yield return item.FullName;
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in EnumerateFiles(source))
        {
            var destination = BackupFiles.Within(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }
}
