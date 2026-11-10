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
    private readonly CheckBox _allGames = new() { Content = "备份全部游戏（包含非本地游戏）" };
    private readonly ListView _gameSelection = new() { SelectionMode = ListViewSelectionMode.Multiple, IsMultiSelectCheckBoxEnabled = true, MaxHeight = 260 };
    private readonly HashSet<Guid> _selectedGameIds = new();
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
        panel.Children.Add(Note("手动、定时和游戏退出后备份共用以下勾选。未选的字段和图片不会上传；恢复时可以再次选择类型。"));
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
        panel.Children.Add(Note("常规设置不包含账号密码、授权令牌和机器专属配置。游戏存档仅使用下面确认的位置，不扫描整个游戏目录。"));
        panel.Children.Add(Heading("游戏范围"));
        _allGames.IsChecked = _controller.Settings.AllGames;
        panel.Children.Add(_allGames);
        _selectedGameIds.UnionWith(_controller.Settings.SelectedGameIds);
        RefreshGameSelection();
        UpdateGameScope();
        _allGames.Checked += (_, _) => { UpdateGameScope(); MarkSettingsDirty(); };
        _allGames.Unchecked += (_, _) => { UpdateGameScope(); MarkSettingsDirty(); };
        _gameSelection.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _selectedGameIds.Clear();
            _selectedGameIds.UnionWith(_gameSelection.SelectedItems.OfType<GameChoice>().Select(g => g.Game.Id));
            UpdateGameScope();
            MarkSettingsDirty();
        };
        panel.Children.Add(_gameScopeNote);
        panel.Children.Add(_gameSelection);
        panel.Children.Add(ActionButton("刷新游戏列表", _ =>
        {
            RefreshGameSelection();
            RefreshSaveGameChoices();
            return Task.CompletedTask;
        }));
        panel.Children.Add(Heading("各游戏存档位置"));
        panel.Children.Add(Note("可为每个游戏指定多个位置，支持目录或单个文件。关联游戏前，恢复的存档统一保存在待恢复区。"));
        BuildSaveLocationControls(panel);
        return panel;
    }

    private void RefreshGameSelection()
    {
        _loading = true;
        _selectedGameIds.IntersectWith(_controller.GetGames().Select(game => game.Id));
        _gameSelection.Items.Clear();
        foreach (GameIdentity game in _controller.GetGames().OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var choice = new GameChoice(game);
            _gameSelection.Items.Add(choice);
            if (_selectedGameIds.Contains(game.Id)) _gameSelection.SelectedItems.Add(choice);
        }
        _loading = false;
        UpdateGameScope();
    }

    private void UpdateGameScope()
    {
        bool all = _allGames.IsChecked == true;
        _gameSelection.Visibility = all ? Visibility.Collapsed : Visibility.Visible;
        _gameSelection.IsEnabled = !all;
        _gameScopeNote.Text = all ? "包含所有游戏，之后添加的游戏也会自动纳入。" : $"已选 {_selectedGameIds.Count} 款游戏。请在列表中勾选需要备份的游戏。";
    }

    private StackPanel BuildAutomationTab()
    {
        var panel = Stack();
        panel.Children.Add(Heading("自动备份"));
        panel.Children.Add(Note("自动备份仅在 PotatoVN 运行且插件启用时执行。两个行为可分别开关，共用备份内容与游戏范围。"));
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
        advanced.Children.Add(Note("通常保持默认值即可。超过保留数量后会清理较旧版本，仍被快照引用的存档除外。"));
        foreach (UIElement item in new UIElement[] { _deviceName, _keepSnapshots, _keepSaves }) advanced.Children.Add(item);
        panel.Children.Add(new Expander { Header = "设备名称与历史保留", Content = advanced, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        _interval.IsEnabled = _timer.IsOn;
        _exitDelay.IsEnabled = _onExit.IsOn;
        _timer.Toggled += (_, _) => { _interval.IsEnabled = _timer.IsOn; MarkSettingsDirty(); };
        _onExit.Toggled += (_, _) => { _exitDelay.IsEnabled = _onExit.IsOn; MarkSettingsDirty(); };
        foreach (NumberBox number in new[] { _interval, _exitDelay, _keepSnapshots, _keepSaves })
            number.ValueChanged += (_, _) => MarkSettingsDirty();
        _deviceName.TextChanged += (_, _) => MarkSettingsDirty();
        panel.Children.Add(Note("仍被保留快照引用的存档会延后清理。游戏卸载或本机找不到存档，不会用空存档覆盖云端版本。"));
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

    private sealed record GameChoice(GameIdentity Game)
    {
        public override string ToString() => GameLabel(Game);
    }
}
