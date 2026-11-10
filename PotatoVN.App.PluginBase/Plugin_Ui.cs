using System.Threading.Tasks;
using GalgameManager.WinApp.Base.Models.Plugin;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.App.PluginBase.BackupUi;

namespace PotatoVN.App.PluginBase;

public partial class Plugin
{
    private void InitUi()
    {
        HostApi.RegisterSidebarButton(new SidebarButtonInfo
        {
            Id = "cloud-backup", Text = "云备份", Placement = SidebarButtonPlacement.Menu,
            FluentGlyph = "\uE753", FallbackGlyph = "\uE753"
        }, () =>
        {
            HostApi.NavigateTo(typeof(BackupPage), "云备份");
            return Task.CompletedTask;
        });
    }

    public FrameworkElement CreateSettingUi() => _controller == null
        ? new TextBlock { Text = "云备份正在初始化。" }
        : new BackupSettingsView(_controller);
}
