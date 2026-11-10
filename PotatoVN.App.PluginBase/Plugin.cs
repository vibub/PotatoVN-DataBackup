using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using GalgameManager.WinApp.Base.Contracts;
using GalgameManager.WinApp.Base.Contracts.PluginUi;
using GalgameManager.WinApp.Base.Models;
using GalgameManager.WinApp.Base.Models.Msgs;
using PotatoVN.App.PluginBase.Backup;
using PotatoVN.App.PluginBase.BackupUi;
using PotatoVN.Backup.Core.Engine;

namespace PotatoVN.App.PluginBase;

public partial class Plugin : IPlugin, IPluginSetting
{
    public static IPotatoVnApi HostApi { get; private set; } = null!;
    private BackupController? _controller;
    public PluginInfo Info { get; } = new()
    {
        Id = new Guid("9c734d66-e185-4f1f-b5c2-6f48046e143b"),
        Name = "自定义云备份",
        Description = "将 PotatoVN 资料和游戏存档备份到 WebDAV、OneDrive、Google Drive，支持选择类型、历史版本和待恢复区。"
    };

    public async Task InitializeAsync(IPotatoVnApi hostApi)
    {
        HostApi = hostApi;
        TaskCompletionSource<PotatoHostAdapter> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        hostApi.InvokeOnMainThread(() =>
        {
            try { completion.SetResult(new PotatoHostAdapter(hostApi)); }
            catch (Exception e) { completion.SetException(e); }
        });
        PotatoHostAdapter host = await completion.Task;
        SettingsStore store = new(host.StateRoot);
        var settings = await store.LoadAsync();
        PendingRestoreStore pending = new(host.StateRoot);
        await pending.LoadAsync();
        await host.CompletePendingImportAsync();
        _controller = new BackupController(hostApi, host, store, settings, pending);
        BackupPage.Controller = _controller;
        BackupPage.HostWindowProvider = () => HostApi.GetMainWindow();
        HostApi.Messenger.Register<GalgameStoppedMessage>(this, (_, _) => _controller.ScheduleGameExit());
        InitUi();
    }

    public Task OnUninstallAsync(bool deleteData, Action<TimeSpan> extendWaitHandler, CancellationToken cts)
    {
        HostApi.Messenger.UnregisterAll(this);
        HostApi.UnregisterSidebarButton("cloud-backup");
        _controller?.Dispose();
        BackupPage.Controller = null;
        BackupPage.HostWindowProvider = null;
        // 待恢复存档及回滚副本包含用户数据，不在卸载插件时静默删除。
        return Task.CompletedTask;
    }
}
