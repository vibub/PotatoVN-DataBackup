using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using PotatoVN.App.PluginBase.BackupUi;
using PotatoVN.Backup.Core;

namespace UiSmoke;

public sealed partial class SmokeApp
{
    private async Task CheckGameSelectionAsync(BackupSettingsView view, DemoController controller, Grid host, Pivot navigation, List<string> results)
    {
        var games = (List<GameIdentity>)controller.GetGames();
        var added = Enumerable.Range(0, 64).Select(index => new GameIdentity
        {
            Id = Guid.NewGuid(), Name = $"{(index < 32 ? "甲组" : "乙组")} SAVE 日本語 {index:00}",
            SourceIds = new() { ["Bangumi"] = $"picker-{index}" }
        }).ToArray();
        games.AddRange(added);
        HashSet<Guid> selected = Field<HashSet<Guid>>(view, "_selectedGameIds");
        Guid[] original = selected.ToArray();
        CheckBox all = Field<CheckBox>(view, "_allGames");
        bool? originalAll = all.IsChecked;
        TextBox search = Field<TextBox>(view, "_gameSearch");
        TextBox saveSearch = Field<TextBox>(view, "_saveGameSearch");
        CheckBox only = Field<CheckBox>(view, "_onlySelectedGames");
        CheckBox configured = Field<CheckBox>(view, "_saveGameConfigured");
        CheckBox unconfigured = Field<CheckBox>(view, "_saveGameUnconfigured");
        CheckBox local = Field<CheckBox>(view, "_saveGameLocal");
        try
        {
            InvokeButton(FindButton(view, "刷新游戏列表"));
            await WaitIdleAsync(view);
            all.IsChecked = false;
            selected.Clear();
            search.Text = "甲组";
            await Task.Delay(80);
            InvokeButton(FindButton(view, "选中筛选结果"));
            Check(selected.Count == 32 && selected.All(id => added.Any(game => game.Id == id && game.Name.StartsWith("甲组"))),
                $"批量选择应仅选中当前搜索的32款游戏，实际 {selected.Count}。");
            search.Text = "乙组";
            await WaitForUiAsync(() => Field<ListView>(view, "_gameSelection").Items.OfType<ListViewItem>()
                .All(item => ((GameIdentity)item.Tag).Name.StartsWith("乙组")), "乙组筛选未更新。");
            var list = Field<ListView>(view, "_gameSelection");
            list.SelectedItems.Add(list.Items[0]);
            Check(selected.Count == 33, "切换筛选后勾选新游戏不能丢失之前的32款。");
            search.Text = "不存在的合成游戏";
            await Task.Delay(80);
            await WaitForUiAsync(() => list.Items.Count == 0, "空搜索未更新。");
            Check(list.Items.Count == 0 && selected.Count == 33 && Field<TextBlock>(view, "_gameSelectionEmpty").Visibility == Visibility.Visible,
                "空搜索结果应保留全部选择并显示空状态。");
            only.IsChecked = true;
            search.Text = "";
            await Task.Delay(80);
            await WaitForUiAsync(() => list.Items.Count == 33, "已选列表未更新。");
            Check(list.Items.Count == 33, "只看已选应跨搜索展示全部已选游戏。");
            ListViewItem removed = (ListViewItem)list.Items[0];
            list.SelectedItems.Remove(removed);
            Check(selected.Count == 32 && list.Items.Count == 32 && !selected.Contains(((GameIdentity)removed.Tag).Id),
                "只看已选时取消勾选应立即移除该行及对应ID。");
            all.IsChecked = true;
            Check(Field<StackPanel>(view, "_manualGameScope").Visibility == Visibility.Collapsed && selected.Count == 32,
                "切到全部游戏应隐藏手选区且保留手选草稿。");
            all.IsChecked = false;
            only.IsChecked = false;
            search.Text = "ｓａｖｅ 日本語";
            await Task.Delay(80);
            await WaitForUiAsync(() => list.Items.Count == 64, "全半角搜索未更新。");
            Check(list.Items.Count == 64, "搜索应支持多个词、大小写及全半角，不应按名称合并游戏。");
            results.Add("PASS game-scope-search: 64 games; filtered batch selection; hidden IDs survive filters; selected-only removal; all-games draft; width/case-insensitive multiword search");

            ListView saveGames = Field<ListView>(view, "_saveGame");
            saveSearch.Text = "甲组";
            await WaitForUiAsync(() => saveGames.Items.Count == 32 && saveGames.Items.OfType<ListViewItem>()
                .All(item => ((GameIdentity)item.Tag).Name.StartsWith("甲组")), "存档甲组筛选未更新。");
            GameIdentity chosen = added[12];
            saveGames.SelectedItem = saveGames.Items.OfType<ListViewItem>().Single(item => ((GameIdentity)item.Tag).Id == chosen.Id);
            Check(Field<TextBlock>(view, "_saveGameTitle").Text == chosen.Name, "点击游戏列表应更新该游戏的存档详情。");
            saveSearch.Text = "乙组";
            await WaitForUiAsync(() => saveGames.Items.Count == 32 && saveGames.Items.OfType<ListViewItem>()
                .All(item => ((GameIdentity)item.Tag).Name.StartsWith("乙组")), "存档乙组筛选未更新。");
            Check(Field<TextBlock>(view, "_saveGameTitle").Text == chosen.Name && (Guid?)typeof(BackupSettingsView).GetField("_saveGameId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view) == chosen.Id,
                "筛选隐藏当前游戏时不能静默切换详情。");
            InvokeButton(FindButton(view, "刷新游戏列表"));
            await WaitIdleAsync(view);
            Check(saveSearch.Text == "乙组" && (Guid?)typeof(BackupSettingsView).GetField("_saveGameId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view) == chosen.Id,
                "刷新列表应保留搜索词与当前配置的游戏ID。");
            saveSearch.Text = "";
            await Task.Delay(80);
            await WaitForUiAsync(() => saveGames.Items.Count == 66, "存档列表未复原。");
            controller.Settings.SaveConfigs.Add(new GameSaveConfig { GameId = chosen.Id, Locations = [new SaveLocation { RelativePath = "save" }] });
            configured.IsChecked = true;
            await WaitForUiAsync(() => saveGames.Items.Count == 1, "已配置筛选未更新。");
            Check(saveGames.Items.Count == 1 && ((GameIdentity)((ListViewItem)saveGames.Items[0]).Tag).Id == chosen.Id,
                "已配置筛选应只包含存在确认存档位置的游戏。");
            unconfigured.IsChecked = true;
            Check(configured.IsChecked == false, "勾选未配置时应取消已配置。");
            await WaitForUiAsync(() => saveGames.Items.Count == 65, "未配置筛选未更新。");
            Check(saveGames.Items.Count == 65, "未配置筛选不应包含已有位置的游戏。");
            configured.IsChecked = true;
            Check(unconfigured.IsChecked == false, "勾选已配置时应取消未配置。");
            local.IsChecked = true;
            await WaitForUiAsync(() => saveGames.Items.Count == 0, "已配置与本机目录的组合筛选未更新。");
            Check(configured.IsChecked == true && local.IsChecked == true, "目录条件可与已配置同时勾选。");
            unconfigured.IsChecked = true;
            Check(configured.IsChecked == false && local.IsChecked == true, "切换配置条件应保留目录条件。");
            await WaitForUiAsync(() => saveGames.Items.Count == 1 &&
                ((GameIdentity)((ListViewItem)saveGames.Items[0]).Tag).Id == games[0].Id, "本机目录筛选未更新。");
            Check(saveGames.Items.Count == 1, "本机目录有效筛选应使用实际存在的目录。");
            configured.IsChecked = false;
            unconfigured.IsChecked = false;
            local.IsChecked = false;
            saveSearch.Text = "没有结果";
            await Task.Delay(80);
            await WaitForUiAsync(() => saveGames.Items.Count == 0 &&
                Field<TextBlock>(view, "_saveGameEmpty").Visibility == Visibility.Visible, "存档空结果未更新。");
            Check(saveGames.Items.Count == 0 && Field<TextBlock>(view, "_saveGameEmpty").Visibility == Visibility.Visible,
                "存档游戏列表也应显示空搜索状态。");
            saveSearch.Text = "";
            await Task.Delay(80);
            await WaitForUiAsync(() => saveGames.Items.Count == 66, "存档列表未复原。");
            results.Add("PASS save-game-search: master/detail stays on explicit ID when filtered out; refresh retains query; mutually exclusive configured/unconfigured checkboxes; valid-directory intersection; no filters shows all; empty state");

            search.Text = "";
            await Task.Delay(80);
            all.IsChecked = true;
            saveGames.SelectedItem = saveGames.Items.OfType<ListViewItem>().Single(item => ((GameIdentity)item.Tag).Id == games[0].Id);
            ScrollViewer content = Field<ScrollViewer>(view, "_pageScroll");
            foreach (int width in new[] { 1080, 620 })
            {
                double scale = host.XamlRoot.RasterizationScale;
                _window!.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(width * scale), (int)(760 * scale)));
                await Task.Delay(180);
                Grid layout = Field<Grid>(view, "_saveGameLayout");
                StackPanel details = Field<StackPanel>(view, "_saveGameDetails");
                Check(Grid.GetColumn(details) == (width == 1080 ? 1 : 0) && Grid.GetRow(details) == (width == 1080 ? 1 : 2),
                    "存档列表在宽窗口应左右分栏，窄窗口应上下排列。");
                Check(saveGames.ActualHeight <= 361 && content.ScrollableWidth < 1, "大量游戏不能撑高选择列表或造成横向裁切。");
                double top = layout.TransformToVisual(content).TransformPoint(new Windows.Foundation.Point()).Y + content.VerticalOffset;
                content.ChangeView(null, Math.Max(0, top - 4), null, true);
                await Task.Delay(100);
                saveSearch.Focus(FocusState.Programmatic);
                await CaptureAsync(host, $"game-picker-{width}-light.png");
                host.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32));
                host.RequestedTheme = ElementTheme.Dark;
                await Task.Delay(150);
                await CaptureAsync(host, $"game-picker-{width}-dark.png");
                host.RequestedTheme = ElementTheme.Light;
                host.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
            }
            results.Add("PASS game-picker-responsive: 66 games; list height bounded; wide columns and narrow stacking; light/dark screenshots at 1080 and 620 DIP");
        }
        finally
        {
            games.RemoveAll(game => added.Any(candidate => candidate.Id == game.Id));
            controller.Settings.SaveConfigs.RemoveAll(config => added.Any(game => game.Id == config.GameId));
            selected.Clear();
            selected.UnionWith(original);
            search.Text = "";
            await Task.Delay(80);
            saveSearch.Text = "";
            await Task.Delay(80);
            configured.IsChecked = false;
            unconfigured.IsChecked = false;
            local.IsChecked = false;
            only.IsChecked = false;
            all.IsChecked = originalAll;
            InvokeButton(FindButton(view, "刷新游戏列表"));
            await WaitIdleAsync(view);
            double scale = host.XamlRoot.RasterizationScale;
            _window!.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1080 * scale), (int)(760 * scale)));
            await Task.Delay(150);
        }
    }

    private static async Task WaitForUiAsync(Func<bool> ready, string message)
    {
        for (int attempt = 0; attempt < 80 && !ready(); attempt++) await Task.Delay(25);
        Check(ready(), message);
    }

    private static async Task EnterGameQueryAsync(GameSearchPicker picker, string text)
    {
        picker.SearchBox.Text = text;
        await Task.Delay(120);
    }
}
