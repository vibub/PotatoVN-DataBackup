using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PotatoVN.App.PluginBase.BackupUi;

public sealed class BackupPage : Page
{
    public static IBackupUiController? Controller { get; set; }
    public static Func<Window?>? HostWindowProvider { get; set; }

    private static IntPtr GetWindowHandle()
    {
        Window? window = HostWindowProvider?.Invoke();
        return window == null ? IntPtr.Zero : WinRT.Interop.WindowNative.GetWindowHandle(window);
    }

    public BackupPage()
    {
        Content = Controller is { } controller
            ? new BackupSettingsView(controller, GetWindowHandle)
            : new TextBlock { Text = "云备份正在初始化，请稍后重新打开。", Margin = new Microsoft.UI.Xaml.Thickness(24) };
    }

    public BackupPage(IBackupUiController controller)
    {
        Content = new BackupSettingsView(controller, GetWindowHandle);
    }
}
