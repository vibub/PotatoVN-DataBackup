using System;
using System.Threading;
using System.Threading.Tasks;
using GalgameManager.Models.BgTasks;

namespace PotatoVN.App.PluginBase.Backup;

/// <summary>一个宿主任务覆盖云端传输与准备过程，直到实际操作结束才成功。</summary>
internal sealed class CloudTransferTask : BgTaskBase, IDisposable
{
    private readonly Func<CancellationToken, Task> _action;
    private readonly Func<string> _summary;
    private readonly string _operation;
    public override string Title => _operation + "任务";
    public override bool CanCancel => true;
    public override bool ProgressOnTrayIcon => true;

    public CloudTransferTask(string operation, Func<CancellationToken, Task> action, Func<string> summary, CancellationToken token)
    {
        _operation = operation;
        _action = action;
        _summary = summary;
        CancellationTokenSource = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(token);
    }

    public void Report(int percent, string message) => ChangeProgress(Math.Min(percent, 99), 100, message, false);

    protected override async Task RunInternal()
    {
        Report(0, $"正在准备{_operation}…");
        try
        {
            await _action(CancellationToken!.Value);
            ChangeProgress(100, 100, _summary());
        }
        catch (OperationCanceledException)
        {
            ChangeProgress(-1, 100, $"{_operation}已取消。", false);
            throw;
        }
    }

    protected override Task RecoverFromJsonInternal() => Task.CompletedTask;
    public void Dispose() => CancellationTokenSource?.Dispose();
}
