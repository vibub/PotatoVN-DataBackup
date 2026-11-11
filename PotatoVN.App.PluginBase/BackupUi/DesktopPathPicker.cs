using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PotatoVN.App.PluginBase.BackupUi;

// WinRT 旧选择器只能指定系统目录枚举；原生对话框可打开游戏目录且不依赖新版宿主 SDK。
internal static class DesktopPathPicker
{
    private static readonly Guid FolderHistory = new("c94afc91-bd12-4948-9b2e-c570f537e8fb");
    private static readonly Guid FileHistory = new("fb592f7f-d011-4b10-bc7c-d134f44ab31e");

    public static string? Pick(IntPtr owner, bool file, string? preferredDirectory)
    {
        var dialog = (IFileDialog)new FileOpenDialog();
        try
        {
            Guid history = file ? FileHistory : FolderHistory;
            dialog.SetClientGuid(ref history);
            dialog.GetOptions(out uint options);
            // 保留默认选项，限制为实际文件系统，并且不改变进程当前目录。
            dialog.SetOptions(options | 0x8u | 0x40u | 0x800u | (file ? 0x1000u : 0x20u));
            dialog.SetTitle(file ? "选择存档文件" : "选择存档文件夹");
            if (Directory.Exists(preferredDirectory))
            {
                Guid shellItemId = typeof(IShellItem).GUID;
                int result = SHCreateItemFromParsingName(preferredDirectory!, IntPtr.Zero, ref shellItemId, out IShellItem? folder);
                if (result >= 0 && folder != null)
                {
                    try
                    {
                        // SetFolder 优先于历史；目录不可用时不强设起点，沿用系统记住的上次位置。
                        dialog.SetFolder(folder);
                    }
                    finally { Marshal.ReleaseComObject(folder); }
                }
            }
            int shown = dialog.Show(owner);
            if (shown == unchecked((int)0x800704C7)) return null;
            Marshal.ThrowExceptionForHR(shown);
            dialog.GetResult(out IShellItem item);
            try
            {
                item.GetDisplayName(0x80058000, out string path); // SIGDN_FILESYSPATH
                return path;
            }
            finally { Marshal.ReleaseComObject(item); }
        }
        finally { Marshal.ReleaseComObject(dialog); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialog { }

    // COM 方法顺序与 shobjidl_core.h 的 IFileDialog / IShellItem 一致。
    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes(uint count, IntPtr filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);
        [PreserveSig] int SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, uint placement);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid interfaceId, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }
}
