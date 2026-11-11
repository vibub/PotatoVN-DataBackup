using PotatoVN.Backup.Core.Engine;

string root = Path.Combine(Path.GetTempPath(), "PotatoVNLocalWorkCleanupChecks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    DateTimeOffset now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    Check(LocalWorkCleanup.RemoveExpired(root, now) == 0, "缺少work时不应创建或删除目录");
    string old = Fixture("work/" + Guid.NewGuid().ToString("N"), now.AddHours(-25));
    string recent = Fixture("work/" + Guid.NewGuid().ToString("N"), now.AddHours(-1));
    string boundary = Fixture("work/" + Guid.NewGuid().ToString("N"), now.AddHours(-24));
    string unknown = Fixture("work/user-folder", now.AddDays(-20));
    string pending = Fixture("pending/archives", now.AddDays(-20));
    string rollback = Fixture("rollback/history", now.AddDays(-20));
    string import = Fixture("import/prepared", now.AddDays(-20));
    string secrets = Fixture("secrets", now.AddDays(-20));
    File.WriteAllText(Path.Combine(root, "settings.json"), "synthetic settings");
    Check(LocalWorkCleanup.RemoveExpired(root, now) == 1 && !Directory.Exists(old), "未回收过期合成工作目录");
    Check(Directory.Exists(recent) && Directory.Exists(boundary), "误删近期或24小时边界的工作目录");
    Console.WriteLine("PASS expired work removed; recent and boundary work retained");
    Check(new[] { unknown, pending, rollback, import, secrets }.All(Directory.Exists), "越界删除了用户目录或持久数据");
    Check(File.ReadAllText(Path.Combine(root, "settings.json")) == "synthetic settings", "误改配置");
    Check(LocalWorkCleanup.RemoveExpired(root, now) == 0, "重复清理未保持幂等");
    Console.WriteLine("PASS only generated work is eligible; persistent data and unknown folders retained");
    string locked = Fixture("work/" + Guid.NewGuid().ToString("N"), now.AddDays(-2));
    using (var held = new FileStream(Path.Combine(locked, "synthetic.bin"), FileMode.Open, FileAccess.Read, FileShare.None))
    {
        bool failed = false;
        try { LocalWorkCleanup.RemoveExpired(root, now); }
        catch (IOException) { failed = true; }
        Check(failed && File.Exists(Path.Combine(locked, "synthetic.bin")), "锁定文件的清理错误没有保留");
    }
    Check(LocalWorkCleanup.RemoveExpired(root, now) == 1, "锁释放后不能重试回收");
    Console.WriteLine("PASS locked file failure is reported and cleanup can retry");
    Console.WriteLine("PASS 3 local cleanup checks; synthetic directories only");
    return 0;

    string Fixture(string relative, DateTimeOffset modified)
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "synthetic.bin"), "synthetic data");
        Directory.SetLastWriteTimeUtc(path, modified.UtcDateTime);
        return path;
    }
}
finally
{
    string expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PotatoVNLocalWorkCleanupChecks")) + Path.DirectorySeparatorChar;
    if (!Path.GetFullPath(root).StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("合成目录越界");
    Directory.Delete(root, true);
}
static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
