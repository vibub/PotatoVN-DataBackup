using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PotatoVN.App.PluginBase.BackupUi;
using PotatoVN.Backup.Core;

namespace UiSmoke;

public sealed partial class SmokeApp
{
    private async Task CheckPresentationAsync(BackupSettingsView view, DemoController controller, Grid host, Pivot navigation, List<string> results)
    {
        navigation.SelectedIndex = 1;
        var cover = Field<Border>(view, "_saveCover");
        InvokeButton(FindButton(view, "重新检查存档位置"));
        await WaitIdleAsync(view);
        await Task.Delay(250);
        Check(cover.Child is Image { Source: BitmapImage bitmap } && bitmap.PixelWidth > 0,
            "详情头部应读取本地样本封面。");
        ListView games = Field<ListView>(view, "_saveGame");
        StackPanel details = Field<StackPanel>(view, "_saveGameDetails");
        Check(Math.Abs(PositionY(details, host) - PositionY(games, host)) < 1,
            "详情封面上边界应与左侧游戏列表对齐。");
        Check(!FindAll<TextBlock>(games).Any(text => text.Text.Contains(controller.GetGames()[0].LocalDirectory!)),
            "游戏列表不应显示安装路径。");
        string broken = Path.Combine(Program.Output, "invalid-cover.jpg");
        File.WriteAllText(broken, "Synthetic corrupt cover.");
        controller.CoverOverride = broken;
        InvokeButton(FindButton(view, "重新检查存档位置"));
        await WaitIdleAsync(view);
        await Task.Delay(350);
        Check(cover.Child is not Image && FindAll<TextBlock>(cover).Any(text => text.Text == "暂无封面"),
            "损坏封面应显示简洁占位，不能留下旧游戏图片。");
        controller.CoverOverride = null;
        InvokeButton(FindButton(view, "重新检查存档位置"));
        await WaitIdleAsync(view);
        results.Add("PASS cover-and-alignment: local cover loaded; missing/corrupt cover fallback; cover top aligns with game list; installation paths hidden");

        navigation.SelectedIndex = 4;
        await WaitForUiAsync(() => FindAll<Button>(view).Any(button => button.Content as string == "刷新状态"),
            $"待恢复页面未加载，当前页签 {navigation.SelectedIndex}。");
        PendingRestore entry = controller.GetPending()[0];
        string? original = entry.TargetPath;
        string path = Path.Combine(Program.Output, "很长的合成目录-" + new string('长', 80), "savedata", "slot001.sav");
        try
        {
            entry.TargetPath = path;
            InvokeButton(FindButton(view, "刷新状态"));
            await WaitIdleAsync(view);
            ScrollViewer scroll = Field<ScrollViewer>(view, "_pageScroll");
            foreach (int width in new[] { 1080, 620 })
            {
                double scale = host.XamlRoot.RasterizationScale;
                _window!.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(width * scale), (int)(760 * scale)));
                await Task.Delay(180);
                ListView pending = Field<ListView>(view, "_pendingList");
                ListViewItem row = pending.Items.OfType<ListViewItem>().Single(item => ((PendingRestore)item.Tag).Id == entry.Id);
                double rowTop = pending.TransformToVisual(scroll).TransformPoint(new Windows.Foundation.Point()).Y + scroll.VerticalOffset;
                scroll.ChangeView(null, Math.Max(0, rowTop - 50), null, true);
                await Task.Delay(180);
                TextBlock text = FindAll<TextBlock>((DependencyObject)row.Content).Single(block => AutomationProperties.GetName(block) == path);
                await WaitForUiAsync(() => text.ActualWidth > 0, "存档路径尚未排版。");
                await CaptureAsync(host, $"pending-hierarchy-{width}-before-check.png");
                Check(text.FontSize == 12 && text.Text.Contains('…') && text.Text.StartsWith(Path.GetPathRoot(path)!) && text.Text.EndsWith("slot001.sav"),
                    $"长存档路径应使用小字号，省略中段并保留路径头尾：字号={text.FontSize}，宽度={text.ActualWidth}，内容={text.Text}");
                Check((ToolTipService.GetToolTip(text) as string) == path && row.Margin.Bottom >= 8,
                    "完整路径应能在工具提示中查看，条目之间应留出间距。");
                TextBlock gameTitle = FindAll<TextBlock>((DependencyObject)row.Content).Single(block => block.Text == entry.Manifest.Game.Name);
                Border state = FindAll<Border>((DependencyObject)row.Content).First(border => !string.IsNullOrEmpty(AutomationProperties.GetName(border)));
                Check(Math.Abs(PositionY(gameTitle, host) - PositionY(state, host)) < 3,
                    "状态标签应与简短游戏名称同行。");
                double top = pending.TransformToVisual(scroll).TransformPoint(new Windows.Foundation.Point()).Y + scroll.VerticalOffset;
                scroll.ChangeView(null, Math.Max(0, top - 50), null, true);
                await Task.Delay(100);
                CheckStatusContrast(pending);
                await CaptureAsync(host, $"pending-hierarchy-{width}-light.png");
                host.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32));
                host.RequestedTheme = ElementTheme.Dark;
                await Task.Delay(150);
                CheckStatusContrast(pending);
                await CaptureAsync(host, $"pending-hierarchy-{width}-dark.png");
                host.RequestedTheme = ElementTheme.Light;
                host.Background = new SolidColorBrush(Microsoft.UI.Colors.White);
            }
            results.Add("PASS pending-hierarchy: separate title/status/metadata; semantic badge fills; row spacing; 12px middle-ellipsized paths retain root and filename plus full tooltip; light/dark wide/narrow screenshots");
        }
        finally
        {
            entry.TargetPath = original;
            typeof(BackupSettingsView).GetMethod("RefreshPendingView", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(view, null);
            double scale = host.XamlRoot.RasterizationScale;
            _window!.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1080 * scale), (int)(760 * scale)));
            await Task.Delay(120);
        }
        navigation.SelectedIndex = 1;
    }
    private static void CheckStatusContrast(ListView list)
    {
        static double Luminance(Windows.UI.Color color)
        {
            static double Linear(byte channel)
            {
                double value = channel / 255d;
                return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
            }
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }
        foreach (ListViewItem row in list.Items)
            foreach (Border badge in FindAll<Border>((DependencyObject)row.Content).Where(border => !string.IsNullOrEmpty(AutomationProperties.GetName(border))))
            {
                var label = (TextBlock)badge.Child;
                double foreground = Luminance(((SolidColorBrush)label.Foreground).Color);
                double background = Luminance(((SolidColorBrush)badge.Background).Color);
                Check((Math.Max(foreground, background) + .05) / (Math.Min(foreground, background) + .05) >= 4.5,
                    "状态标签文字与背景对比度应至少达到4.5。");
            }
    }

}
