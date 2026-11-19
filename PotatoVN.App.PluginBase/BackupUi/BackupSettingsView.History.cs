using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.Backup.Core;
using PotatoVN.Backup.Core.Engine;

namespace PotatoVN.App.PluginBase.BackupUi;

public sealed partial class BackupSettingsView
{
    private readonly ListView _snapshotList = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 280 };
    private readonly TextBlock _snapshotDetails = Note("读取云端备份后选择版本。");
    private readonly GameSearchPicker _mapBackupGame = new("云端游戏");
    private readonly CheckBox _cloudOnlySaves = new() { Content = "含存档" };
    private readonly CheckBox _cloudOnlyUnmatched = new() { Content = "未匹配" };
    private readonly TextBlock _mappingPreview = Note("");
    private readonly TextBlock _snapshotNotices = Note("");
    private readonly Expander _snapshotNoticeExpander = new() { Header = "备份提示", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly GameSearchPicker _mapLocalGame = new("本机游戏");
    private const int LibraryPageIndex = 5;
    private readonly StackPanel _libraryRows = Stack(16);
    private readonly Dictionary<string, (string Path, Guid? LibraryId)> _libraryTargetDrafts = new();
    private Guid? _historyProfileId;
    private Dictionary<Guid, Guid> _historyTargets = new();

    private StackPanel BuildHistoryTab()
    {
        var panel = Stack();
        panel.Children.Add(Heading("从云端恢复"));
        panel.Children.Add(Note("软件资料恢复需要重启；存档下载后在“待恢复存档”确认写入。"));
        panel.Children.Add(ActionButton("读取云端备份", async ct =>
        {
            if (_controller.Settings.ActiveProfileId == null) throw new InvalidOperationException("请先在“连接云盘”中添加并保存云盘账号。");
            IReadOnlyList<SnapshotManifest> snapshots = await _controller.ListSnapshotsAsync(ct);
            _historyProfileId = _controller.Settings.ActiveProfileId;
            _snapshotList.Items.Clear();
            foreach (SnapshotManifest snapshot in snapshots.OrderByDescending(s => s.CreatedUtc))
                _snapshotList.Items.Add(new SnapshotChoice(snapshot));
            if (_snapshotList.Items.Count > 0) _snapshotList.SelectedIndex = 0;
            else
            {
                _snapshotDetails.Text = "此云盘暂无备份。";
                _snapshotNoticeExpander.Visibility = Visibility.Collapsed;
                RefreshGameMappingChoices();
            }
        }));
        panel.Children.Add(_snapshotList);
        panel.Children.Add(_snapshotDetails);
        _snapshotNoticeExpander.Content = new ScrollViewer { Content = _snapshotNotices, MaxHeight = 160 };
        _snapshotNoticeExpander.Visibility = Visibility.Collapsed;
        panel.Children.Add(_snapshotNoticeExpander);
        Button restore = ActionButton("选择内容并恢复…", RestoreSelectedSnapshotAsync, successMessage: "所选内容已处理。");
        restore.Visibility = Visibility.Collapsed;
        panel.Children.Add(restore);

        var mapping = Stack();
        mapping.Children.Add(Note("先选左侧云端游戏，再选右侧本机游戏，保存对应关系。"));
        _mapBackupGame.Filters.Children.Add(_cloudOnlySaves);
        _mapBackupGame.Filters.Children.Add(_cloudOnlyUnmatched);
        _mapBackupGame.Filters.MinHeight = _mapLocalGame.Filters.MinHeight = 36;
        _mapBackupGame.Results.MaxHeight = _mapLocalGame.Results.MaxHeight = 320;
        mapping.Children.Add(ResponsiveColumns(_mapBackupGame, _mapLocalGame));
        mapping.Children.Add(_mappingPreview);
        _mapBackupGame.Filter = item =>
        {
            if (item.Id is not Guid id || _snapshotList.SelectedItem is not SnapshotChoice selected) return false;
            return (_cloudOnlySaves.IsChecked != true || selected.Snapshot.SaveGames.ContainsKey(id)) &&
                (_cloudOnlyUnmatched.IsChecked != true || !_historyTargets.ContainsKey(id));
        };
        foreach (CheckBox check in new[] { _cloudOnlySaves, _cloudOnlyUnmatched })
        {
            check.Checked += (_, _) => _mapBackupGame.Refresh();
            check.Unchecked += (_, _) => _mapBackupGame.Refresh();
        }
        var mappingActions = Row();
        mappingActions.Children.Add(ActionButton("保存对应关系", async ct =>
        {
            RequireSnapshot();
            if (_mapBackupGame.HasPendingQuery || _mapLocalGame.HasPendingQuery)
                throw new InvalidOperationException("请从搜索结果中确认对应游戏。");
            if (_mapBackupGame.SelectedId is not Guid backupId || _mapLocalGame.SelectedId is not Guid localId)
                throw new InvalidOperationException("请分别选择备份游戏和本机游戏。");
            await _controller.ConfigureGameMappingAsync(backupId, localId, ct);
            RefreshGameMappingChoices();
        }, successMessage: "游戏对应关系已保存。"));
        mappingActions.Children.Add(ActionButton("改为自动识别", async _ =>
        {
            RequireSnapshot();
            if (_mapBackupGame.HasPendingQuery || _mapBackupGame.SelectedId is not Guid backupId)
                throw new InvalidOperationException("请从搜索结果中选择备份中的游戏。");
            _controller.Settings.GameMappings.RemoveAll(item => item.BackupGameId == backupId);
            await _controller.SaveSettingsAsync();
            RefreshGameMappingChoices();
        }, successMessage: "已改为自动识别。"));
        mappingActions.Children.Add(ActionButton("刷新本机游戏", _ => { RefreshGameMappingChoices(); return Task.CompletedTask; }));
        mapping.Children.Add(mappingActions);
        panel.Children.Add(Heading("游戏与存档"));
        panel.Children.Add(mapping);
        _snapshotList.SelectionChanged += (_, _) =>
        {
            restore.Visibility = _snapshotList.SelectedItem is SnapshotChoice ? Visibility.Visible : Visibility.Collapsed;
            if (_snapshotList.SelectedItem is SnapshotChoice row)
            {
                SnapshotManifest snapshot = row.Snapshot;
                string types = string.Join("、", ContentChoices.Where(choice => snapshot.Content.HasFlag(choice.Flag)).Select(choice => choice.Label));
                _snapshotDetails.Text = $"包含：{types}\n{snapshot.Games.Count} 款游戏 · {snapshot.SaveGames.Count} 款含存档 · {snapshot.SaveManifestPaths.Count} 份存档";
                _snapshotNotices.Text = string.Join("\n", snapshot.Notices);
                _snapshotNoticeExpander.Visibility = snapshot.Notices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                _snapshotNoticeExpander.IsExpanded = false;
            }
            else
            {
                _snapshotDetails.Text = "请选择备份版本。";
                _snapshotNoticeExpander.Visibility = Visibility.Collapsed;
            }
            RefreshGameMappingChoices();
        };
        _mapBackupGame.SelectionChanged += (_, _) => SelectSavedGameMapping();
        _mapLocalGame.SelectionChanged += (_, _) => UpdateMappingPreview();
        return panel;
    }

    private async Task RestoreSelectedSnapshotAsync(CancellationToken ct)
    {
        SnapshotManifest snapshot = RequireSnapshot();
        var body = Stack(8);
        body.Children.Add(Note($"版本：{snapshot.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {snapshot.DeviceName}"));
        body.Children.Add(Note("选择要恢复的内容。未勾选的内容及本机有效的游戏目录关联会保留。"));
        var checks = new List<(BackupContent Flag, CheckBox Check)>();
        foreach ((BackupContent flag, string label) in ContentChoices)
        {
            bool available = snapshot.Content.HasFlag(flag);
            var check = new CheckBox { Content = available ? label : $"{label}（此版本未包含）", IsChecked = available, IsEnabled = available };
            checks.Add((flag, check));
            body.Children.Add(check);
        }
        var hint = new InfoBar { Severity = InfoBarSeverity.Informational, IsClosable = false, IsOpen = true,
            Message = "软件资料恢复需要重启；存档下载后需另行确认写入。" };
        body.Children.Add(hint);
        if (await DialogAsync("选择恢复内容", new ScrollViewer { Content = body, MaxHeight = 470 }, "确认并继续") != ContentDialogResult.Primary)
            return;
        BackupContent content = checks.Where(c => c.Check.IsChecked == true).Aggregate(BackupContent.None, (flags, item) => flags | item.Flag);
        if (content == BackupContent.None) throw new InvalidOperationException("请至少选择一种恢复内容。");
        var matching = await ConfirmRestoreMatchesAsync(snapshot, content, ct);
        if (!matching.Continue) return;
        await _controller.RestoreSnapshotAsync(snapshot, content, ct, matching.Matching);
        await _controller.RefreshPendingAsync(ct);
    }

    private SnapshotManifest RequireSnapshot()
    {
        if (_historyProfileId != _controller.Settings.ActiveProfileId)
            throw new InvalidOperationException("备份位置已切换，请重新读取云端备份。");
        return (_snapshotList.SelectedItem as SnapshotChoice)?.Snapshot ?? throw new InvalidOperationException("请先读取云端备份，再选择一个版本。");
    }

    private Dictionary<Guid, Guid> MappingTargets(SnapshotManifest snapshot) =>
        PotatoVN.Backup.Core.Engine.RestoreMatchingPreview.Analyze(snapshot.Games, _controller.GetGames(), _controller.Settings.GameMappings)
            .AutomaticTargets.ToDictionary(pair => pair.Key, pair => pair.Value);

    private void RefreshGameMappingChoices()
    {
        Guid? previous = _mapBackupGame.SelectedId;
        SnapshotManifest? snapshot = (_snapshotList.SelectedItem as SnapshotChoice)?.Snapshot;
        var cloud = snapshot?.Games ?? new List<GameIdentity>();
        Dictionary<Guid, Guid> targets = _historyTargets = snapshot == null ? new() : MappingTargets(snapshot);
        _mapBackupGame.SetItems(cloud.OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase).Select(game =>
        {
            bool saved = snapshot!.SaveGames.TryGetValue(game.Id, out DateTimeOffset captured);
            string status = !snapshot.Content.HasFlag(BackupContent.Saves) ? "未包含存档" : saved ? "含存档" : "无存档";
            string detail = saved ? $"存档时间：{captured.ToLocalTime():yyyy-MM-dd HH:mm}" : status;
            return GameSearchItem.FromCloudGame(game) with { Badge = status, MatchBadge = targets.ContainsKey(game.Id) ? "已匹配" : "未匹配",
                Tone = saved ? StatusTone.Success : StatusTone.Neutral, Detail = detail };
        }), cloud.Any(game => game.Id == previous) ? previous : cloud.FirstOrDefault()?.Id);
        _mapLocalGame.SetItems(_controller.GetGames().OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase).Select(game =>
        {
            var item = GameSearchItem.FromGame(game);
            GameIdentity? owner = cloud.FirstOrDefault(candidate => targets.GetValueOrDefault(candidate.Id) == game.Id);
            return owner == null ? item : item with { MatchBadge = "已匹配", Detail = "当前对应：" + owner.Name };
        }), null);
        SelectSavedGameMapping();
    }

    private void SelectSavedGameMapping()
    {
        if (_mapBackupGame.SelectedId is not Guid backupId || _snapshotList.SelectedItem is not SnapshotChoice row) return;
        Dictionary<Guid, Guid> targets = MappingTargets(row.Snapshot);
        _mapLocalGame.SelectedId = targets.TryGetValue(backupId, out Guid local) ? local : null;
        UpdateMappingPreview();
    }

    private void UpdateMappingPreview()
    {
        if (_mapBackupGame.SelectedId is not Guid backupId || _snapshotList.SelectedItem is not SnapshotChoice row)
        { _mappingPreview.Text = "读取备份后选择云端游戏。"; return; }
        string cloud = row.Snapshot.Games.First(game => game.Id == backupId).Name;
        string? local = _controller.GetGames().FirstOrDefault(game => game.Id == _mapLocalGame.SelectedId)?.Name;
        _mappingPreview.Text = local == null ? $"{cloud} → 尚未选择本机游戏" : $"{cloud} → {local}";
    }

    private StackPanel BuildLibraryTab()
    {
        var panel = Stack();
        panel.Children.Add(Heading("关联这台电脑的游戏库"));
        panel.Children.Add(Note("选择本机游戏库，或指定对应的文件夹。"));
        panel.Children.Add(_libraryRows);
        panel.Children.Add(ActionButton("刷新游戏库列表", _ => { RefreshLibraryView(); return Task.CompletedTask; }));
        return panel;
    }

    private void RefreshLibraryView()
    {
        if (_navigation.SelectedIndex != LibraryPageIndex) return;
        _libraryRows.Children.Clear();
        if (_controller.Settings.LibraryMappings.Count == 0)
        {
            _libraryRows.Children.Add(Note("暂无需要关联的游戏库。"));
            return;
        }
        IReadOnlyList<LocalLibrary> libraries = _controller.GetLocalLibraries();
        bool enabled = _operationCts == null && !_controller.IsBusy;
        foreach (PendingLibraryMapping mapping in _controller.Settings.LibraryMappings)
        {
            LibraryKind? kind = LibraryAssociation.GetKind(JsonNode.Parse(mapping.SourceJson)!.AsObject());
            LocalLibrary[] choices = libraries.Where(library => library.Kind == kind).ToArray();
            LocalLibrary? current = mapping.TargetLibraryId is { } id
                ? choices.FirstOrDefault(library => library.Id == id)
                : choices.FirstOrDefault(library => LibraryAssociation.SamePath(library.Path, mapping.TargetPath));
            var group = Stack(8);
            group.Children.Add(Heading(mapping.Name, "BodyStrongTextBlockStyle"));
            group.Children.Add(StatusBadge(current != null ? "已关联" : "待关联",
                current != null ? StatusTone.Success : StatusTone.Warning));
            group.Children.Add(PathLine("旧设备位置", mapping.OriginalPath, wrap: true));

            var (draftPath, draftLibraryId) = _libraryTargetDrafts.GetValueOrDefault(mapping.Id,
                (current?.Path ?? mapping.TargetPath ?? "", current?.Id));
            var target = TextInput("本机文件夹", "浏览或输入文件夹路径");
            target.Text = draftPath;
            target.IsEnabled = enabled;
            var picker = new ComboBox
            {
                Header = "本机游戏库", PlaceholderText = choices.Length == 0 ? "暂无可用游戏库" : "选择现有游戏库",
                ItemsSource = choices, IsEnabled = enabled && choices.Length > 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedItem = choices.FirstOrDefault(library => library.Id == draftLibraryId)
            };
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedItem is not LocalLibrary library) return;
                target.Text = library.Path;
                _libraryTargetDrafts[mapping.Id] = (library.Path, library.Id);
            };
            target.TextChanged += (_, _) =>
            {
                var library = picker.SelectedItem as LocalLibrary;
                if (library != null && !string.Equals(library.Path, target.Text, StringComparison.OrdinalIgnoreCase))
                {
                    picker.SelectedItem = null;
                    library = null;
                }
                _libraryTargetDrafts[mapping.Id] = (target.Text, library?.Id);
            };
            group.Children.Add(picker);
            group.Children.Add(target);
            var actions = Row();
            Button browse = BrowsePathButton(target);
            browse.IsEnabled = enabled;
            actions.Children.Add(browse);
            var apply = new Button { Content = "确认并关联此游戏库…", IsEnabled = enabled && kind != null };
            apply.Click += async (_, _) => await RunAsync(async ct =>
            {
                var library = picker.SelectedItem as LocalLibrary;
                string path = library?.Path ?? target.Text.Trim();
                if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
                    throw new InvalidOperationException("请选择这台电脑上已存在的游戏库文件夹。");
                string destination = library == null ? path : $"{library.Name}\n{path}";
                if (await DialogAsync("确认游戏库对应位置",
                    Note($"将“{mapping.Name}”关联到：\n{destination}\n\n关联即时生效。存档可在“待恢复存档”中手动恢复。"),
                    "关联此游戏库") != ContentDialogResult.Primary) return;
                await _controller.ApplyLibraryMappingAsync(mapping.Id, path, library?.Id, ct);
                _libraryTargetDrafts.Remove(mapping.Id);
                ShowMessage(_controller.Status, InfoBarSeverity.Success);
            }, showSuccess: false);
            actions.Children.Add(apply);
            group.Children.Add(actions);
            _libraryRows.Children.Add(group);
        }
    }

    private sealed record SnapshotChoice(SnapshotManifest Snapshot)
    {
        public override string ToString() => $"{Snapshot.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {Snapshot.DeviceName} · {Snapshot.Games.Count} 个游戏";
    }
}
