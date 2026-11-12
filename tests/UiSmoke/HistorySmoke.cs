using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.App.PluginBase.BackupUi;
using PotatoVN.Backup.Core;

namespace UiSmoke;

public sealed partial class SmokeApp
{
    private async Task CheckHistoryUiAsync(BackupSettingsView view, DemoController controller, FrameworkElement host, Pivot navigation, List<string> results)
    {
        var originalMappings = controller.Settings.GameMappings.ToList();
        var cloud = Enumerable.Range(1, 78).Select(i => new GameIdentity { Id = Guid.NewGuid(), Name = $"云端游戏{i:D2}（合成资料）", SourceIds = new() { ["vndb"] = $"v{i}" } }).ToList();
        var snapshot = new SnapshotManifest { Id = "history-ui", Content = BackupContent.GameInfo | BackupContent.Saves,
            DeviceName = "合成电脑", CreatedUtc = DateTimeOffset.UtcNow, Games = cloud,
            SaveManifestPaths = cloud.Take(6).Select(game => "synthetic/" + game.Id).ToList(),
            SaveGames = cloud.Take(6).ToDictionary(game => game.Id, _ => DateTimeOffset.UtcNow),
            Notices = cloud.Skip(6).Select(game => game.Name + "：尚无可备份的存档位置或文件。").ToList() };
        controller.Snapshots = [snapshot];
        navigation.SelectedIndex = 3;
        await Task.Delay(150);
        InvokeButton(FindButton(view, "读取云端备份"));
        await WaitIdleAsync(view);
        var left = Field<GameSearchPicker>(view, "_mapBackupGame");
        var right = Field<GameSearchPicker>(view, "_mapLocalGame");
        var saved = Field<CheckBox>(view, "_cloudOnlySaves");
        var unmatched = Field<CheckBox>(view, "_cloudOnlyUnmatched");
        try
        {
            string detail = Field<TextBlock>(view, "_snapshotDetails").Text;
            Check(detail.Contains("78 款游戏 · 6 款含存档 · 6 份存档") && !detail.Contains("尚无"), "历史摘要必须和真实存档引用一致，不能混入逐游戏缺失提示。");
            Check(left.Results.Items.Count == 78 && !Field<Expander>(view, "_snapshotNoticeExpander").IsExpanded, "云端必须显示完整游戏列表，旧提示默认收起。");
            Check(left.Results.Items.OfType<ListViewItem>().Count(item => ((GameSearchItem)item.Tag).Badge.Contains("含存档")) == 6, "云端含存档标签数量错误。");
            Check(left.Results.Items.OfType<ListViewItem>().All(item => ((StackPanel)item.Content).Children.OfType<Panel>().Any(panel => panel.Children.OfType<Border>().Any())), "游戏候选应使用共享彩色状态标签。");
            saved.IsChecked = true;
            await Task.Delay(100);
            Check(left.Results.Items.Count == 6, "含存档筛选必须显示6款游戏。");
            left.SearchBox.Text = "VNDB:v6";
            await Task.Delay(100);
            Check(left.Results.Items.Count == 1, "状态文案变化后仍须能按来源ID搜索云端游戏。");
            left.SearchBox.Text = "云端游戏06";
            await Task.Delay(100);
            Check(left.HasPendingQuery && left.Results.Items.Count == 1, "输入仅筛选，不得直接提交关联。");
            left.Results.SelectedItem = left.Results.Items[0];
            Check(!left.HasPendingQuery && left.SelectedId == cloud[5].Id, "明确点击候选后应提交对应ID。");
            right.OnlyLocal.IsChecked = true;
            Check(right.Results.Items.Count == 1, "本机有效目录筛选错误。");
            right.SearchBox.Text = controller.GetGames()[0].LocalDirectory!;
            await Task.Delay(100);
            Check(right.Results.Items.Count == 1, "本机游戏目录必须可以搜索，但不在条目上展示。");
            right.Results.SelectedItem = right.Results.Items[0];
            right.SearchBox.Text = "本地游戏";
            await Task.Delay(100);
            Check(right.HasPendingQuery && right.Results.SelectedItem == null, "重新搜索当前游戏后，应等待用户明确点击候选。");
            right.Results.SelectedItem = right.Results.Items[0];
            Check(!right.HasPendingQuery, "再次点击原目标也必须能确认搜索，不得卡住保存。");
            Check(Field<TextBlock>(view, "_mappingPreview").Text.Contains("云端游戏06"), "应显示当前云端到本机的对应预览。");
            InvokeButton(FindButton(view, "保存对应关系"));
            await WaitIdleAsync(view);
            Check(controller.Settings.GameMappings.Any(item => item.BackupGameId == cloud[5].Id && item.LocalGameId == controller.GetGames()[0].Id), "保存对应关系应使用显式ID。");
            unmatched.IsChecked = true;
            Check(left.Results.Items.Count == 5 && left.SelectedId == cloud[5].Id, "筛选隐藏当前条目时应保留选择，未匹配数应更新。");
            unmatched.IsChecked = false;
            foreach (int width in new[] { 1080, 620 })
            {
                double scale = host.XamlRoot.RasterizationScale;
                _window!.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(width * scale), (int)(760 * scale)));
                await Task.Delay(180);
                ScrollViewer scroll = Field<ScrollViewer>(view, "_pageScroll");
                double top = left.TransformToVisual(scroll).TransformPoint(new Windows.Foundation.Point()).Y + scroll.VerticalOffset;
                scroll.ChangeView(null, Math.Max(0, top - 35), null, true);
                await Task.Delay(100);
                var localPosition = right.TransformToVisual(left).TransformPoint(new Windows.Foundation.Point());
                Check(width == 1080 ? localPosition.X > 300 && Math.Abs(localPosition.Y) < 2 : localPosition.Y > 100, "宽窗口应左右并排，窄窗口应上下排列。");
                await CaptureAsync(host, $"history-mapping-{width}.png");
            }
            MethodInfo addLog = typeof(BackupSettingsView).GetMethod("AddLog", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var logs = Field<List<string>>(view, "_logEntries");
            int before = logs.Count;
            addLog.Invoke(view, ["正在校验存档（1/78）：游戏1"]);
            addLog.Invoke(view, ["正在校验存档（78/78）：游戏78"]);
            Check(logs.Count == before + 1 && logs[^1].Contains("78/78"), "连续存档进度应合并而非刷屏。");
            addLog.Invoke(view, ["备份完成。\n本次更改：存档更新 1 款游戏"]);
            Check(Field<TextBox>(view, "_logText").Text.Replace("\r\n", "\n").Replace("\r", "\n").Contains("\n    本次更改"), "多行结果应保留缩进与分段。");
            controller.Snapshots = [];
            InvokeButton(FindButton(view, "读取云端备份"));
            await WaitIdleAsync(view);
            Check(left.Results.Items.Count == 0 && Field<Expander>(view, "_snapshotNoticeExpander").Visibility == Visibility.Collapsed, "空历史不得残留旧游戏和提示。");
            results.Add("PASS history-ui: all 78 games / 6 saves agree; tags; two-column search/filter/map; selection retained; narrow stacking; progress logs collapsed; empty reload cleared");
        }
        finally
        {
            controller.Settings.GameMappings = originalMappings;
            controller.Snapshots = [];
            saved.IsChecked = unmatched.IsChecked = false;
            right.OnlyLocal.IsChecked = false;
            double scale = host.XamlRoot.RasterizationScale;
            _window!.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1080 * scale), (int)(760 * scale)));
            navigation.SelectedIndex = 0;
        }
    }
}
