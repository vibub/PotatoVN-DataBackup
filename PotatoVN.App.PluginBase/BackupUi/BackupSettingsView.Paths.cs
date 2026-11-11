using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;

namespace PotatoVN.App.PluginBase.BackupUi;

public sealed partial class BackupSettingsView
{
    private Task<string?> PickPathAsync(bool file = false, string? preferredDirectory = null)
    {
        // 使用宿主实际 Window，兼容没有 ContentIslandEnvironment 的旧宿主。
        IntPtr handle = _getWindowHandle?.Invoke() ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("未能取得主窗口，请重新打开云备份页面后再选择路径。");
        return Task.FromResult(DesktopPathPicker.Pick(handle, file, preferredDirectory));
    }

    private Button BrowsePathButton(TextBox target, bool file = false, Action? onPicked = null, string? preferredDirectory = null)
    {
        var button = new Button { Content = file ? "浏览文件…" : "浏览文件夹…" };
        button.Click += async (_, _) =>
        {
            try
            {
                string? path = await PickPathAsync(file, preferredDirectory);
                if (path != null) { target.Text = path; onPicked?.Invoke(); }
            }
            catch (Exception error) { ShowMessage(error.GetBaseException().Message, InfoBarSeverity.Error); }
        };
        return button;
    }
}
