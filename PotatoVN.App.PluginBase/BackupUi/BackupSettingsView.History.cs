using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.Backup.Core;

namespace PotatoVN.App.PluginBase.BackupUi;

public sealed partial class BackupSettingsView
{
    private readonly ListView _snapshotList = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 340 };
    private readonly TextBlock _snapshotDetails = Note("刷新历史后选择一个版本。");
    private readonly ComboBox _mapBackupGame = new() { Header = "备份中的游戏", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _mapLocalGame = new() { Header = "本机对应条目", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _libraryRows = Stack(16);
    private Guid? _historyProfileId;

    private StackPanel BuildHistoryTab()
    {
        var panel = Stack();
        panel.Children.Add(Heading("云端历史版本"));
        panel.Children.Add(Note("显示当前渠道中各设备的已完成备份。资料恢复需重启；存档先进入待恢复区，不会直接写入游戏位置。"));
        var actions = Row();
        actions.Children.Add(ActionButton("刷新历史", async ct =>
        {
            if (_controller.Settings.ActiveProfileId == null) throw new InvalidOperationException("请先在渠道页设置当前渠道。");
            IReadOnlyList<SnapshotManifest> snapshots = await _controller.ListSnapshotsAsync(ct);
            _historyProfileId = _controller.Settings.ActiveProfileId;
            _snapshotList.Items.Clear();
            foreach (SnapshotManifest snapshot in snapshots.OrderByDescending(s => s.CreatedUtc))
                _snapshotList.Items.Add(new SnapshotChoice(snapshot));
            _snapshotDetails.Text = snapshots.Count == 0 ? "当前渠道还没有已完成的备份。" : $"找到 {snapshots.Count} 个版本，请选择后恢复。";
        }));
        actions.Children.Add(ActionButton("选择内容并恢复", async ct =>
        {
            SnapshotManifest snapshot = RequireSnapshot();
            var body = Stack(8);
            body.Children.Add(Note($"版本：{snapshot.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {snapshot.DeviceName}"));
            body.Children.Add(Note("选择要恢复的类型。未选内容及现有本地目录关联会保留；软件资料恢复需要重启，存档只下载至待恢复区。"));
            var checks = new List<(BackupContent Flag, CheckBox Check)>();
            foreach ((BackupContent flag, string label) in ContentChoices)
            {
                bool available = snapshot.Content.HasFlag(flag);
                var check = new CheckBox { Content = available ? label : $"{label}（此版本未包含）", IsChecked = available, IsEnabled = available };
                checks.Add((flag, check));
                body.Children.Add(check);
            }
            if (await DialogAsync("恢复备份", new ScrollViewer { Content = body, MaxHeight = 470 }, "恢复所选内容") != ContentDialogResult.Primary) return;
            BackupContent content = checks.Where(c => c.Check.IsChecked == true).Aggregate(BackupContent.None, (flags, item) => flags | item.Flag);
            if (content == BackupContent.None) throw new InvalidOperationException("请至少选择一种恢复内容。");
            var matching = await ConfirmRestoreMatchesAsync(snapshot, content, ct);
            if (!matching.Continue) return;
            await _controller.RestoreSnapshotAsync(snapshot, content, ct, matching.Matching);
            await _controller.RefreshPendingAsync(ct);
        }));
        panel.Children.Add(actions);
        panel.Children.Add(_snapshotList);
        panel.Children.Add(_snapshotDetails);
        panel.Children.Add(Heading("游戏对应关系"));
        panel.Children.Add(Note("通常按游戏标识自动匹配。存在同名条目、识别冲突或需要指定对应关系时，在这里手动确认，再执行恢复。"));
        panel.Children.Add(_mapBackupGame);
        panel.Children.Add(_mapLocalGame);
        var mappingActions = Row();
        mappingActions.Children.Add(ActionButton("保存对应关系", async ct =>
        {
            _ = RequireSnapshot();
            if (Choice(_mapBackupGame) is not Guid backupId || Choice(_mapLocalGame) is not Guid localId)
                throw new InvalidOperationException("请分别选择备份游戏和本机条目。");
            await _controller.ConfigureGameMappingAsync(backupId, localId, ct);
        }));
        mappingActions.Children.Add(ActionButton("恢复自动识别", async _ =>
        {
            if (Choice(_mapBackupGame) is not Guid backupId) throw new InvalidOperationException("请选择备份中的游戏。");
            _controller.Settings.GameMappings.RemoveAll(mapping => mapping.BackupGameId == backupId);
            await _controller.SaveSettingsAsync();
            RefreshGameMappingChoices();
        }));
        mappingActions.Children.Add(ActionButton("刷新本机条目", _ => { RefreshGameMappingChoices(); return Task.CompletedTask; }));
        panel.Children.Add(mappingActions);
        _snapshotList.SelectionChanged += (_, _) =>
        {
            if (_snapshotList.SelectedItem is SnapshotChoice row)
            {
                SnapshotManifest snapshot = row.Snapshot;
                string types = string.Join("、", ContentChoices.Where(choice => snapshot.Content.HasFlag(choice.Flag)).Select(choice => choice.Label));
                _snapshotDetails.Text = $"包含：{types}\n游戏：{snapshot.Games.Count} 个；存档版本引用：{snapshot.SaveManifestPaths.Count} 个";
                if (snapshot.Notices.Count > 0) _snapshotDetails.Text += "\n" + string.Join("\n", snapshot.Notices);
            }
            RefreshGameMappingChoices();
        };
        _mapBackupGame.SelectionChanged += (_, _) => SelectSavedGameMapping();
        return panel;
    }

    private SnapshotManifest RequireSnapshot()
    {
        if (_historyProfileId != _controller.Settings.ActiveProfileId)
            throw new InvalidOperationException("当前渠道已切换，请重新刷新历史版本。");
        return (_snapshotList.SelectedItem as SnapshotChoice)?.Snapshot ?? throw new InvalidOperationException("请选择一个历史版本。");
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
        panel.Children.Add(Heading("恢复后的库路径"));
        panel.Children.Add(Note("旧设备的路径只作为参考。指定这台设备上的对应游戏库后，才建立本地目录关联；不会因为盘符或目录名称相同就自动关联。"));
        panel.Children.Add(_libraryRows);
        panel.Children.Add(ActionButton("刷新待映射库", _ => { RefreshLibraryView(); return Task.CompletedTask; }));
        RefreshLibraryView();
        return panel;
    }

    private void RefreshLibraryView()
    {
        if (_libraryRows == null) return;
        _libraryRows.Children.Clear();
        if (_controller.Settings.LibraryMappings.Count == 0)
        {
            _libraryRows.Children.Add(Note("没有待映射的游戏库。新设备恢复资料后，原库路径会显示在这里。"));
            return;
        }
        foreach (PendingLibraryMapping mapping in _controller.Settings.LibraryMappings)
        {
            var group = Stack(8);
            group.Children.Add(Heading(mapping.Name, "BodyStrongTextBlockStyle"));
            group.Children.Add(Note($"原位置：{mapping.OriginalPath}"));
            var target = TextInput("本机游戏库目录", "例如：E:\\VisualNovels");
            target.Text = mapping.TargetPath ?? "";
            group.Children.Add(target);
            var apply = new Button { Content = "确认并关联此库", IsEnabled = _operationCts == null && !_controller.IsBusy };
            apply.Click += async (_, _) => await RunAsync(async ct =>
            {
                string path = target.Text.Trim();
                if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
                    throw new InvalidOperationException("请选择本机已存在的游戏库绝对目录。");
                if (await DialogAsync("确认游戏库对应位置", Note($"将“{mapping.Name}”映射到：\n{path}\n\n关联后仍需在待恢复区手动恢复存档。"), "关联此库") != ContentDialogResult.Primary) return;
                await _controller.ApplyLibraryMappingAsync(mapping.Id, path, ct);
            });
            group.Children.Add(apply);
            _libraryRows.Children.Add(group);
        }
    }

    private sealed record SnapshotChoice(SnapshotManifest Snapshot)
    {
        public override string ToString() => $"{Snapshot.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {Snapshot.DeviceName} · {Snapshot.Games.Count} 个游戏";
    }
}
