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

    public FrameworkElement CreateSettingUi()
    {
        if (_controller == null) return new TextBlock { Text = "云备份正在初始化，请稍后重新打开。" };
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(4) };
        panel.Children.Add(new TextBlock
        {
            Text = "连接云盘、选择备份内容和恢复存档，都在独立的云备份页面完成。",
            TextWrapping = TextWrapping.Wrap
        });
        var open = new Button { Content = "打开云备份设置" };
        open.Click += (_, _) => HostApi.NavigateTo(typeof(BackupPage), "云备份");
        panel.Children.Add(open);
        panel.Children.Add(new TextBlock
        {
            Text = "也可以从侧边栏“云备份”进入。操作按钮、进度和结果始终显示在页面底部。",
            TextWrapping = TextWrapping.Wrap, Opacity = .8
        });
        return panel;
    }
}
