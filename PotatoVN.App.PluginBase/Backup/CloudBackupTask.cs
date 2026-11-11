using System;
using System.Threading;
using System.Threading.Tasks;
using GalgameManager.Models.BgTasks;

namespace PotatoVN.App.PluginBase.Backup;

/// <summary>一个宿主任务覆盖完整备份；原生导出只是其中一个步骤。</summary>
internal sealed class CloudBackupTask : BgTaskBase, IDisposable
{
    private readonly Func<CancellationToken, Task> _action;
    private readonly Func<string> _summary;
    public override string Title => "云备份任务";
    public override bool CanCancel => true;
    public override bool ProgressOnTrayIcon => true;

    public CloudBackupTask(Func<CancellationToken, Task> action, Func<string> summary, CancellationToken token)
    {
        _action = action;
        _summary = summary;
        CancellationTokenSource = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(token);
    }

    public void Report(int percent, string message) => ChangeProgress(Math.Min(percent, 99), 100, message, false);

    protected override async Task RunInternal()
    {
        Report(0, "正在准备云备份…");
        try
        {
            await _action(CancellationToken!.Value);
            ChangeProgress(100, 100, _summary());
        }
        catch (OperationCanceledException)
        {
            ChangeProgress(-1, 100, "云备份已取消。", false);
            throw;
        }
    }

    protected override Task RecoverFromJsonInternal() => Task.CompletedTask;
    public void Dispose() => CancellationTokenSource?.Dispose();
}
