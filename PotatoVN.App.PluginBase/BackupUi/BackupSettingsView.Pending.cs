using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.Backup.Core;
using PotatoVN.Backup.Core.Engine;

namespace PotatoVN.App.PluginBase.BackupUi;

public sealed partial class BackupSettingsView
{
    private readonly TextBlock _pendingSummary = Note("");
    private readonly TextBlock _pendingCounts = Note("");
    private readonly TextBlock _pendingSelection = Note("");
    private readonly TextBlock _pendingEmpty = Note("");
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
        panel.Children.Add(Heading("待恢复存档"));
        panel.Children.Add(_pendingSummary);
        panel.Children.Add(_pendingCounts);
        var filters = Row();
        foreach (UIElement control in new UIElement[] { _pendingGameFilter, _pendingVersionFilter, _pendingStatusFilter }) filters.Children.Add(control);
        panel.Children.Add(filters);
        panel.Children.Add(_pendingSearch);
        panel.Children.Add(_pendingList);
        panel.Children.Add(_pendingEmpty);
        panel.Children.Add(_pendingSelection);
        var actions = Row();
        actions.Children.Add(ActionButton("刷新状态", async ct => await _controller.RefreshPendingAsync(ct), successMessage: "待恢复存档已刷新。"));
        actions.Children.Add(ActionButton("全选筛选结果", _ => { _pendingList.SelectAll(); return Task.CompletedTask; }));
        actions.Children.Add(ActionButton("取消选择", _ => { _pendingList.SelectedItems.Clear(); return Task.CompletedTask; }));
        actions.Children.Add(ActionButton("设置所选目标…", async ct => await ConfigurePendingTargetAsync(ct), successMessage: "恢复位置已保存。"));
        panel.Children.Add(actions);
        var restoreActions = Row();
        restoreActions.Children.Add(ActionButton("恢复所选存档", async ct =>
        {
            Guid[] ids = SelectedPendingIds();
            if (ids.Length == 0) throw new InvalidOperationException("请先选择要恢复的存档位置。");
            await _controller.RefreshPendingAsync(ct);
            PendingRestore[] entries = _controller.GetPending().Where(p => ids.Contains(p.Id)).ToArray();
            if (entries.Any(p => p.Status is RestoreStatus.PendingLink or RestoreStatus.PendingPath || string.IsNullOrWhiteSpace(p.TargetPath)))
                throw new InvalidOperationException("所选项目仍需关联有效本地游戏或指定目标位置，请先点击“设置所选目标”。");
            if (entries.GroupBy(p => Path.GetFullPath(p.TargetPath!), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                throw new InvalidOperationException("同一目标位置选中了多个存档版本。请先用“备份版本”筛选，或只选中希望恢复的那个版本。");
            var body = Stack(8);
            body.Children.Add(Note("内容相同的存档会跳过；内容不同时，用所选版本替换目标位置。恢复前会保存当前存档，写入失败时回滚。"));
            foreach (PendingRestore entry in entries)
                body.Children.Add(Note($"{entry.Manifest.Game.Name} · {entry.Manifest.CapturedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n→ {entry.TargetPath}"));
            if (await DialogAsync("确认存档恢复位置", new ScrollViewer { Content = body, MaxHeight = 440 }, "恢复所选存档") != ContentDialogResult.Primary)
                return;
            await _controller.RestorePendingAsync(ids, ct);
        }));
        panel.Children.Add(restoreActions);
        var cleanup = Stack(8);
        cleanup.Children.Add(Note("仅清理本机暂存副本，云端备份和游戏存档会保留。"));
        var cleanupActions = Row();
        cleanupActions.Children.Add(ActionButton("清理所选…", async ct => await ClearPendingWithConfirmationAsync(SelectedPendingIds(), ct), successMessage: "所选本机暂存副本已清理。"));
        cleanupActions.Children.Add(ActionButton("清理全部已恢复…", async ct =>
        {
            Guid[] ids = _controller.GetPending().Where(p => p.Status == RestoreStatus.Restored).Select(p => p.Id).ToArray();
            if (ids.Length == 0) throw new InvalidOperationException("没有已恢复的暂存项目。");
            await ClearPendingWithConfirmationAsync(ids, ct);
        }));
        cleanupActions.Children.Add(ActionButton("清空待恢复存档…", async ct => await ClearPendingWithConfirmationAsync(_controller.GetPending().Select(p => p.Id).ToArray(), ct)));
        cleanup.Children.Add(cleanupActions);
        panel.Children.Add(new Expander { Header = "管理暂存空间", Content = cleanup, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        _pendingList.SelectionChanged += (_, _) => UpdatePendingSelection();
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
            var content = Stack(8);
            var badges = Row();
            var title = Heading(entry.Manifest.Game.Name, "BodyStrongTextBlockStyle");
            title.MaxLines = 2;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            badges.Children.Add(title);
            StatusTone tone = entry.Status switch
            {
                RestoreStatus.Ready => StatusTone.Info,
                RestoreStatus.Restored => StatusTone.Success,
                RestoreStatus.Failed => StatusTone.Error,
                _ => StatusTone.Warning
            };
            badges.Children.Add(StatusBadge(StateLabel(entry.Status), tone));
            if (entry.IsBusy) badges.Children.Add(StatusBadge("正在使用"));
            content.Children.Add(badges);
            var metadata = Row();
            metadata.Children.Add(SecondaryText(entry.ProfileName));
            metadata.Children.Add(SecondaryText(entry.Manifest.CapturedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")));
            metadata.Children.Add(SecondaryText(SizeLabel(entry.Manifest.Length)));
            content.Children.Add(metadata);
            SaveLocation? location = entry.Manifest.Locations.FirstOrDefault(l => l.Id == entry.LocationId);
            if (!string.IsNullOrWhiteSpace(entry.TargetPath)) content.Children.Add(PathLine("存档", entry.TargetPath));
            else if (location != null)
                content.Children.Add(PathLine("备份位置", SaveBaseLabel(location.BaseKind) +
                    (string.IsNullOrWhiteSpace(location.RelativePath) ? "" : "\\" + location.RelativePath)));
            if (!string.IsNullOrWhiteSpace(entry.Error)) content.Children.Add(SecondaryText(entry.Error));
            var item = new ListViewItem { Content = content, Tag = entry, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 10) };
            _pendingList.Items.Add(item);
            if (selected.Contains(entry.Id)) _pendingList.SelectedItems.Add(item);
        }
        _pendingEmpty.Visibility = _pendingList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _pendingEmpty.Text = entries.Count == 0
            ? "暂无待恢复存档。"
            : "没有符合筛选条件的存档。";
        UpdatePendingSelection();
    }

    private void UpdatePendingSelection() =>
        _pendingSelection.Text = $"显示 {_pendingList.Items.Count} 个存档位置 · 已选 {_pendingList.SelectedItems.Count} 项";

    private Guid[] SelectedPendingIds() => _pendingList.SelectedItems.OfType<ListViewItem>()
        .Select(item => ((PendingRestore)item.Tag).Id).ToArray();

    private async Task ConfigurePendingTargetAsync(CancellationToken ct)
    {
        Guid[] ids = SelectedPendingIds();
        if (ids.Length != 1) throw new InvalidOperationException("请只选择一个存档位置，再设置对应游戏与目标。");
        PendingRestore entry = _controller.GetPending().First(p => p.Id == ids[0]);
        var game = new GameSearchPicker("对应的本机游戏");
        game.SetItems(new[] { new GameSearchItem(null, "自动识别对应条目", "保留自动识别规则") { Badge = "自动识别", Tone = StatusTone.Info } }
            .Concat(_controller.GetGames().OrderBy(candidate => candidate.Name, StringComparer.CurrentCultureIgnoreCase).Select(GameSearchItem.FromGame)),
            entry.LocalGameId);
        var target = TextInput("本机目标路径", "留空则按备份位置规则解析");
        target.Text = entry.TargetOverride ?? "";
        var body = Stack();
        body.Children.Add(GameEntryContent(entry.Manifest.Game, "云端存档", StatusTone.Info));
        body.Children.Add(SecondaryText($"版本时间：{entry.Manifest.CapturedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}"));
        body.Children.Add(game);
        body.Children.Add(target);
        SaveLocation? location = entry.Manifest.Locations.FirstOrDefault(l => l.Id == entry.LocationId);
        body.Children.Add(BrowsePathButton(target, location?.IsFile == true));
        var resolved = Note("");
        body.Children.Add(resolved);
        void UpdateTargetPreview()
        {
            try
            {
                GameIdentity? selectedGame = game.SelectedId is Guid selectedId ? _controller.GetGames().FirstOrDefault(g => g.Id == selectedId) : null;
                string? actual = location == null ? null : SaveArchiveService.Resolve(location, selectedGame, string.IsNullOrWhiteSpace(target.Text) ? null : target.Text.Trim());
                resolved.Text = actual == null ? "请选择已关联本机目录的游戏，并补全目标路径。" : $"将恢复到：{actual}";
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or NotSupportedException)
            { resolved.Text = exception.Message; }
        }
        game.SelectionChanged += (_, _) => { target.Text = ""; UpdateTargetPreview(); };
        target.TextChanged += (_, _) => UpdateTargetPreview();
        UpdateTargetPreview();
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };
        body.Children.Add(error);
        var dialog = NewDialog("设置存档恢复位置", new ScrollViewer { Content = body, MaxHeight = 500 }, "保存对应关系");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (game.HasPendingQuery)
            {
                error.Message = "请从搜索结果中选择游戏，或明确选择自动识别。";
                error.IsOpen = true;
                args.Cancel = true;
                return;
            }
            if (string.IsNullOrWhiteSpace(target.Text) || Path.IsPathFullyQualified(target.Text.Trim())) return;
            error.Message = "请填写完整绝对路径，或留空使用自动解析的目标。";
            error.IsOpen = true;
            args.Cancel = true;
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        await _controller.ConfigurePendingAsync(entry.Id, game.SelectedId is Guid gameId ? gameId : null,
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
        if (await DialogAsync(notRestored > 0 ? "清理尚未恢复的存档" : "清理本机暂存副本", Note(message), "清理本机副本") != ContentDialogResult.Primary)
            return;
        await _controller.ClearPendingAsync(ids, ct);
    }

    private static string VersionKey(PendingRestore entry) => $"{entry.ProfileId:N}/{entry.Manifest.ManifestPath}";
    private static string StateLabel(RestoreStatus status) => status switch
    {
        RestoreStatus.PendingLink => "待关联", RestoreStatus.PendingPath => "待指定路径",
        RestoreStatus.Ready => "可恢复", RestoreStatus.Restored => "已恢复", _ => "失败"
    };
}
