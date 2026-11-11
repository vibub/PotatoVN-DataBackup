using Microsoft.UI.Xaml.Controls;
using PotatoVN.App.PluginBase.Backup;
using PotatoVN.App.PluginBase.BackupUi;

namespace UiSmoke;

public sealed partial class SmokeApp
{
    private static async Task CheckBackupTaskAsync(BackupSettingsView view, DemoController controller, List<string> results)
    {
        var export = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uploading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CloudBackupTask? success = null;
        using (success = new CloudBackupTask(async token =>
        {
            await export.Task.WaitAsync(token);
            success!.Report(40, "正在上传资料");
            uploading.SetResult();
            await publish.Task.WaitAsync(token);
        }, () => "备份完成：合成云端已发布。", CancellationToken.None))
        {
            Task running = success.Run();
            Check(success.Title == "云备份任务" && success.IsRunning && success.CanCancel && !success.CurrentProgress.NotifyWhenSuccess,
                "完整备份任务没有在宿主任务模型中正确启动。");
            export.SetResult();
            await uploading.Task;
            Check(success.IsRunning && success.CurrentProgress.Current == 40 && !running.IsCompleted,
                "导出完成不应结束云备份任务。");
            success.Report(100, "正在发布完整备份");
            Check(success.IsRunning && success.CurrentProgress.Current == 99 && !success.CurrentProgress.NotifyWhenSuccess,
                "实际操作结束前不能提前通知成功。");
            publish.SetResult();
            await running;
            Check(!success.IsRunning && success.CurrentProgress.Current == 100 && success.CurrentProgress.NotifyWhenSuccess,
                "完整备份发布后没有发出成功终态。");
        }
        results.Add("PASS 整体备份任务覆盖导出与上传，实际操作结束后才报告成功。");

        using (var failed = new CloudBackupTask(_ => Task.FromException(new IOException("合成上传失败")), () => "不应成功", CancellationToken.None))
        {
            try { await failed.Run(); throw new InvalidOperationException("错误被当成成功。"); }
            catch (IOException) { }
            for (int attempt = 0; failed.CurrentProgress.Current >= 0 && attempt < 20; attempt++) await Task.Delay(10);
            Check(failed.CurrentProgress.Current < 0 && failed.CurrentProgress.ToSeverity() == InfoBarSeverity.Error, "失败任务错误地通知了成功。");
        }
        results.Add("PASS 上传失败保留异常与失败状态，不通知备份完成。");

        using (var cancelled = new CloudBackupTask(token => Task.Delay(Timeout.Infinite, token), () => "不应成功", CancellationToken.None))
        {
            Task running = cancelled.Run();
            await cancelled.CancelAsync();
            try { await running; throw new InvalidOperationException("取消无效。"); }
            catch (OperationCanceledException) { }
            Check(!cancelled.IsRunning && cancelled.CurrentProgress.Current < 0 && !cancelled.CurrentProgress.NotifyWhenSuccess,
                "取消任务仍然运行或通知了成功。");
        }
        results.Add("PASS 宿主取消入口结束整体任务且不通知成功。");

        controller.SetBackupProgress(65);
        await Task.Delay(80);
        ProgressBar bar = Field<ProgressBar>(view, "_progress");
        Check(bar.Visibility == Microsoft.UI.Xaml.Visibility.Visible && !bar.IsIndeterminate && bar.Value == 65, "设置页未显示整体备份进度。");
        controller.SetBackupProgress(null);
        await Task.Delay(80);
        Check(bar.IsIndeterminate, "其他任务应继续显示不确定进度。");
        Expander advanced = FindAll<Expander>(view).Single(expander => Equals(expander.Header, "更多选项"));
        advanced.IsExpanded = true;
        await Task.Delay(100);
        Button folder = FindAll<Button>(view).Single(button => Equals(button.Content, "打开本机数据目录"));
        InvokeButton(folder);
        await Task.Delay(100);
        Check(controller.OpenDataFolderCalls == 1, "本机目录入口没有调用宿主适配。");
        advanced.IsExpanded = false;
        results.Add("PASS 设置页显示整体百分比并提供本机数据目录入口。");
    }
}
