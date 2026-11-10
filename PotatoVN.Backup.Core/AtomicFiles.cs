using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PotatoVN.Backup.Core;

public static class AtomicFiles
{
    public static void Commit(string temporary, string destination)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
            return;
        }
        // 本机复现 Move 覆盖返回 5、Replace 返回 1175。Windows 10 的 POSIX 重命名
        // 允许读者继续访问旧版本，同时让新读者看到完整新文件，不采用延迟重试。
        using SafeFileHandle source = CreateFileW(NativePath(temporary), 0x00010000,
            1 | 2 | 4, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (source.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法提交本机临时文件。");
        byte[] name = Encoding.Unicode.GetBytes(NativePath(destination));
        int rootOffset = IntPtr.Size == 8 ? 8 : 4;
        int lengthOffset = rootOffset + IntPtr.Size;
        int nameOffset = lengthOffset + 4;
        byte[] information = new byte[nameOffset + name.Length];
        BitConverter.GetBytes(3u).CopyTo(information, 0); // REPLACE_IF_EXISTS | POSIX_SEMANTICS
        BitConverter.GetBytes((uint)name.Length).CopyTo(information, lengthOffset);
        name.CopyTo(information, nameOffset);
        if (!SetFileInformationByHandle(source, 22, information, (uint)information.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法原子保存本机文件，原数据仍会保留。");
    }

    private static string NativePath(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass,
        byte[] information, uint length);
}
