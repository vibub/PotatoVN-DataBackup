using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PotatoVN.App.PluginBase.BackupUi;
using PotatoVN.Backup.Core;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace UiSmoke;

public sealed partial class SmokeApp
{
    private static async Task RunMatchingSmokeAsync(BackupSettingsView view, DemoController controller, List<string> results)
    {
        string before = JsonSerializer.Serialize(controller.Settings, BackupJson.Options);
        int savesBefore = controller.SaveCalls, operationsBefore = controller.OperationCalls;
        var settingsOnly = await Preview(view, new SnapshotManifest(), BackupContent.Settings, CancellationToken.None);
        Check(settingsOnly.Continue && settingsOnly.Matching == null, "仅设置恢复不弹出游戏匹配");
        results.Add("PASS matching-settings-only: no dialog");
        var known = await Preview(view, new SnapshotManifest { Games = controller.GetGames().ToList() }, BackupContent.GameInfo, CancellationToken.None);
        Check(known.Continue && known.Matching?.Targets.Count == controller.GetGames().Count, "全部精确匹配不弹窗");
        results.Add("PASS matching-known-games: no dialog");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await ExpectCancelled(Preview(view, new SnapshotManifest(), BackupContent.Settings, cancelled.Token));
        }
        results.Add("PASS matching-pre-cancelled: no dialog");

        SnapshotManifest unmatched = new()
        {
            Games = [new GameIdentity { Id = Guid.NewGuid(), Name = "云端游戏甲（合成数据）" },
                new GameIdentity { Id = Guid.NewGuid(), Name = "云端游戏乙（合成数据）" }]
        };
        using (var cancellation = new CancellationTokenSource())
        {
            var preview = Preview(view, unmatched, BackupContent.GameInfo, cancellation.Token);
            ContentDialog dialog = await WaitDialogAsync(view, "确认未匹配的本地游戏");
            Check(FindAll<GameSearchPicker>(dialog).Count() == 2, "未匹配弹窗应展示两个本地条目");
            await CaptureAsync(dialog, "matching-unmatched.png");
            cancellation.Cancel();
            await ExpectCancelled(preview);
        }
        results.Add("PASS matching-unmatched-render: 2 rows; cancellation closes dialog");

        var closedPreview = Preview(view, unmatched, BackupContent.GameInfo, CancellationToken.None);
        ContentDialog closedDialog = await WaitDialogAsync(view, "确认未匹配的本地游戏");
        closedDialog.Hide();
        Check(!(await closedPreview).Continue, "直接关闭首次确认应终止恢复");
        results.Add("PASS matching-first-close: no continuation");

        SnapshotManifest occupied = new()
        {
            Games = [new GameIdentity { Id = controller.GetGames()[1].Id, Name = "云端资料条目（合成数据）" }]
        };
        using (var cancellation = new CancellationTokenSource())
        {
            var preview = Preview(view, occupied, BackupContent.GameInfo, cancellation.Token);
            ContentDialog dialog = await WaitDialogAsync(view, "确认未匹配的本地游戏");
            FindAll<GameSearchPicker>(dialog).Single().SelectedId = occupied.Games[0].Id;
            InvokePrimary(dialog);
            ContentDialog merge = await WaitDialogAsync(view, "确认调整已有对应关系");
            await CaptureAsync(merge, "matching-merge-confirmation.png");
            cancellation.Cancel();
            await ExpectCancelled(preview);
        }
        results.Add("PASS matching-second-cancel: merge dialog closes without continuation");

        var closeMergePreview = Preview(view, occupied, BackupContent.GameInfo, CancellationToken.None);
        ContentDialog first = await WaitDialogAsync(view, "确认未匹配的本地游戏");
        FindAll<GameSearchPicker>(first).Single().SelectedId = occupied.Games[0].Id;
        InvokePrimary(first);
        ContentDialog second = await WaitDialogAsync(view, "确认调整已有对应关系");
        second.Hide();
        Check(!(await closeMergePreview).Continue, "关闭第二次合并确认应终止恢复");
        results.Add("PASS matching-second-close: no continuation");

        var duplicatePreview = Preview(view, unmatched, BackupContent.GameInfo, CancellationToken.None);
        ContentDialog duplicates = await WaitDialogAsync(view, "确认未匹配的本地游戏");
        GameSearchPicker[] pickers = FindAll<GameSearchPicker>(duplicates).ToArray();
        await EnterGameQueryAsync(pickers[0], "云端游戏甲");
        Check(pickers[0].HasPendingQuery && pickers[0].SelectedId == null,
            $"输入名称只能筛选，不得自动关联。pending={pickers[0].HasPendingQuery}, selected={pickers[0].SelectedId}, text={pickers[0].SearchBox.Text}");
        InvokePrimary(duplicates);
        await Task.Delay(100);
        Check(!duplicatePreview.IsCompleted && FindAll<InfoBar>(duplicates).Any(info => info.IsOpen),
            "未确认搜索候选时应拦截恢复。");
        pickers[0].SelectedId = unmatched.Games[0].Id;
        Check(((IEnumerable<GameSearchItem>)pickers[1].SearchBox.ItemsSource).Single(item => item.Id == unmatched.Games[0].Id).Detail.Contains("本轮已选择"),
            "被其他本地游戏选中的云端候选应标注占用信息。");
        pickers[1].SelectedId = unmatched.Games[0].Id;
        InvokePrimary(duplicates);
        await Task.Delay(100);
        Check(!duplicatePreview.IsCompleted && FindAll<InfoBar>(duplicates).Any(info => info.IsOpen),
            "同一云端游戏不能同时对应两个本地游戏。");
        pickers[1].SelectedId = unmatched.Games[1].Id;
        InvokePrimary(duplicates);
        var confirmed = await duplicatePreview;
        Check(confirmed.Continue && confirmed.Matching?.Targets.Count == 2, "修正重复对应后应返回两个明确ID的对应关系。");
        results.Add("PASS matching-search-and-duplicates: typed names do not map; uncommitted query blocks; duplicate cloud choice blocks; distinct IDs confirm");

        Check(before == JsonSerializer.Serialize(controller.Settings, BackupJson.Options), "匹配预览与取消不得写配置");
        Check(controller.SaveCalls == savesBefore && controller.OperationCalls == operationsBefore, "匹配预览不得保存配置、下载或触发恢复");
        results.Add("PASS matching-side-effects: no settings writes / downloads / restore calls");
    }

    private static Task<(bool Continue, RestoreMatchingDecisions? Matching)> Preview(
        BackupSettingsView view, SnapshotManifest snapshot, BackupContent content, CancellationToken ct)
    {
        MethodInfo method = typeof(BackupSettingsView).GetMethod("ConfirmRestoreMatchesAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("找不到恢复匹配预览。");
        return (Task<(bool Continue, RestoreMatchingDecisions? Matching)>)method.Invoke(view, [snapshot, content, ct])!;
    }

    private static async Task ExpectCancelled(Task task)
    {
        try { await task; throw new InvalidOperationException("操作没有响应取消。"); }
        catch (OperationCanceledException) { }
    }

    private static async Task<ContentDialog> WaitDialogAsync(BackupSettingsView view, string title)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            await Task.Delay(60);
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(view.XamlRoot))
                if (Find<ContentDialog>(popup.Child) is { } dialog && dialog.Title as string == title)
                {
                    dialog.UpdateLayout();
                    await Task.Delay(80);
                    return dialog;
                }
        }
        throw new InvalidOperationException("未显示预期对话框：" + title);
    }

    private static void InvokePrimary(ContentDialog dialog)
    {
        Button button = FindAll<Button>(dialog).Single(b => b.Name == "PrimaryButton");
        var peer = new ButtonAutomationPeer(button);
        if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new InvalidOperationException("测试对话框主按钮不支持调用。");
        invoke.Invoke();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task CaptureAsync(FrameworkElement element, string fileName)
    {
        RenderTargetBitmap bitmap = new();
        await bitmap.RenderAsync(element);
        Check(bitmap.PixelWidth > 200 && bitmap.PixelHeight > 100, "对话框未完成布局。");
        var pixels = await bitmap.GetPixelsAsync();
        StorageFile file = await StorageFile.GetFileFromPathAsync(CreateEmpty(Path.Combine(Program.Output, fileName)));
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        using DataReader reader = DataReader.FromBuffer(pixels);
        byte[] data = new byte[pixels.Length]; reader.ReadBytes(data);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth,
            (uint)bitmap.PixelHeight, 96, 96, data);
        await encoder.FlushAsync();
    }
}
