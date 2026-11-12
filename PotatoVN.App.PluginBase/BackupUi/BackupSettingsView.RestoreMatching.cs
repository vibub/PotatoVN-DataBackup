using System;
using System.Collections.Generic;
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
    private async Task<(bool Continue, RestoreMatchingDecisions? Matching)> ConfirmRestoreMatchesAsync(
        SnapshotManifest snapshot, BackupContent content, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!RestoreMatchingPreview.RequiresMatching(content)) return (true, null);
        var analysis = RestoreMatchingPreview.Analyze(snapshot.Games, _controller.GetGames(), _controller.Settings.GameMappings);
        if (!analysis.NeedsPrompt) return (true, RestoreMatchingPreview.Decide(analysis, new Dictionary<Guid, Guid?>()));
        var panel = Stack(12);
        panel.Children.Add(Note("以下本地游戏没有明确的云端对应关系。为每个游戏选择对应项，或者保留“不关联”。已明确匹配的游戏不在此列表中。"));
        panel.Children.Add(Note("未关联的本地条目保持原样。未被选中的云端游戏，勾选“游戏信息”时会恢复为非本地资料；存档进入待恢复区。"));
        var rows = new List<(Guid LocalId, GameSearchPicker Choice)>();
        var options = new List<GameSearchItem> { new(null, "不关联云端", "保留本地条目") };
        foreach (GameIdentity cloud in analysis.CloudGames.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            string label = GameSearchItem.FromCloudGame(cloud).Detail;
            if (analysis.AutomaticTargets.TryGetValue(cloud.Id, out Guid occupied))
                label += " · 已对应本地：" + analysis.LocalGames.Single(g => g.Id == occupied).Name;
            options.Add(GameSearchItem.FromCloudGame(cloud) with { Detail = label,
                Badge = analysis.AutomaticTargets.ContainsKey(cloud.Id) ? "已匹配" : "未匹配",
                Tone = analysis.AutomaticTargets.ContainsKey(cloud.Id) ? StatusTone.Info : StatusTone.Warning });
        }
        foreach (GameIdentity local in analysis.UnmatchedLocalGames.OrderByDescending(g => g.LocalDirectory != null)
                     .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var group = Stack(4);
            group.Children.Add(GameEntryContent(local, System.IO.Directory.Exists(local.LocalDirectory) ? "已关联" : "未关联",
                System.IO.Directory.Exists(local.LocalDirectory) ? StatusTone.Info : StatusTone.Warning));
            var choice = new GameSearchPicker("对应的云端游戏");
            choice.SetItems(options, null);
            group.Children.Add(choice); panel.Children.Add(group); rows.Add((local.Id, choice));
        }
        foreach (var row in rows)
            row.Choice.SelectionChanged += (_, _) =>
            {
                foreach (var target in rows)
                    target.Choice.UpdateDetails(item =>
                    {
                        string detail = options.First(option => option.Id == item.Id).Detail;
                        var occupied = rows.FirstOrDefault(other => other.LocalId != target.LocalId && other.Choice.SelectedId == item.Id);
                        return item.Id != null && occupied.Choice != null
                            ? detail + " · 本轮已选择：" + analysis.LocalGames.Single(game => game.Id == occupied.LocalId).Name
                            : detail;
                    });
            };
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };
        panel.Children.Add(error);
        RestoreMatchingDecisions? result = null;
        var dialog = NewDialog("确认未匹配的本地游戏", new ScrollViewer { Content = panel, MaxHeight = 440 }, "确认对应关系并继续");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                if (rows.Any(row => row.Choice.HasPendingQuery))
                    throw new InvalidOperationException("请从搜索结果中确认对应游戏，或明确选择不关联。");
                result = RestoreMatchingPreview.Decide(analysis, rows.ToDictionary(row => row.LocalId,
                    row => row.Choice.SelectedId));
                error.IsOpen = false;
            }
            catch (InvalidOperationException exception)
            {
                error.Message = exception.Message; error.IsOpen = true; args.Cancel = true;
            }
        };
        ContentDialogResult accepted;
        using (ct.Register(() => DispatcherQueue.TryEnqueue(() => dialog.Hide())))
            accepted = await dialog.ShowAsync();
        ct.ThrowIfCancellationRequested();
        if (accepted != ContentDialogResult.Primary || result == null) return (false, null);
        if (result.Merges.Count > 0)
        {
            var summary = Stack(8);
            summary.Children.Add(Note("你选择的云端游戏已对应其他本地资料。继续后将保留所选目标的本地目录和现有记录，再按勾选类型恢复云端内容；多余资料条目只移除记录，不删除游戏文件或存档。此操作需要重启。"));
            foreach (LocalGameMerge merge in result.Merges)
            {
                GameIdentity from = analysis.LocalGames.Single(g => g.Id == merge.RemovedGameId);
                GameIdentity to = analysis.LocalGames.Single(g => g.Id == merge.KeptGameId);
                summary.Children.Add(Note($"将“{from.Name}”的关联及待恢复任务转到“{to.Name}”，保留目标条目。"));
            }
            summary.Children.Add(Note("两份本地记录存在差异时，保留目标条目；所选类型再使用云端备份值。原始数据由导入回滚副本保留。"));
            var mergeDialog = NewDialog("确认调整已有对应关系", new ScrollViewer { Content = summary, MaxHeight = 400 }, "合并记录并恢复");
            ContentDialogResult mergeAccepted;
            using (ct.Register(() => DispatcherQueue.TryEnqueue(() => mergeDialog.Hide())))
                mergeAccepted = await mergeDialog.ShowAsync();
            ct.ThrowIfCancellationRequested();
            if (mergeAccepted != ContentDialogResult.Primary) return (false, null);
        }
        return (true, result);
    }

}
