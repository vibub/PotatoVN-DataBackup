using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.Backup.Core;

namespace PotatoVN.App.PluginBase.BackupUi;

public sealed partial class BackupSettingsView
{
    private static readonly (BackupContent Flag, string Label)[] ContentChoices =
    [
        (BackupContent.GameInfo, "游戏信息"), (BackupContent.Covers, "游戏图片（封面）"),
        (BackupContent.Reviews, "评价与评论（含游玩状态）"), (BackupContent.PlayTime, "游玩时间与次数"),
        (BackupContent.Characters, "游戏角色及图片"), (BackupContent.Headers, "头图"),
        (BackupContent.LibrariesAndCategories, "游戏库与分类"), (BackupContent.Staff, "制作人员资料及图片"),
        (BackupContent.Home, "主页布局"), (BackupContent.Settings, "常规设置"), (BackupContent.Saves, "游戏存档")
    ];
    private readonly Dictionary<BackupContent, CheckBox> _contentChecks = new();
    private readonly CheckBox _allGames = new() { Content = "备份全部游戏（含非本地）" };
    private readonly ListView _gameSelection = new() { SelectionMode = ListViewSelectionMode.Multiple, IsMultiSelectCheckBoxEnabled = true, MaxHeight = 260 };
    private readonly HashSet<Guid> _selectedGameIds = new();
    private readonly TextBox _gameSearch = TextInput("搜索备份游戏", "游戏名、目录或来源 ID");
    private readonly CheckBox _onlySelectedGames = new() { Content = "只看已选" };
    private readonly StackPanel _manualGameScope = Stack(8);
    private readonly TextBlock _gameSelectionEmpty = Note("");
    private bool _refreshingGameSelection;
    private IReadOnlyList<GameIdentity> _scopeGames = Array.Empty<GameIdentity>();
    private readonly TextBlock _gameScopeNote = Note("");
    private readonly ToggleSwitch _timer = new() { Header = "定时备份", OnContent = "已开启", OffContent = "已关闭" };
    private readonly ToggleSwitch _onExit = new() { Header = "游戏退出后备份", OnContent = "已开启", OffContent = "已关闭" };
    private readonly NumberBox _interval = Number("定时间隔（小时）", .05, 8760, .5);
    private readonly NumberBox _exitDelay = Number("游戏退出后等待（秒）", 0, 3600, 1);
    private readonly NumberBox _keepSnapshots = Number("每台设备保留的软件资料版本数", 1, 10000, 1);
    private readonly NumberBox _keepSaves = Number("每个游戏每台设备保留的存档版本数", 1, 10000, 1);
    private readonly TextBox _deviceName = TextInput("这台设备的名称");

    private StackPanel BuildContentTab()
    {
        var panel = Stack();
        panel.Children.Add(Heading("选择备份内容"));
        panel.Children.Add(Note("手动和自动备份共用以下设置。"));
        var choices = Row();
        foreach ((BackupContent flag, string label) in ContentChoices)
        {
            var check = new CheckBox { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, Width = 264, IsChecked = _controller.Settings.Content.HasFlag(flag) };
            check.Checked += (_, _) => MarkSettingsDirty();
            check.Unchecked += (_, _) => MarkSettingsDirty();
            _contentChecks.Add(flag, check);
            choices.Children.Add(check);
        }
        var selectActions = Row();
        var all = new Button { Content = "全选" };
        all.Click += (_, _) => { foreach (CheckBox check in _contentChecks.Values) check.IsChecked = true; };
        var none = new Button { Content = "全不选" };
        none.Click += (_, _) => { foreach (CheckBox check in _contentChecks.Values) check.IsChecked = false; };
        selectActions.Children.Add(all);
        selectActions.Children.Add(none);
        panel.Children.Add(selectActions);
        panel.Children.Add(choices);
        panel.Children.Add(Heading("游戏范围"));
        _allGames.IsChecked = _controller.Settings.AllGames;
        panel.Children.Add(_allGames);
        _selectedGameIds.UnionWith(_controller.Settings.SelectedGameIds);
        RefreshGameSelection(true);
        UpdateGameScope();
        _allGames.Checked += (_, _) => { UpdateGameScope(); MarkSettingsDirty(); };
        _allGames.Unchecked += (_, _) => { UpdateGameScope(); MarkSettingsDirty(); };
        _gameSelection.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _gameSelection.SelectionChanged += (_, args) =>
        {
            if (_refreshingGameSelection) return;
            bool changed = false;
            foreach (ListViewItem item in args.RemovedItems)
                if (_gameSelection.Items.Contains(item)) changed |= _selectedGameIds.Remove(((GameIdentity)item.Tag).Id);
            foreach (ListViewItem item in args.AddedItems)
                if (_gameSelection.Items.Contains(item)) changed |= _selectedGameIds.Add(((GameIdentity)item.Tag).Id);
            if (!changed) return;
            if (_onlySelectedGames.IsChecked == true) RefreshGameSelection();
            else UpdateGameScope();
            MarkSettingsDirty();
        };
        _gameSearch.TextChanged += (_, _) => RefreshGameSelection();
        _onlySelectedGames.Checked += (_, _) => RefreshGameSelection();
        _onlySelectedGames.Unchecked += (_, _) => RefreshGameSelection();
        _manualGameScope.Children.Add(_gameSearch);
        _manualGameScope.Children.Add(_onlySelectedGames);
        _manualGameScope.Children.Add(_gameSelection);
        _manualGameScope.Children.Add(_gameSelectionEmpty);
        var gameActions = Row();
        var selectFiltered = new Button { Content = "选中筛选结果" };
        selectFiltered.Click += (_, _) => SelectFilteredGames(true);
        var clearFiltered = new Button { Content = "取消筛选结果选择" };
        clearFiltered.Click += (_, _) => SelectFilteredGames(false);
        gameActions.Children.Add(selectFiltered);
        gameActions.Children.Add(clearFiltered);
        _manualGameScope.Children.Add(gameActions);
        panel.Children.Add(_gameScopeNote);
        panel.Children.Add(_manualGameScope);
        UpdateGameScope();
        panel.Children.Add(ActionButton("刷新游戏列表", _ =>
        {
            RefreshGameSelection(true);
            RefreshSaveGameChoices();
            return Task.CompletedTask;
        }));
        panel.Children.Add(Heading("各游戏存档位置"));
        panel.Children.Add(Note("支持添加多个文件夹或文件。"));
        BuildSaveLocationControls(panel);
        return panel;
    }

    private void RefreshGameSelection(bool reload = false)
    {
        if (reload) _scopeGames = _controller.GetGames().ToArray();
        IReadOnlyList<GameIdentity> games = _scopeGames;
        _selectedGameIds.IntersectWith(games.Select(game => game.Id));
        _refreshingGameSelection = true;
        _gameSelection.Items.Clear();
        foreach (GameIdentity game in FilteredScopeGames())
        {
            ListViewItem item = GameListItem(game, System.IO.Directory.Exists(game.LocalDirectory) ? "已关联" : "未关联", System.IO.Directory.Exists(game.LocalDirectory) ? StatusTone.Info : StatusTone.Neutral);
            _gameSelection.Items.Add(item);
            if (_selectedGameIds.Contains(game.Id)) _gameSelection.SelectedItems.Add(item);
        }
        _refreshingGameSelection = false;
        _gameSelectionEmpty.Visibility = _gameSelection.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _gameSelectionEmpty.Text = games.Count == 0 ? "暂无游戏。" : "无匹配游戏。";
        UpdateGameScope();
    }

    private IEnumerable<GameIdentity> FilteredScopeGames() =>
        _scopeGames.Where(game => GameSearch.Matches(_gameSearch.Text, game) &&
            (_onlySelectedGames.IsChecked != true || _selectedGameIds.Contains(game.Id)))
            .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase);

    private void SelectFilteredGames(bool select)
    {
        // 批量操作直接使用当前输入，避免延迟的 TextChanged 通知使其作用于旧列表。
        Guid[] ids = FilteredScopeGames().Select(game => game.Id).ToArray();
        int previous = _selectedGameIds.Count;
        if (select) _selectedGameIds.UnionWith(ids);
        else _selectedGameIds.ExceptWith(ids);
        RefreshGameSelection();
        if (previous != _selectedGameIds.Count) MarkSettingsDirty();
    }

    private void UpdateGameScope()
    {
        bool all = _allGames.IsChecked == true;
        _manualGameScope.Visibility = all ? Visibility.Collapsed : Visibility.Visible;
        _gameSelection.IsEnabled = !all;
        _gameScopeNote.Text = all ? "包含以后新增的游戏" :
            $"已选 {_selectedGameIds.Count} 款游戏，当前显示 {_gameSelection.Items.Count} 款";
    }

    private static ListViewItem GameListItem(GameIdentity game, string state, StatusTone tone = StatusTone.Neutral, bool showUnlinked = false)
    {
        var content = Stack(8);
        var name = Heading(game.Name, "BodyStrongTextBlockStyle");
        name.MaxLines = 2;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTipService.SetToolTip(name, game.Name);
        content.Children.Add(name);
        var badges = Row();
        badges.Children.Add(StatusBadge(state, tone));
        if (showUnlinked) badges.Children.Add(StatusBadge("未关联", StatusTone.Warning));
        content.Children.Add(badges);
        return new ListViewItem { Tag = game, Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 10, 10, 12), Margin = new Thickness(0, 0, 0, 6) };
    }

    private StackPanel BuildAutomationTab()
    {
        var panel = Stack();
        panel.Children.Add(Heading("自动备份"));
        panel.Children.Add(Note("仅在 PotatoVN 运行且插件启用时执行。"));
        _timer.IsOn = _controller.Settings.TimerEnabled;
        _onExit.IsOn = _controller.Settings.OnGameExitEnabled;
        _interval.Value = _controller.Settings.IntervalHours;
        _exitDelay.Value = _controller.Settings.ExitDelaySeconds;
        _keepSnapshots.Value = _controller.Settings.KeepSnapshots;
        _keepSaves.Value = _controller.Settings.KeepSaves;
        _deviceName.Text = _controller.Settings.DeviceName;
        panel.Children.Add(_timer);
        panel.Children.Add(_interval);
        panel.Children.Add(_onExit);
        panel.Children.Add(_exitDelay);
        var advanced = Stack();
        advanced.Children.Add(Note("超过保留数量时清理旧版本，仍被快照引用的存档会保留。"));
        foreach (UIElement item in new UIElement[] { _deviceName, _keepSnapshots, _keepSaves }) advanced.Children.Add(item);
        panel.Children.Add(new Expander { Header = "设备名称与历史保留", Content = advanced, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        _interval.IsEnabled = _timer.IsOn;
        _exitDelay.IsEnabled = _onExit.IsOn;
        _timer.Toggled += (_, _) => { _interval.IsEnabled = _timer.IsOn; MarkSettingsDirty(); };
        _onExit.Toggled += (_, _) => { _exitDelay.IsEnabled = _onExit.IsOn; MarkSettingsDirty(); };
        foreach (NumberBox number in new[] { _interval, _exitDelay, _keepSnapshots, _keepSaves })
            number.ValueChanged += (_, _) => MarkSettingsDirty();
        _deviceName.TextChanged += (_, _) => MarkSettingsDirty();
        panel.Children.Add(Note(_controller.Settings.LastSuccessfulBackupUtc is { } time ? $"最近成功备份：{time.ToLocalTime():yyyy-MM-dd HH:mm:ss}" : "尚未完成备份。"));
        return panel;
    }

    private async Task SaveAllSettingsAsync()
    {
        if (!double.IsFinite(_interval.Value) || _interval.Value < .05 || _interval.Value > 8760)
            throw new InvalidOperationException("定时间隔应为 0.05 至 8760 小时。");
        if (!double.IsFinite(_exitDelay.Value) || _exitDelay.Value < 0 || _exitDelay.Value > 3600)
            throw new InvalidOperationException("退出后等待时间应为 0 至 3600 秒。");
        if (!double.IsFinite(_keepSnapshots.Value) || _keepSnapshots.Value < 1 || _keepSnapshots.Value > 10000 ||
            !double.IsFinite(_keepSaves.Value) || _keepSaves.Value < 1 || _keepSaves.Value > 10000)
            throw new InvalidOperationException("保留版本数应为 1 至 10000。");
        BackupContent content = _contentChecks.Where(pair => pair.Value.IsChecked == true).Aggregate(BackupContent.None, (flags, pair) => flags | pair.Key);
        if (content == BackupContent.None)
            throw new InvalidOperationException("请到“备份内容”至少勾选一种需要备份的内容。");
        if (_allGames.IsChecked != true && _selectedGameIds.Count == 0 &&
            (content & ~(BackupContent.Settings | BackupContent.Home)) != BackupContent.None)
            throw new InvalidOperationException("请到“备份内容”勾选至少一款游戏，或开启“备份全部游戏”。");
        if ((_timer.IsOn || _onExit.IsOn) && _controller.Settings.ActiveProfileId == null && !_profileDirty)
            throw new InvalidOperationException("请先到“连接云盘”填写连接信息并测试连接，再开启自动备份。");
        BackupSettings settings = _controller.Settings;
        var previous = (settings.Content, settings.AllGames, settings.SelectedGameIds, settings.TimerEnabled,
            settings.OnGameExitEnabled, settings.IntervalHours, settings.ExitDelaySeconds,
            settings.KeepSnapshots, settings.KeepSaves, settings.DeviceName);
        settings.Content = content;
        settings.AllGames = _allGames.IsChecked == true;
        settings.SelectedGameIds = _selectedGameIds.ToList();
        _controller.Settings.TimerEnabled = _timer.IsOn;
        _controller.Settings.OnGameExitEnabled = _onExit.IsOn;
        _controller.Settings.IntervalHours = _interval.Value;
        _controller.Settings.ExitDelaySeconds = (int)Math.Round(_exitDelay.Value);
        _controller.Settings.KeepSnapshots = (int)Math.Round(_keepSnapshots.Value);
        _controller.Settings.KeepSaves = (int)Math.Round(_keepSaves.Value);
        _controller.Settings.DeviceName = string.IsNullOrWhiteSpace(_deviceName.Text) ? Environment.MachineName : _deviceName.Text.Trim();
        try
        {
            if (_editingProfile != null && (_profileDirty || settings.Profiles.Contains(_editingProfile))) await SaveProfileFieldsAsync();
            else await _controller.SaveSettingsAsync();
            MarkSettingsSaved();
        }
        catch
        {
            (settings.Content, settings.AllGames, settings.SelectedGameIds, settings.TimerEnabled,
                settings.OnGameExitEnabled, settings.IntervalHours, settings.ExitDelaySeconds,
                settings.KeepSnapshots, settings.KeepSaves, settings.DeviceName) = previous;
            throw;
        }
    }

    private static NumberBox Number(string header, double min, double max, double step) => new()
    {
        Header = header, Minimum = min, Maximum = max, SmallChange = step,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

}
