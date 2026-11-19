using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PotatoVN.App.PluginBase.Backup;

internal sealed class PluginStorage(string pluginsRoot, Guid pluginId, string legacyRoot)
{
    private static readonly string[] StateEntries = ["settings.json", "settings-restore.json", "secrets", "pending", "rollback", "import", "cache"];

    public string Root { get; } = Path.Combine(pluginsRoot, pluginId.ToString("D") + ".data");
    public string LegacyRoot => legacyRoot;
    public string RestartRoot => Path.Combine(legacyRoot, "restart");
    public string ExternalTempRoot => Path.Combine(legacyRoot, "host-temp");
    private string MigrationRoot => Root + ".migrating";

    public static string FindLegacyRoot(string dataRoot, string localAppData, string? localCache)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataRoot).ToUpperInvariant())))[..16];
        if (localCache != null)
        {
            string redirected = Path.Combine(localCache, "Local", "PotatoVN-DataBackup", key);
            if (Directory.Exists(redirected)) return redirected;
        }
        return Path.Combine(localAppData, "PotatoVN-DataBackup", key);
    }

    public void Initialize(string dataRoot, string temporaryRoot)
    {
        if (Directory.Exists(Root)) return;

        // 原生导入先把 LocalState 移到 ImportBackup，再展开 ZIP。插件状态由此接回，避免把它塞入 ZIP。
        string relative = Path.GetRelativePath(dataRoot, Root);
        string source = LegacyRoot;
        if (!Path.IsPathFullyQualified(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            foreach (string temp in new[] { temporaryRoot, ExternalTempRoot })
            {
                string backup = Path.Combine(temp, "ImportBackup", relative);
                if (!StateEntries.Select(name => Path.Combine(backup, name)).Any(path => File.Exists(path) || Directory.Exists(path)))
                    continue;
                source = backup;
                break;
            }
        }
        if (!Directory.Exists(source) && !Directory.Exists(MigrationRoot))
        {
            Directory.CreateDirectory(Root);
            return;
        }

        Directory.CreateDirectory(MigrationRoot);
        bool sameVolume = string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(Root), StringComparison.OrdinalIgnoreCase);
        var copied = new List<string>();
        foreach (string name in StateEntries)
        {
            string from = Path.Combine(source, name);
            string to = Path.Combine(MigrationRoot, name);
            if (Directory.Exists(from))
            {
                if (sameVolume) Directory.Move(from, to);
                else { CopyDirectory(from, to); copied.Add(from); }
            }
            else if (File.Exists(from))
            {
                if (sameVolume) File.Move(from, to);
                else { File.Copy(from, to, overwrite: true); copied.Add(from); }
            }
        }

        Directory.Move(MigrationRoot, Root);
        foreach (string from in copied)
        {
            if (Directory.Exists(from)) Directory.Delete(from, recursive: true);
            else File.Delete(from);
        }
    }

    public IEnumerable<string> FilesForImport()
    {
        // 在进入子目录前排除当前插件的数据，避免递归打包 work、凭据或正在生成的导入包。
        foreach (string entry in Directory.EnumerateFileSystemEntries(pluginsRoot))
        {
            if (string.Equals(entry, Root, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry, MigrationRoot, StringComparison.OrdinalIgnoreCase)) continue;
            if (File.GetAttributes(entry).HasFlag(FileAttributes.Directory))
            {
                foreach (string file in Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories))
                    yield return file;
            }
            else yield return entry;
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        if (File.GetAttributes(from).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("插件数据目录不能通过重解析点跨盘迁移：" + from);
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        foreach (string child in Directory.EnumerateDirectories(from))
            CopyDirectory(child, Path.Combine(to, Path.GetFileName(child)));
    }
}
