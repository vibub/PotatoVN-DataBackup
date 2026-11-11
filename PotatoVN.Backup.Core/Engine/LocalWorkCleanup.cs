namespace PotatoVN.Backup.Core.Engine;

/// <summary>只回收插件生成的过期工作目录，不处理配置、凭据或待恢复数据。</summary>
public static class LocalWorkCleanup
{
    public static int RemoveExpired(string localRoot, DateTimeOffset now)
    {
        var root = new DirectoryInfo(Path.GetFullPath(Path.Combine(localRoot, "work")));
        if (!root.Exists || root.Attributes.HasFlag(FileAttributes.ReparsePoint)) return 0;
        string prefix = root.FullName + Path.DirectorySeparatorChar;
        DateTime cutoff = now.UtcDateTime.AddHours(-24);
        int removed = 0;
        foreach (DirectoryInfo directory in root.EnumerateDirectories())
        {
            // 仅认可 Work() 创建的32位随机目录，跳过链接、近期目录和其他文件。
            if (!Guid.TryParseExact(directory.Name, "N", out _) || directory.LastWriteTimeUtc >= cutoff ||
                directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            string target = Path.GetFullPath(directory.FullName);
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new IOException("本机临时文件目录越界，已停止清理。");
            Directory.Delete(target, true);
            removed++;
        }
        return removed;
    }
}
