using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.Backup.Core;
using PotatoVN.Backup.Core.Engine;

namespace PotatoVN.App.PluginBase.BackupUi;

public sealed partial class BackupSettingsView
{
    private readonly TextBlock _pendingSummary = Note("");
    private readonly TextBlock _pendingSelection = SectionHeading("");
    private readonly TextBlock _pendingRestoreHint = SecondaryText("");
    private readonly TextBlock _pendingEmpty = Note("");
    private readonly StackPanel _pendingVersionFilters = Stack(0);
    private readonly DropDownButton _pendingVersionFilterButton = new();
    private readonly Dictionary<Guid, string> _pendingGameVersions = new();
    private readonly Dictionary<RestoreStatus, CheckBox> _pendingStatusFilters = new();
    private readonly TextBox _pendingSearch = TextInput("搜索", "游戏、渠道、备份时间或目标路径");
    private readonly ListView _pendingList = new()
    {
        SelectionMode = ListViewSelectionMode.None,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        MaxHeight = 440
    };
    private bool _buildingPendingFilters;
    private string? _pendingRestoreBlocker;
    private PendingRestore[] _selectedPending = [];

    private sealed record PendingVersionChoice(string Key, string Label, DateTimeOffset Time, bool IsLegacy, PendingRestore[] Locations);

    private StackPanel BuildPendingTab()
    {
        var panel = Stack();
        panel.Children.Add(SectionHeading("待恢复存档"));
        var toolbar = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _pendingSummary.VerticalAlignment = VerticalAlignment.Center;
        toolbar.Children.Add(_pendingSummary);
        Button refresh = ActionButton("刷新状态", async ct => await _controller.RefreshPendingAsync(ct),
            successMessage: "待恢复存档已刷新。", style: "SubtleButtonStyle");
        refresh.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(refresh, 1);
        toolbar.Children.Add(refresh);
        panel.Children.Add(toolbar);
        panel.Children.Add(_pendingSearch);
        var statusFilters = Row();
        foreach (RestoreStatus status in Enum.GetValues<RestoreStatus>())
        {
            CheckBox check = PendingFilter(status.ToString(), StateLabel(status));
            _pendingStatusFilters.Add(status, check);
            statusFilters.Children.Add(check);
        }
        _pendingVersionFilterButton.Content = "按备份版本筛选";
        _pendingVersionFilterButton.Flyout = new Flyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft,
            Content = new ScrollViewer
            {
                Content = _pendingVersionFilters, MaxHeight = 260, MaxWidth = 400,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            }
        };
        statusFilters.Children.Add(_pendingVersionFilterButton);
        panel.Children.Add(statusFilters);
        var results = Stack(8);
        results.Children.Add(_pendingList);
        results.Children.Add(_pendingEmpty);
        var sidebar = Stack(8);
        sidebar.Children.Add(_pendingSelection);
        sidebar.Children.Add(_pendingRestoreHint);
        var actions = Row();
        sidebar.Children.Add(actions);
        actions.Children.Add(ActionButton("设置所选目标…", async ct => await ConfigurePendingTargetAsync(ct), successMessage: "恢复位置已保存。",
            canExecute: () => _selectedPending is [{ IsBusy: false }]));
        actions.Children.Add(ActionButton("恢复所选存档", async ct =>
        {
            Guid[] ids = SelectedPendingIds();
            if (ids.Length == 0) throw new InvalidOperationException("请先选择要恢复的存档位置。");
            var versions = PendingLocationChecks().Where(check => check.IsChecked == true)
                .ToDictionary(check => ((PendingRestore)check.Tag).Id, check => (string)check.DataContext);
            await _controller.RefreshPendingAsync(ct);
            PendingRestore[] entries = _controller.GetPending().Where(p => ids.Contains(p.Id)).ToArray();
            if (entries.Length != ids.Length) throw new InvalidOperationException("所选存档已变化，请重新选择。");
            if (PendingRestoreBlocker(entries) is { } blocker) throw new InvalidOperationException(blocker);
            var body = Stack(8);
            body.Children.Add(Note("内容相同的存档会跳过；内容不同时，用所选版本替换目标位置。恢复前会保存当前存档，写入失败时回滚。"));
            foreach (PendingRestore entry in entries)
                body.Children.Add(Note($"{entry.Manifest.Game.Name} · {versions[entry.Id]}\n→ {entry.TargetPath}"));
            if (await DialogAsync("确认存档恢复位置", new ScrollViewer { Content = body, MaxHeight = 440 }, "恢复所选存档") != ContentDialogResult.Primary)
                return;
            await _controller.RestorePendingAsync(ids, ct);
        }, canExecute: () => _pendingRestoreBlocker == null && _selectedPending.Length > 0, style: "AccentButtonStyle"));
        var selectionActions = Row();
        selectionActions.Children.Add(ActionButton("全选筛选结果", _ => { SelectPendingLocations(true); return Task.CompletedTask; },
            canExecute: () => _pendingList.Items.Count > 0, style: "SubtleButtonStyle"));
        selectionActions.Children.Add(ActionButton("取消选择", _ => { SelectPendingLocations(false); return Task.CompletedTask; },
            canExecute: () => _selectedPending.Length > 0, style: "SubtleButtonStyle"));
        sidebar.Children.Add(selectionActions);
        Border selectionPanel = Surface(sidebar);
        selectionPanel.VerticalAlignment = VerticalAlignment.Top;
        panel.Children.Add(ResponsiveColumns(selectionPanel, results, new GridLength(180)));
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
        _pendingSearch.TextChanged += (_, _) => { if (!_buildingPendingFilters) RefreshPendingView(); };
        RefreshPendingView();
        return panel;
    }

    private CheckBox PendingFilter(string key, string label)
    {
        var check = new CheckBox { Tag = key, Content = Note(label), MinWidth = 0 };
        check.Checked += (_, _) => RefreshPendingView();
        check.Unchecked += (_, _) => RefreshPendingView();
        return check;
    }

    private static HashSet<string> CheckedPendingFilters(IEnumerable<CheckBox> filters) =>
        filters.Where(check => check.IsChecked == true).Select(check => (string)check.Tag).ToHashSet();

    private void SetPendingFilters(StackPanel options, IEnumerable<(string Key, string Label)> items)
    {
        var choices = items.ToArray();
        var old = options.Children.OfType<CheckBox>().ToArray();
        if (old.Select(check => ((string)check.Tag, ((TextBlock)check.Content).Text)).SequenceEqual(choices)) return;
        HashSet<string> selected = CheckedPendingFilters(old);
        options.Children.Clear();
        foreach (var (key, label) in choices)
        {
            CheckBox check = PendingFilter(key, label);
            check.IsChecked = selected.Contains(key);
            options.Children.Add(check);
        }
    }

    private void RefreshPendingView()
    {
        if (_buildingPendingFilters) return;
        IReadOnlyList<PendingRestore> entries = _controller.GetPending();
        var references = entries.SelectMany(entry => entry.BackupVersions.DefaultIfEmpty(),
            (entry, version) => (Entry: entry, Version: version)).ToArray();
        var versionGroups = references.GroupBy(row => VersionKey(row.Entry.ProfileId, row.Version))
            .OrderByDescending(group => group.First().Version?.CreatedUtc).ToArray();
        _pendingSummary.Text = $"实际占用 {SizeLabel(_controller.PendingBytes)} · {entries.Select(p => p.Manifest.Game.Id).Distinct().Count()} 个游戏 · {versionGroups.Count(group => group.First().Version != null)} 个备份版本 · {entries.Count} 个存档位置";
        foreach (var (status, check) in _pendingStatusFilters)
            ((TextBlock)check.Content).Text = $"{StateLabel(status)} {entries.Count(p => p.Status == status)}";

        _buildingPendingFilters = true;
        SetPendingFilters(_pendingVersionFilters, versionGroups.Select(group =>
        {
            var (entry, version) = group.First();
            int games = group.Select(row => row.Entry.Manifest.Game.Id).Distinct().Count();
            string label = version == null ? "旧暂存（版本未记录）" : BackupTime(version.CreatedUtc);
            if (!string.IsNullOrWhiteSpace(version?.DeviceName)) label += " · " + version.DeviceName;
            return (group.Key, $"{label} · {entry.ProfileName} · {games} 个游戏");
        }));
        HashSet<string> versions = CheckedPendingFilters(_pendingVersionFilters.Children.OfType<CheckBox>());
        _pendingVersionFilterButton.Content = versions.Count == 0 ? "按备份版本筛选" : $"按备份版本筛选（{versions.Count}）";
        _pendingVersionFilterButton.Visibility = versionGroups.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        HashSet<string> statuses = CheckedPendingFilters(_pendingStatusFilters.Values);
        string search = _pendingSearch.Text.Trim();
        HashSet<Guid> selected = SelectedPendingIds().ToHashSet();
        _pendingList.Items.Clear();
        foreach (var game in references.Where(row =>
                     (statuses.Count == 0 || statuses.Contains(row.Entry.Status.ToString())) &&
                     (versions.Count == 0 || versions.Contains(VersionKey(row.Entry.ProfileId, row.Version))) &&
                     PendingSearchMatches(row.Entry, row.Version, search))
                 .GroupBy(row => row.Entry.Manifest.Game.Id)
                 .OrderBy(group => group.First().Entry.Manifest.Game.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var choices = game.GroupBy(row => row.Version == null
                    ? $"{row.Entry.ProfileId:N}/legacy:{row.Entry.Manifest.ManifestPath}"
                    : VersionKey(row.Entry.ProfileId, row.Version))
                .Select(group =>
                {
                    var (entry, version) = group.First();
                    DateTimeOffset time = version?.CreatedUtc ?? entry.Manifest.CapturedUtc;
                    string label = version == null ? $"旧暂存 · {BackupTime(time)}" : BackupTime(time);
                    if (!string.IsNullOrWhiteSpace(version?.DeviceName)) label += " · " + version.DeviceName;
                    return new PendingVersionChoice(group.Key, $"{label} · {entry.ProfileName}", time, version == null,
                        group.Select(row => row.Entry).DistinctBy(row => row.Id).ToArray());
                })
                .OrderByDescending(version => version.Time).ThenBy(version => version.IsLegacy)
                .ThenBy(version => version.Key, StringComparer.Ordinal).ToArray();
            _pendingList.Items.Add(BuildPendingGameRow(game.First().Entry.Manifest.Game, choices, selected));
        }
        _buildingPendingFilters = false;
        _pendingEmpty.Visibility = _pendingList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _pendingEmpty.Text = entries.Count == 0 ? "暂无待恢复存档。" : "没有符合筛选条件的存档。";
        UpdatePendingSelection();
    }

    private static bool PendingSearchMatches(PendingRestore entry, PendingBackupVersion? version, string search)
    {
        if (search.Length == 0) return true;
        string text = $"{entry.Manifest.Game.Name} {entry.ProfileName} {BackupTime(entry.Manifest.CapturedUtc)} {entry.TargetPath}";
        if (version != null) text += $" {BackupTime(version.CreatedUtc)} {version.DeviceName}";
        return text.Contains(search, StringComparison.CurrentCultureIgnoreCase);
    }

    private ListViewItem BuildPendingGameRow(GameIdentity game, PendingVersionChoice[] choices, HashSet<Guid> selected)
    {
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MaxWidth = 288 });
        var title = Heading(game.Name, "BodyStrongTextBlockStyle");
        title.MaxLines = 2;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(title, game.Name);
        header.Children.Add(title);

        string? chosenKey = _pendingGameVersions.GetValueOrDefault(game.Id);
        PendingVersionChoice active = choices.FirstOrDefault(choice => choice.Key == chosenKey) ?? choices[0];
        var locations = Stack(6);
        FrameworkElement versionControl;
        if (choices.Length > 1)
        {
            var picker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };
            AutomationProperties.SetName(picker, $"为“{game.Name}”选择存档版本");
            foreach (PendingVersionChoice choice in choices)
            {
                var label = SecondaryText(choice.Label);
                label.MaxLines = 1;
                label.TextTrimming = TextTrimming.CharacterEllipsis;
                var item = new ComboBoxItem { Content = label, Tag = choice };
                ToolTipService.SetToolTip(item, choice.Label);
                picker.Items.Add(item);
                if (ReferenceEquals(choice, active)) picker.SelectedItem = item;
            }
            ToolTipService.SetToolTip(picker, active.Label);
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedItem is not ComboBoxItem { Tag: PendingVersionChoice version }) return;
                _pendingGameVersions[game.Id] = version.Key;
                ToolTipService.SetToolTip(picker, version.Label);
                ShowPendingVersion(locations, version, SelectedPendingIds().ToHashSet());
                UpdatePendingSelection();
            };
            versionControl = picker;
        }
        else
        {
            versionControl = SecondaryText(active.Label);
            ToolTipService.SetToolTip(versionControl, active.Label);
        }

        versionControl.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(versionControl, 1);
        header.Children.Add(versionControl);
        ShowPendingVersion(locations, active, selected);
        var content = Stack(8);
        content.Children.Add(header);
        content.Children.Add(locations);
        return new ListViewItem
        {
            Content = Surface(content), Tag = locations, IsTabStop = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 10)
        };
    }

    private void ShowPendingVersion(StackPanel locations, PendingVersionChoice version, HashSet<Guid> selected)
    {
        locations.Children.Clear();
        foreach (PendingRestore entry in version.Locations)
        {
            var content = Stack(4);
            var badges = Row();
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
            SaveLocation? location = entry.Manifest.Locations.FirstOrDefault(l => l.Id == entry.LocationId);
            string? path = entry.TargetPath;
            if (string.IsNullOrWhiteSpace(path) && location != null)
                path = SaveBaseLabel(location.BaseKind) + (string.IsNullOrWhiteSpace(location.RelativePath) ? "" : "\\" + location.RelativePath);
            if (!string.IsNullOrWhiteSpace(path))
                content.Children.Add(PathLine(string.IsNullOrWhiteSpace(entry.TargetPath) ? "备份位置" : "存档", path));
            content.Children.Add(SecondaryText($"采集于 {BackupTime(entry.Manifest.CapturedUtc)} · {SizeLabel(entry.Manifest.Length)}"));
            if (!string.IsNullOrWhiteSpace(entry.Error)) content.Children.Add(SecondaryText(entry.Error));

            var check = new CheckBox
            {
                Content = content, Tag = entry, DataContext = version.Label, IsChecked = selected.Contains(entry.Id),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(check, $"{entry.Manifest.Game.Name} · {StateLabel(entry.Status)} · {path}");
            check.Checked += (_, _) => UpdatePendingSelection();
            check.Unchecked += (_, _) => UpdatePendingSelection();
            locations.Children.Add(check);
        }
    }

    private IEnumerable<CheckBox> PendingLocationChecks() => _pendingList.Items.OfType<ListViewItem>()
        .SelectMany(row => ((StackPanel)row.Tag).Children.OfType<CheckBox>());

    private void SelectPendingLocations(bool selected)
    {
        _buildingPendingFilters = true;
        foreach (CheckBox check in PendingLocationChecks()) check.IsChecked = selected;
        _buildingPendingFilters = false;
        UpdatePendingSelection();
    }

    private void UpdatePendingSelection()
    {
        if (_buildingPendingFilters) return;
        CheckBox[] checks = PendingLocationChecks().ToArray();
        _selectedPending = checks.Where(check => check.IsChecked == true).Select(check => (PendingRestore)check.Tag).ToArray();
        _pendingSelection.Text = $"已选 {_selectedPending.Length} 项 · 显示 {checks.Length} 项";
        _pendingRestoreBlocker = PendingRestoreBlocker(_selectedPending);
        _pendingRestoreHint.Text = _selectedPending.Length == 0
            ? "选择一项可设置目标。"
            : _pendingRestoreBlocker ?? "已选存档可以恢复。";
        RefreshOperationControls();
    }

    private static string? PendingRestoreBlocker(IReadOnlyList<PendingRestore> entries)
    {
        if (entries.Any(entry => entry.IsBusy)) return "所选存档正在使用，请等当前操作完成。";
        if (entries.Any(entry => entry.Status is RestoreStatus.PendingLink or RestoreStatus.PendingPath ||
            !Path.IsPathFullyQualified(entry.TargetPath ?? "")))
            return "请逐项选择缺少目标的存档，再点击“设置所选目标”。";

        try
        {
            if (entries.GroupBy(entry => Path.GetFullPath(entry.TargetPath!), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                return "同一目标选了多个版本，请只保留想恢复的版本。";
        }
        catch (ArgumentException)
        {
            return "所选存档的目标路径无效，请重新设置。";
        }
        return null;
    }

    private Guid[] SelectedPendingIds() => _selectedPending.Select(entry => entry.Id).ToArray();

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
        body.Children.Add(SecondaryText($"存档采集于 {BackupTime(entry.Manifest.CapturedUtc)}"));
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

    private static string VersionKey(Guid profileId, PendingBackupVersion? version) => $"{profileId:N}/{version?.ManifestPath}";
    private static string BackupTime(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    private static string StateLabel(RestoreStatus status) => status switch
    {
        RestoreStatus.PendingLink => "待关联", RestoreStatus.PendingPath => "待指定路径",
        RestoreStatus.Ready => "可恢复", RestoreStatus.Restored => "已恢复", _ => "失败"
    };
}
