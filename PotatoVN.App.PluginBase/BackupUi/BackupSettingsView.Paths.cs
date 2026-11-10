using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace PotatoVN.App.PluginBase.BackupUi;

public sealed partial class BackupSettingsView
{
    private async Task<string?> PickPathAsync(bool file = false)
    {
        // Use the host's actual Window. XamlRoot.ContentIslandEnvironment is absent in older hosts.
        IntPtr handle = _getWindowHandle?.Invoke() ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("未能取得主窗口，请重新打开云备份页面后再选择路径。");
        if (file)
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
            return (await picker.PickSingleFileAsync())?.Path;
        }
        var folders = new FolderPicker();
        folders.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(folders, handle);
        return (await folders.PickSingleFolderAsync())?.Path;
    }

    private Button BrowsePathButton(TextBox target, bool file = false, Action? onPicked = null)
    {
        var button = new Button { Content = file ? "浏览文件…" : "浏览文件夹…" };
        button.Click += async (_, _) =>
        {
            try
            {
                string? path = await PickPathAsync(file);
                if (path != null) { target.Text = path; onPicked?.Invoke(); }
            }
            catch (Exception error) { ShowMessage(error.GetBaseException().Message, InfoBarSeverity.Error); }
        };
        return button;
    }
}
