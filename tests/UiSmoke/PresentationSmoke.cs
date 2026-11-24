using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PotatoVN.App.PluginBase.BackupUi;
using PotatoVN.Backup.Core;

namespace UiSmoke;

public sealed partial class SmokeApp
{
    private static async Task CheckSaveLocationMenuAsync(BackupSettingsView view, DemoController controller, Pivot navigation, List<string> results)
    {
        ListView games = Field<ListView>(view, "_saveGame");
        GameIdentity game = controller.GetGames()[0];
        games.SelectedItem = games.Items.OfType<ListViewItem>().Single(row => ((GameIdentity)row.Tag).Id == game.Id);
        ScrollViewer page = Field<ScrollViewer>(view, "_pageScroll");
        page.ChangeView(null, 0, null, true);
        await WaitForUiAsync(() => page.VerticalOffset <= 1, "检查存档跳转前应回到页首。");
        InvokeButton(FindButton(view, "配置存档位置"));
        TextBox search = Field<TextBox>(view, "_saveGameSearch");
        await WaitForUiAsync(() =>
        {
            double listTop = PositionY(games, page);
            return page.VerticalOffset > 0 && search.FocusState != FocusState.Unfocused &&
                listTop >= -1 && listTop + Math.Min(games.ActualHeight, 40) <= page.ActualHeight;
        }, "首屏入口应显示游戏列表，并将焦点放到搜索框。");
        search.Text = game.Name;
        navigation.SelectedIndex = 2;
        await Task.Delay(120);
        navigation.SelectedIndex = 1;
        await Task.Delay(120);
        Check(page.VerticalOffset <= 1 && search.Text == game.Name && games.SelectedItem is ListViewItem { Tag: GameIdentity selected } &&
            selected.Id == game.Id, "切回页签应从顶部开始，同时保留游戏搜索和选择。");
        search.Text = "";
        InvokeButton(FindButton(view, "配置存档位置"));
        await Task.Delay(150);

        var add = (DropDownButton)FindButton(view, "添加存档位置");
        var menu = (MenuFlyout)add.Flyout;
        MenuFlyoutItem[] entries = menu.Items.OfType<MenuFlyoutItem>().ToArray();
        Check(entries.Select(item => item.Text).SequenceEqual(new[] { "选择存档文件夹…", "选择存档文件…", "手动填写位置…" }),
            "添加菜单应保留三种输入方式。");
        ListView locations = Field<ListView>(view, "_saveRoots");
        Button edit = FindButton(view, "编辑所选位置…");
        Button remove = FindButton(view, "移除所选位置");
        Check(!edit.IsEnabled && !remove.IsEnabled, "未选位置时应禁用编辑和移除。");

        add.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        await Task.Delay(120);
        int saves = controller.SaveCalls;
        int originalCount = locations.Items.Count;
        async Task<ContentDialog> OpenManualAsync()
        {
            menu.ShowAt(add);
            await Task.Delay(100);
            var peer = new MenuFlyoutItemAutomationPeer(entries[2]);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            return await WaitDialogAsync(view, "添加存档位置");
        }
        ContentDialog cancel = await OpenManualAsync();
        cancel.Hide();
        await WaitIdleAsync(view);
        Check(controller.SaveCalls == saves && !Field<InfoBar>(view, "_feedback").IsOpen, "取消添加时不应保存或报告成功。");
        ContentDialog manual = await OpenManualAsync();
        FindAll<TextBox>(manual).Single(box => Equals(box.Header, "存档路径")).Text = Path.Combine(game.LocalDirectory!, "save", "slot001.sav");
        FindAll<CheckBox>(manual).Single(check => Equals(check.Content, "备份单个文件")).IsChecked = true;
        InvokePrimary(manual);
        await WaitIdleAsync(view);
        Check(controller.SaveCalls == saves + 1 && locations.Items.Count == originalCount + 1, "确认添加应保存并刷新存档位置。");
        locations.SelectedIndex = originalCount;
        Check(edit.IsEnabled && remove.IsEnabled, "选中存档位置后应启用编辑和移除。");

        InvokeButton(remove);
        InvokePrimary(await WaitDialogAsync(view, "移除存档位置"));
        await WaitIdleAsync(view);
        Check(locations.Items.Count == originalCount && !edit.IsEnabled && !remove.IsEnabled, "确认移除后应刷新列表及操作状态。");
        string largeFolder = Path.Combine(game.LocalDirectory!, "size-check");
        string screenshotFolder = Directory.CreateDirectory(Path.Combine(largeFolder, "Screenshot")).FullName;
        using (var screenshot = File.Create(Path.Combine(screenshotFolder, "example.png")))
            screenshot.SetLength(10 * 1024 * 1024 + 1);

        ContentDialog large = await OpenManualAsync();
        FindAll<TextBox>(large).Single(box => Equals(box.Header, "存档路径")).Text = largeFolder;
        int savesBeforeLarge = controller.SaveCalls;
        InvokePrimary(large);
        ContentDialog warning = await WaitDialogAsync(view, "存档位置超过 10 MB");
        Check(controller.SaveCalls == savesBeforeLarge, "大文件夹必须在用户确认前保持未保存。");
        warning.Hide();
        ContentDialog returned = await WaitDialogAsync(view, "添加存档位置");
        Check(FindAll<TextBox>(returned).Single(box => Equals(box.Header, "存档路径")).Text == largeFolder, "返回修改应保留原路径。");
        InvokePrimary(returned);
        InvokePrimary(await WaitDialogAsync(view, "存档位置超过 10 MB"));
        await WaitIdleAsync(view);
        Check(controller.SaveCalls == savesBeforeLarge + 1, "继续保存应只写入一次。");

        Button checkSizes = FindButton(view, "检测存档大小");
        Button refreshGames = FindButton(view, "刷新游戏列表");
        Check(VisualTreeHelper.GetParent(checkSizes) is ActionRow scopeActions && scopeActions.Children.IndexOf(checkSizes) == scopeActions.Children.IndexOf(refreshGames) + 1,
            "存档大小按钮应位于刷新游戏列表的右侧。");
        InvokeButton(checkSizes);
        ContentDialog report = await WaitDialogAsync(view, "存档大小检测");
        Border[] sizeBadges = FindAll<Border>(report).Where(border => border.Child is TextBlock &&
            !string.IsNullOrEmpty(AutomationProperties.GetName(border))).ToArray();
        Border largeBadge = sizeBadges.Single(badge => AutomationProperties.GetName(badge) == "10 MB");
        Border smallBadge = sizeBadges.Single(badge => AutomationProperties.GetName(badge).EndsWith(" B", StringComparison.Ordinal));
        Check(FindAll<TextBlock>(report).Any(text => text.Text == game.Name) &&
            FindAll<TextBlock>(report).Any(text => Equals(ToolTipService.GetToolTip(text), largeFolder) && text.FontSize == 12) &&
            !FindAll<TextBlock>(report).Any(text => text.Text.Contains("（超过 10 MB）")), "检测结果应分开显示游戏名、大小标签和带工具提示的路径。");
        if (!new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            bool dark = report.ActualTheme == ElementTheme.Dark;
            Check(((SolidColorBrush)largeBadge.Background).Color == (dark ? Windows.UI.Color.FromArgb(255, 73, 54, 21) : Windows.UI.Color.FromArgb(255, 255, 241, 206)) &&
                ((SolidColorBrush)smallBadge.Background).Color == (dark ? Windows.UI.Color.FromArgb(255, 23, 57, 42) : Windows.UI.Color.FromArgb(255, 231, 244, 236)),
                "超过 10 MB 的标签应为黄色，其余大小标签应为绿色。");
        }
        await CaptureAsync(report, "save-size-report.png");
        report.Hide();
        await WaitIdleAsync(view);
        Check(controller.SaveCalls == savesBeforeLarge + 1, "主动检测只读取存档位置。");
        locations.SelectedIndex = originalCount;
        InvokeButton(remove);
        InvokePrimary(await WaitDialogAsync(view, "移除存档位置"));
        await WaitIdleAsync(view);
        page.ChangeView(null, 0, null, true);
        results.Add("PASS save-location-menu: navigation, cancel/add/remove, nested folder size warning, return/confirm, and green/yellow size badges");
    }

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
        GameIdentity longName = controller.GetGames()[1];
        var longRow = games.Items.OfType<ListViewItem>().Single(row => ((GameIdentity)row.Tag).Id == longName.Id);
        TextBlock longTitle = FindAll<TextBlock>((DependencyObject)longRow.Content).Single(text => text.Text == longName.Name);
        Check(longTitle.MaxLines == 2 && longTitle.TextTrimming == TextTrimming.CharacterEllipsis &&
            Equals(ToolTipService.GetToolTip(longTitle), longName.Name), "长游戏标题应限制为两行并保留完整工具提示。");
        ScrollViewer savePage = Field<ScrollViewer>(view, "_pageScroll");
        InvokeButton(FindButton(view, "配置存档位置"));
        await WaitForUiAsync(() => PositionY(games, savePage) >= -1 &&
            PositionY(games, savePage) + 40 <= savePage.ActualHeight, "跳转后应能看到存档游戏列表。");
        await CaptureAsync(host, "save-location-detail-light.png");
        savePage.ChangeView(null, 0, null, true);
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
        PendingRestore entry = controller.GetPending().Single(item => item.Status == RestoreStatus.Ready);
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
                ListViewItem row = pending.Items.OfType<ListViewItem>().Single(item =>
                    ((StackPanel)item.Tag).Children.OfType<CheckBox>().Any(check => ((PendingRestore)check.Tag).Id == entry.Id));
                CheckBox location = ((StackPanel)row.Tag).Children.OfType<CheckBox>().Single(check => ((PendingRestore)check.Tag).Id == entry.Id);
                double rowTop = pending.TransformToVisual(scroll).TransformPoint(new Windows.Foundation.Point()).Y + scroll.VerticalOffset;
                scroll.ChangeView(null, Math.Max(0, rowTop - 50), null, true);
                await Task.Delay(180);
                TextBlock text = FindAll<TextBlock>((DependencyObject)location.Content).Single(block => AutomationProperties.GetName(block) == path);
                await WaitForUiAsync(() => text.ActualWidth > 0, "存档路径尚未排版。");
                await CaptureAsync(host, $"pending-hierarchy-{width}-before-check.png");
                Check(text.FontSize == 12 && text.Text.Contains('…') && text.Text.StartsWith(Path.GetPathRoot(path)!) && text.Text.EndsWith("slot001.sav"),
                    $"长存档路径应使用小字号，省略中段并保留路径头尾：字号={text.FontSize}，宽度={text.ActualWidth}，内容={text.Text}");
                Check((ToolTipService.GetToolTip(text) as string) == path && row.Margin.Bottom >= 8,
                    "完整路径应能在工具提示中查看，条目之间应留出间距。");
                TextBlock gameTitle = FindAll<TextBlock>((DependencyObject)row.Content).Single(block => block.Text == entry.Manifest.Game.Name);
                Border state = FindAll<Border>((DependencyObject)location.Content).First(border => !string.IsNullOrEmpty(AutomationProperties.GetName(border)));
                Check(PositionY(gameTitle, host) < PositionY(state, host), "每个游戏的存档状态应显示在对应版本下面。");
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
