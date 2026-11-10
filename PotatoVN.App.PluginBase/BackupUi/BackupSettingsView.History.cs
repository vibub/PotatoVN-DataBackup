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
    private readonly ListView _snapshotList = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 280 };
    private readonly TextBlock _snapshotDetails = Note("点击“读取云端备份”，然后选择需要恢复的版本。");
    private readonly ComboBox _mapBackupGame = new() { Header = "备份中的游戏", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _mapLocalGame = new() { Header = "本机对应的游戏", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _libraryRows = Stack(16);
    private readonly Dictionary<string, string> _libraryTargetDrafts = new();
    private Guid? _historyProfileId;

    private StackPanel BuildHistoryTab()
    {
        var panel = Stack();
        panel.Children.Add(Heading("从云端恢复"));
        panel.Children.Add(Note("1. 读取备份 → 2. 选择版本和内容 → 3. 确认恢复。软件资料恢复需要重启；存档会先下载到“待恢复存档”，由你另行确认写入。"));
        panel.Children.Add(ActionButton("读取云端备份", async ct =>
        {
            if (_controller.Settings.ActiveProfileId == null) throw new InvalidOperationException("请先在“连接云盘”中添加并保存云盘账号。");
            IReadOnlyList<SnapshotManifest> snapshots = await _controller.ListSnapshotsAsync(ct);
            _historyProfileId = _controller.Settings.ActiveProfileId;
            _snapshotList.Items.Clear();
            foreach (SnapshotManifest snapshot in snapshots.OrderByDescending(s => s.CreatedUtc))
                _snapshotList.Items.Add(new SnapshotChoice(snapshot));
            if (_snapshotList.Items.Count > 0) _snapshotList.SelectedIndex = 0;
            else _snapshotDetails.Text = "这个备份位置还没有已完成的备份。可先在本机点击“立即备份”，或检查是否选择了正确的云盘账号。";
        }, successMessage: "云端备份列表已更新。"));
        panel.Children.Add(_snapshotList);
        panel.Children.Add(_snapshotDetails);
        Button restore = ActionButton("选择内容并恢复…", RestoreSelectedSnapshotAsync, successMessage: "所选内容已处理；下载的存档请前往“待恢复存档”继续恢复。");
        restore.Visibility = Visibility.Collapsed;
        panel.Children.Add(restore);

        var mapping = Stack();
        mapping.Children.Add(Note("通常会自动识别对应游戏。只有识别冲突或需要更改对应关系时才需要这里的设置；保存后再执行上方恢复。"));
        mapping.Children.Add(_mapBackupGame);
        mapping.Children.Add(_mapLocalGame);
        var mappingActions = Row();
        mappingActions.Children.Add(ActionButton("保存对应关系", async ct =>
        {
            RequireSnapshot();
            if (Choice(_mapBackupGame) is not Guid backupId || Choice(_mapLocalGame) is not Guid localId)
                throw new InvalidOperationException("请分别选择备份游戏和本机游戏。");
            await _controller.ConfigureGameMappingAsync(backupId, localId, ct);
        }, successMessage: "游戏对应关系已保存。"));
        mappingActions.Children.Add(ActionButton("改为自动识别", async _ =>
        {
            RequireSnapshot();
            if (Choice(_mapBackupGame) is not Guid backupId) throw new InvalidOperationException("请选择备份中的游戏。");
            _controller.Settings.GameMappings.RemoveAll(item => item.BackupGameId == backupId);
            await _controller.SaveSettingsAsync();
            RefreshGameMappingChoices();
        }, successMessage: "已移除手动对应关系，后续恢复将自动识别。"));
        mappingActions.Children.Add(ActionButton("刷新本机游戏", _ => { RefreshGameMappingChoices(); return Task.CompletedTask; }, successMessage: "本机游戏列表已更新。"));
        mapping.Children.Add(mappingActions);
        panel.Children.Add(new Expander { Header = "高级：手动指定游戏对应关系", Content = mapping, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        _snapshotList.SelectionChanged += (_, _) =>
        {
            restore.Visibility = _snapshotList.SelectedItem is SnapshotChoice ? Visibility.Visible : Visibility.Collapsed;
            if (_snapshotList.SelectedItem is SnapshotChoice row)
            {
                SnapshotManifest snapshot = row.Snapshot;
                string types = string.Join("、", ContentChoices.Where(choice => snapshot.Content.HasFlag(choice.Flag)).Select(choice => choice.Label));
                _snapshotDetails.Text = $"包含：{types}\n{snapshot.Games.Count} 个游戏 · {snapshot.SaveManifestPaths.Count} 份游戏存档";
                if (snapshot.Notices.Count > 0) _snapshotDetails.Text += "\n" + string.Join("\n", snapshot.Notices);
            }
            else _snapshotDetails.Text = "请选择一个备份版本。切换云盘后需要重新读取备份列表。";
            RefreshGameMappingChoices();
        };
        _mapBackupGame.SelectionChanged += (_, _) => SelectSavedGameMapping();
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
            Message = "软件资料恢复需要重启。游戏存档只下载到待恢复存档，确认对应游戏和目标位置后再手动写入。" };
        body.Children.Add(hint);
        if (await DialogAsync("选择恢复内容", new ScrollViewer { Content = body, MaxHeight = 470 }, "确认并继续") != ContentDialogResult.Primary)
        { ShowMessage("已取消恢复。", InfoBarSeverity.Informational); return; }
        BackupContent content = checks.Where(c => c.Check.IsChecked == true).Aggregate(BackupContent.None, (flags, item) => flags | item.Flag);
        if (content == BackupContent.None) throw new InvalidOperationException("请至少选择一种恢复内容。");
        var matching = await ConfirmRestoreMatchesAsync(snapshot, content, ct);
        if (!matching.Continue) { ShowMessage("已取消恢复。", InfoBarSeverity.Informational); return; }
        await _controller.RestoreSnapshotAsync(snapshot, content, ct, matching.Matching);
        await _controller.RefreshPendingAsync(ct);
    }

    private SnapshotManifest RequireSnapshot()
    {
        if (_historyProfileId != _controller.Settings.ActiveProfileId)
            throw new InvalidOperationException("备份位置已切换，请重新读取云端备份。");
        return (_snapshotList.SelectedItem as SnapshotChoice)?.Snapshot ?? throw new InvalidOperationException("请先读取云端备份，再选择一个版本。");
    }

    private void RefreshGameMappingChoices()
    {
        object? old = Choice(_mapBackupGame);
        _mapBackupGame.Items.Clear();
        if (_snapshotList.SelectedItem is SnapshotChoice row)
            foreach (GameIdentity game in row.Snapshot.Games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
                AddChoice(_mapBackupGame, game.Id, game.Name + (game.SourceIds.Count > 0 ? " · " + string.Join(" / ", game.SourceIds.Select(p => $"{p.Key}: {p.Value}")) : ""));
        SelectChoice(_mapBackupGame, old);
        if (_mapBackupGame.SelectedItem == null && _mapBackupGame.Items.Count > 0) _mapBackupGame.SelectedIndex = 0;
        _mapLocalGame.Items.Clear();
        foreach (GameIdentity game in _controller.GetGames().OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
            AddChoice(_mapLocalGame, game.Id, GameLabel(game));
        SelectSavedGameMapping();
    }

    private void SelectSavedGameMapping()
    {
        if (Choice(_mapBackupGame) is not Guid backupId) return;
        GameMatchMapping? mapping = _controller.Settings.GameMappings.FirstOrDefault(m => m.BackupGameId == backupId);
        SelectChoice(_mapLocalGame, mapping?.LocalGameId);
    }

    private StackPanel BuildLibraryTab()
    {
        var panel = Stack();
        panel.Children.Add(Heading("关联这台电脑的游戏库"));
        panel.Children.Add(Note("换电脑恢复后，选择旧游戏库在这台电脑上的对应文件夹。关联完成后，存档仍需在“待恢复存档”手动恢复。"));
        panel.Children.Add(_libraryRows);
        panel.Children.Add(ActionButton("刷新游戏库列表", _ => { RefreshLibraryView(); return Task.CompletedTask; }, successMessage: "游戏库列表已更新。"));
        RefreshLibraryView();
        return panel;
    }

    private void RefreshLibraryView()
    {
        if (_libraryRows == null) return;
        _libraryRows.Children.Clear();
        if (_controller.Settings.LibraryMappings.Count == 0)
        {
            _libraryRows.Children.Add(Note("暂无需要关联的游戏库。换电脑恢复软件资料后，旧设备的游戏库会出现在这里。"));
            return;
        }
        foreach (PendingLibraryMapping mapping in _controller.Settings.LibraryMappings)
        {
            var group = Stack(8);
            group.Children.Add(Heading(mapping.Name, "BodyStrongTextBlockStyle"));
            group.Children.Add(Note($"旧设备位置：{mapping.OriginalPath}"));
            var target = TextInput("这台电脑上的对应文件夹", "点击浏览选择，或粘贴文件夹路径");
            target.Text = _libraryTargetDrafts.GetValueOrDefault(mapping.Id) ?? mapping.TargetPath ?? "";
            target.TextChanged += (_, _) => _libraryTargetDrafts[mapping.Id] = target.Text;
            group.Children.Add(target);
            var actions = Row();
            Button browse = BrowsePathButton(target);
            browse.IsEnabled = _operationCts == null && !_controller.IsBusy;
            actions.Children.Add(browse);
            var apply = new Button { Content = "确认并关联此游戏库…", IsEnabled = _operationCts == null && !_controller.IsBusy };
            apply.Click += async (_, _) => await RunAsync(async ct =>
            {
                string path = target.Text.Trim();
                if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
                    throw new InvalidOperationException("请选择这台电脑上已存在的游戏库文件夹。");
                if (await DialogAsync("确认游戏库对应位置", Note($"将“{mapping.Name}”关联到：\n{path}\n\n只会关联该文件夹内可确认的游戏；存档仍需在待恢复存档手动恢复。"), "关联此游戏库") != ContentDialogResult.Primary)
                { ShowMessage("未更改游戏库关联。", InfoBarSeverity.Informational); return; }
                await _controller.ApplyLibraryMappingAsync(mapping.Id, path, ct);
                _libraryTargetDrafts.Remove(mapping.Id);
            }, successMessage: "游戏库关联已完成，存档请在待恢复存档页确认恢复。");
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
