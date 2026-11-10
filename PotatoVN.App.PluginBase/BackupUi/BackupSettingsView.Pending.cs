using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.Backup.Core;

namespace PotatoVN.App.PluginBase.BackupUi;

public sealed partial class BackupSettingsView
{
    private readonly TextBlock _pendingSummary = Note("");
    private readonly TextBlock _pendingCounts = Note("");
    private readonly ComboBox _pendingGameFilter = new() { Header = "游戏", MinWidth = 150 };
    private readonly ComboBox _pendingVersionFilter = new() { Header = "备份版本", MinWidth = 190 };
    private readonly ComboBox _pendingStatusFilter = new() { Header = "状态", MinWidth = 130 };
    private readonly TextBox _pendingSearch = TextInput("搜索", "游戏、渠道、备份时间或目标路径");
    private readonly ListView _pendingList = new()
    {
        SelectionMode = ListViewSelectionMode.Multiple,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        MaxHeight = 440
    };
    private bool _buildingPendingFilters;
    private string _pendingFilterKey = "";

    private StackPanel BuildPendingTab()
    {
        var panel = Stack();
        panel.Children.Add(Heading("待恢复区"));
        panel.Children.Add(Note("这里保存已经下载的存档。所有存档都等游戏关联有效本地目录后，再由你确认恢复。清理仅删除本机暂存副本和任务。"));
        panel.Children.Add(_pendingSummary);
        panel.Children.Add(_pendingCounts);
        var filters = Row();
        foreach (UIElement control in new UIElement[] { _pendingGameFilter, _pendingVersionFilter, _pendingStatusFilter }) filters.Children.Add(control);
        panel.Children.Add(filters);
        panel.Children.Add(_pendingSearch);
        panel.Children.Add(_pendingList);
        var actions = Row();
        actions.Children.Add(ActionButton("刷新状态", async ct => await _controller.RefreshPendingAsync(ct)));
        actions.Children.Add(ActionButton("选择当前结果", _ => { _pendingList.SelectAll(); return Task.CompletedTask; }));
        actions.Children.Add(ActionButton("设置游戏与目标", async ct => await ConfigurePendingTargetAsync(ct)));
        panel.Children.Add(actions);
        var restoreActions = Row();
        restoreActions.Children.Add(ActionButton("恢复所选存档", async ct =>
        {
            Guid[] ids = SelectedPendingIds();
            if (ids.Length == 0) throw new InvalidOperationException("请先选择要恢复的存档位置。");
            await _controller.RefreshPendingAsync(ct);
            PendingRestore[] entries = _controller.GetPending().Where(p => ids.Contains(p.Id)).ToArray();
            if (entries.Any(p => p.Status is RestoreStatus.PendingLink or RestoreStatus.PendingPath || string.IsNullOrWhiteSpace(p.TargetPath)))
                throw new InvalidOperationException("所选项目仍需关联有效本地游戏或指定目标位置，请先点击“设置游戏与目标”。");
            var body = Stack(8);
            body.Children.Add(Note("内容相同的存档会跳过；内容不同时，用所选版本替换目标位置。恢复前会保存当前存档，写入失败时回滚。"));
            foreach (PendingRestore entry in entries)
                body.Children.Add(Note($"{entry.Manifest.Game.Name} · {entry.Manifest.CapturedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n→ {entry.TargetPath}"));
            if (await DialogAsync("确认存档恢复位置", new ScrollViewer { Content = body, MaxHeight = 440 }, "恢复所选存档") != ContentDialogResult.Primary) return;
            await _controller.RestorePendingAsync(ids, ct);
        }));
        restoreActions.Children.Add(ActionButton("清理所选", async ct => await ClearPendingWithConfirmationAsync(SelectedPendingIds(), ct)));
        panel.Children.Add(restoreActions);
        var cleanup = Row();
        cleanup.Children.Add(ActionButton("清理全部已恢复", async ct =>
        {
            Guid[] ids = _controller.GetPending().Where(p => p.Status == RestoreStatus.Restored).Select(p => p.Id).ToArray();
            if (ids.Length == 0) throw new InvalidOperationException("没有已恢复的暂存项目。");
            await ClearPendingWithConfirmationAsync(ids, ct);
        }));
        cleanup.Children.Add(ActionButton("全部清理", async ct => await ClearPendingWithConfirmationAsync(_controller.GetPending().Select(p => p.Id).ToArray(), ct)));
        panel.Children.Add(cleanup);
        panel.Children.Add(Note("清理不会删除云端版本或已写入游戏实际目录的存档。云端对应版本仍存在时，可以从历史页重新下载。正在使用的项目需先完成或取消操作。"));
        _pendingStatusFilter.SelectionChanged += (_, _) => { if (!_buildingPendingFilters) RefreshPendingView(); };
        _pendingGameFilter.SelectionChanged += (_, _) => { if (!_buildingPendingFilters) RefreshPendingView(); };
        _pendingVersionFilter.SelectionChanged += (_, _) => { if (!_buildingPendingFilters) RefreshPendingView(); };
        _pendingSearch.TextChanged += (_, _) => { if (!_buildingPendingFilters) RefreshPendingView(); };
        RefreshPendingView();
        return panel;
    }

    private void RefreshPendingView()
    {
        if (_pendingList == null || _buildingPendingFilters) return;
        IReadOnlyList<PendingRestore> entries = _controller.GetPending();
        _pendingSummary.Text = $"实际占用 {SizeLabel(_controller.PendingBytes)} · {entries.Select(p => p.Manifest.Game.Id).Distinct().Count()} 个游戏 · {entries.Select(p => $"{p.ProfileId}/{p.Manifest.ManifestPath}").Distinct().Count()} 个版本 · {entries.Count} 个存档位置";
        _pendingCounts.Text = string.Join("　", Enum.GetValues<RestoreStatus>().Select(status => $"{StateLabel(status)} {entries.Count(p => p.Status == status)}"));
        string key = string.Join("|", entries.Select(p => p.Id.ToString()).OrderBy(id => id));
        if (_pendingFilterKey != key || _pendingGameFilter.Items.Count == 0)
        {
            _pendingFilterKey = key;
            _buildingPendingFilters = true;
            object? oldGame = Choice(_pendingGameFilter);
            object? oldVersion = Choice(_pendingVersionFilter);
            object? oldStatus = Choice(_pendingStatusFilter);
            _pendingGameFilter.Items.Clear();
            _pendingVersionFilter.Items.Clear();
            _pendingStatusFilter.Items.Clear();
            AddChoice(_pendingGameFilter, "all", "全部游戏");
            AddChoice(_pendingVersionFilter, "all", "全部版本");
            AddChoice(_pendingStatusFilter, "all", "全部状态");
            foreach (IGrouping<Guid, PendingRestore> game in entries.GroupBy(p => p.Manifest.Game.Id).OrderBy(g => g.First().Manifest.Game.Name, StringComparer.CurrentCultureIgnoreCase))
                AddChoice(_pendingGameFilter, game.Key.ToString("N"), game.First().Manifest.Game.Name);
            foreach (IGrouping<string, PendingRestore> version in entries.GroupBy(VersionKey).OrderByDescending(g => g.First().Manifest.CapturedUtc))
            {
                PendingRestore entry = version.First();
                AddChoice(_pendingVersionFilter, version.Key, $"{entry.Manifest.CapturedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {entry.ProfileName}");
            }
            foreach (RestoreStatus status in Enum.GetValues<RestoreStatus>()) AddChoice(_pendingStatusFilter, status.ToString(), StateLabel(status));
            SelectChoice(_pendingGameFilter, oldGame ?? "all");
            SelectChoice(_pendingVersionFilter, oldVersion ?? "all");
            SelectChoice(_pendingStatusFilter, oldStatus ?? "all");
            if (_pendingGameFilter.SelectedItem == null) _pendingGameFilter.SelectedIndex = 0;
            if (_pendingVersionFilter.SelectedItem == null) _pendingVersionFilter.SelectedIndex = 0;
            if (_pendingStatusFilter.SelectedItem == null) _pendingStatusFilter.SelectedIndex = 0;
            _buildingPendingFilters = false;
        }
        HashSet<Guid> selected = SelectedPendingIds().ToHashSet();
        _pendingList.Items.Clear();
        string gameFilter = Choice(_pendingGameFilter)?.ToString() ?? "all";
        string versionFilter = Choice(_pendingVersionFilter)?.ToString() ?? "all";
        string statusFilter = Choice(_pendingStatusFilter)?.ToString() ?? "all";
        string search = _pendingSearch.Text.Trim();
        foreach (PendingRestore entry in entries.OrderBy(p => p.Manifest.Game.Name, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(p => p.Manifest.CapturedUtc))
        {
            if (gameFilter != "all" && gameFilter != entry.Manifest.Game.Id.ToString("N")) continue;
            if (versionFilter != "all" && versionFilter != VersionKey(entry)) continue;
            if (statusFilter != "all" && statusFilter != entry.Status.ToString()) continue;
            string description = $"{entry.Manifest.Game.Name} {entry.ProfileName} {entry.Manifest.CapturedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} {entry.TargetPath}";
            if (search.Length > 0 && !description.Contains(search, StringComparison.CurrentCultureIgnoreCase)) continue;
            var content = Stack(4);
            content.Children.Add(Heading($"{entry.Manifest.Game.Name} · {StateLabel(entry.Status)}{(entry.IsBusy ? " · 正在使用" : "")}", "BodyStrongTextBlockStyle"));
            content.Children.Add(Note($"{entry.ProfileName} · {entry.Manifest.CapturedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 存档包 {SizeLabel(entry.Manifest.Length)}"));
            SaveLocation? location = entry.Manifest.Locations.FirstOrDefault(l => l.Id == entry.LocationId);
            if (location != null) content.Children.Add(Note($"{(location.IsFile ? "文件" : "目录")} · {SaveBaseLabel(location.BaseKind)}{(string.IsNullOrWhiteSpace(location.RelativePath) ? "" : "\\" + location.RelativePath)}"));
            content.Children.Add(Note(string.IsNullOrWhiteSpace(entry.TargetPath) ? "目标位置尚未确定" : $"目标：{entry.TargetPath}"));
            if (!string.IsNullOrWhiteSpace(entry.Error)) content.Children.Add(Note(entry.Error));
            var item = new ListViewItem { Content = content, Tag = entry, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            _pendingList.Items.Add(item);
            if (selected.Contains(entry.Id)) _pendingList.SelectedItems.Add(item);
        }
    }

    private Guid[] SelectedPendingIds() => _pendingList.SelectedItems.OfType<ListViewItem>()
        .Select(item => ((PendingRestore)item.Tag).Id).ToArray();

    private async Task ConfigurePendingTargetAsync(CancellationToken ct)
    {
        Guid[] ids = SelectedPendingIds();
        if (ids.Length != 1) throw new InvalidOperationException("请只选择一个存档位置，再设置对应游戏与目标。");
        PendingRestore entry = _controller.GetPending().First(p => p.Id == ids[0]);
        var game = new ComboBox { Header = "对应的本机游戏条目", HorizontalAlignment = HorizontalAlignment.Stretch };
        AddChoice(game, null, "自动识别对应条目");
        foreach (GameIdentity candidate in _controller.GetGames().OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
            AddChoice(game, candidate.Id, GameLabel(candidate));
        SelectChoice(game, entry.LocalGameId);
        if (game.SelectedItem == null) game.SelectedIndex = 0;
        var target = TextInput("本机目标路径", "留空则按备份位置规则解析");
        target.Text = entry.TargetOverride ?? "";
        var body = Stack();
        body.Children.Add(Note($"备份游戏：{entry.Manifest.Game.Name}\n版本时间：{entry.Manifest.CapturedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}"));
        body.Children.Add(game);
        body.Children.Add(target);
        body.Children.Add(Note("对应条目仍需关联有效本地游戏目录。此处只保存对应关系与目标，不会写入存档。自定义位置需要完整绝对路径。"));
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };
        body.Children.Add(error);
        var dialog = NewDialog("设置存档恢复位置", body, "保存对应关系");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(target.Text) || Path.IsPathFullyQualified(target.Text.Trim())) return;
            error.Message = "请填写完整绝对路径，或留空使用自动解析的目标。";
            error.IsOpen = true;
            args.Cancel = true;
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await _controller.ConfigurePendingAsync(entry.Id, Choice(game) is Guid gameId ? gameId : null,
            string.IsNullOrWhiteSpace(target.Text) ? null : target.Text.Trim(), ct);
        await _controller.RefreshPendingAsync(ct);
    }

    private async Task ClearPendingWithConfirmationAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) throw new InvalidOperationException("没有选中可清理的暂存项目。");
        PendingRestore[] entries = _controller.GetPending().Where(p => ids.Contains(p.Id)).ToArray();
        if (entries.Any(p => p.IsBusy)) throw new InvalidOperationException("选中项目正在下载或恢复，请先完成或取消当前操作。");
        int notRestored = entries.Count(p => p.Status != RestoreStatus.Restored);
        string message = $"清理 {entries.Length} 个存档位置的本机暂存副本及任务？\n\n云端备份、游戏资料和游戏实际目录中的存档不会删除。";
        if (notRestored > 0) message += $"\n\n其中 {notRestored} 项尚未恢复。清理后，需要在云端对应版本仍存在时重新下载。";
        if (await DialogAsync(notRestored > 0 ? "清理尚未恢复的存档" : "清理本机暂存副本", Note(message), "清理本机副本") != ContentDialogResult.Primary) return;
        await _controller.ClearPendingAsync(ids, ct);
    }

    private static string VersionKey(PendingRestore entry) => $"{entry.ProfileId:N}/{entry.Manifest.ManifestPath}";
    private static string StateLabel(RestoreStatus status) => status switch
    {
        RestoreStatus.PendingLink => "待关联", RestoreStatus.PendingPath => "待指定路径",
        RestoreStatus.Ready => "可恢复", RestoreStatus.Restored => "已恢复", _ => "失败"
    };
}
