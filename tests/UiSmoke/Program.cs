using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using System.Reflection;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PotatoVN.App.PluginBase.BackupUi;
using PotatoVN.Backup.Core;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace UiSmoke;

internal static class Program
{
    public static string Output => Path.GetFullPath(Environment.GetEnvironmentVariable("POTATOVN_UI_SMOKE_OUTPUT") ?? Path.Combine(AppContext.BaseDirectory, "screenshots"));
    [STAThread]
    public static void Main()
    {
        Directory.CreateDirectory(Output);
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(initialization =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new SmokeApp();
            });
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(Output, "error.txt"), e.ToString()); Environment.ExitCode = 1; }
    }
}

public sealed partial class SmokeApp : Application
{
    private Window? _window;
    public SmokeApp()
    {
        InitializeComponent();
        UnhandledException += (_, args) => { File.WriteAllText(Path.Combine(Program.Output, "error.txt"), args.Exception.ToString()); args.Handled = true; Exit(); };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var controller = new DemoController();
        var view = new BackupSettingsView(controller);
        // 模拟插件管理页：宿主标题占据一部分高度，设置控件位于外层滚动容器中。
        var host = new Grid { RequestedTheme = ElementTheme.Light, Background = new SolidColorBrush(Microsoft.UI.Colors.White) };
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        host.Children.Add(new TextBlock
        {
            Text = "插件 · 云备份设置", FontSize = 28,
            Margin = new Thickness(24), Height = 48,
            VerticalAlignment = VerticalAlignment.Center
        });
        var hostPanel = new StackPanel();
        hostPanel.Children.Add(view);
        var hostScroll = new ScrollViewer
        {
            Content = hostPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(hostScroll, 1);
        host.Children.Add(hostScroll);
        _window = new Window { Title = "PotatoVN 云备份 · 隔离界面测试", Content = host };
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 850));
        bool started = false;
        view.Loaded += async (_, _) =>
        {
            if (started) return;
            started = true;
            List<string> results = [];
            try
            {
                await Task.Delay(700);
                ComboBox navigation = FindAll<ComboBox>(view).Single(combo => AutomationProperties.GetName(combo) == "设置页面");
                Check(navigation.Items.Count == 6, "应能通过固定导航访问全部六个设置页面。");
                foreach (var size in new[] { (Width: 1080, Height: 760), (Width: 760, Height: 620), (Width: 620, Height: 540) })
                {
                    // AppWindow 使用物理像素；按当前缩放换算后验证同等逻辑尺寸。
                    double scale = host.XamlRoot.RasterizationScale;
                    _window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)Math.Round(size.Width * scale), (int)Math.Round(size.Height * scale)));
                    await Task.Delay(350);
                    for (int index = 0; index < navigation.Items.Count; index++)
                    {
                        navigation.SelectedIndex = index;
                        await Task.Delay(160);
                        ShowLayoutFeedback(view);
                        host.UpdateLayout();
                        Button backup = FindButton(view, "立即备份");
                        Button save = FindButton(view, "保存设置");
                        InfoBar feedback = FindAll<InfoBar>(view).Single();
                        ScrollViewer contentScroll = FindAll<ScrollViewer>(view).Single(scroll => AutomationProperties.GetName(scroll) == "设置内容");
                        AssertVisible(navigation, hostScroll, "页面导航");
                        AssertVisible(save, hostScroll, "保存设置");
                        AssertVisible(backup, hostScroll, "立即备份");
                        AssertVisible(feedback, hostScroll, "操作反馈");
                        Check(hostScroll.ScrollableHeight < 2, "设置控件撑高了宿主滚动区域，反馈和操作会被卷走。");
                        if (size.Width == 1080) await CaptureAsync(host, $"settings-{size.Width}x{size.Height}-page-{index}-top.png");
                        double navigationY = PositionY(navigation, hostScroll);
                        double actionY = PositionY(save, hostScroll);
                        contentScroll.ChangeView(null, contentScroll.ScrollableHeight, null, true);
                        await Task.Delay(100);
                        AssertVisible(feedback, hostScroll, "滚动后的操作反馈");
                        AssertVisible(backup, hostScroll, "滚动后的立即备份");
                        Check(Math.Abs(PositionY(navigation, hostScroll) - navigationY) < 1 && Math.Abs(PositionY(save, hostScroll) - actionY) < 1,
                            "滚动设置内容时移动了固定导航或底部操作。");
                        await CaptureAsync(host, $"settings-{size.Width}x{size.Height}-page-{index}.png");
                        string page = (navigation.Items[index] as ComboBoxItem)?.Content?.ToString() ?? navigation.Items[index].ToString()!;
                        results.Add($"PASS layout {size.Width}x{size.Height} {page}: navigation/actions/feedback visible; outer scroll {hostScroll.ScrollableHeight:0.##}; content scroll {contentScroll.ScrollableHeight:0.##}");
                        if (index == 0)
                        {
                            InvokeButton(FindButton(view, "查看日志"));
                            await Task.Delay(100);
                            host.UpdateLayout();
                            TextBox log = Field<TextBox>(view, "_logText");
                            Check(log.Visibility == Visibility.Visible && log.Text.Contains("布局检查"), "展开日志应展示最近的操作结果。");
                            AssertVisible(log, hostScroll, "展开的操作日志");
                            AssertVisible(navigation, hostScroll, "日志展开时的页面导航");
                            AssertVisible(save, hostScroll, "日志展开时的保存按钮");
                            AssertVisible(feedback, hostScroll, "日志展开时的操作反馈");
                            Check(hostScroll.ScrollableHeight < 2, "展开日志后不应撑高宿主滚动区域。");
                            await CaptureAsync(host, $"settings-{size.Width}x{size.Height}-log-open.png");
                            InvokeButton(FindButton(view, "收起日志"));
                            await Task.Delay(80);
                            Check(log.Visibility == Visibility.Collapsed, "收起日志后应恢复设置内容空间。");
                            results.Add($"PASS log-toggle {size.Width}x{size.Height}: recent results visible; navigation/actions/feedback stay in viewport");
                        }
                    }
                }
                // 恢复常规尺寸，避免对话框验证被小窗口截断。
                double normalScale = host.XamlRoot.RasterizationScale;
                _window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1080 * normalScale), (int)(760 * normalScale)));
                await Task.Delay(250);
                navigation.SelectedIndex = 2;
                await Task.Delay(120);
                CheckAutomationInputs(view, results);
                navigation.SelectedIndex = 0;
                await Task.Delay(120);
                await CheckProfileSwitchingAsync(view, controller, results);
                await CheckSettingsCommitAsync(view, controller, results);
                await CheckFirstRunSettingsAsync(hostPanel, view, results);
                navigation.SelectedIndex = 4;
                await Task.Delay(120);
                await CheckPendingTargetSwitchAsync(view, controller, results);
                navigation.SelectedIndex = 0;
                await RunMatchingSmokeAsync(view, controller, results);
                File.WriteAllLines(Path.Combine(Program.Output, "result.txt"), results);
            }
            catch (Exception error)
            {
                File.WriteAllLines(Path.Combine(Program.Output, "result.txt"), results);
                File.WriteAllText(Path.Combine(Program.Output, "error.txt"), error.ToString());
                Environment.ExitCode = 1;
            }
            finally { _window.Close(); Exit(); }
        };
        _window.Activate();
    }

    private static Button FindButton(DependencyObject view, string label) =>
        FindAll<Button>(view).Single(button => button.Content as string == label);

    private static double PositionY(FrameworkElement element, UIElement viewport) =>
        element.TransformToVisual(viewport).TransformPoint(new Windows.Foundation.Point()).Y;

    private static void AssertVisible(FrameworkElement element, FrameworkElement viewport, string label)
    {
        Windows.Foundation.Point point = element.TransformToVisual(viewport).TransformPoint(new Windows.Foundation.Point());
        Check(element.ActualWidth > 0 && element.ActualHeight > 0 && point.X >= -1 && point.Y >= -1 &&
            point.X + element.ActualWidth <= viewport.ActualWidth + 1 && point.Y + element.ActualHeight <= viewport.ActualHeight + 1,
            $"{label}超出宿主可视区域：位置({point.X:0.#},{point.Y:0.#}) 大小{element.ActualWidth:0.#}×{element.ActualHeight:0.#}，可视区域{viewport.ActualWidth:0.#}×{viewport.ActualHeight:0.#}。");
    }

    private static void ShowLayoutFeedback(BackupSettingsView view)
    {
        MethodInfo method = typeof(BackupSettingsView).GetMethod("ShowMessage", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("找不到操作反馈入口。");
        method.Invoke(view, ["布局检查：操作结果会显示在这里，滚动设置时仍然可见。", InfoBarSeverity.Informational]);
    }

    private static T Field<T>(BackupSettingsView view, string name) where T : class =>
        typeof(BackupSettingsView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(view) as T
        ?? throw new InvalidOperationException("找不到设置控件：" + name);

    private static void CheckAutomationInputs(BackupSettingsView view, List<string> results)
    {
        ToggleSwitch timer = Field<ToggleSwitch>(view, "_timer");
        ToggleSwitch onExit = Field<ToggleSwitch>(view, "_onExit");
        NumberBox interval = Field<NumberBox>(view, "_interval");
        NumberBox exitDelay = Field<NumberBox>(view, "_exitDelay");
        timer.IsOn = false;
        onExit.IsOn = false;
        Check(!interval.IsEnabled && !exitDelay.IsEnabled, "关闭自动备份时应禁用对应的时间输入。");
        timer.IsOn = true;
        Check(interval.IsEnabled && !exitDelay.IsEnabled, "定时备份应只启用自己的间隔输入。");
        onExit.IsOn = true;
        Check(interval.IsEnabled && exitDelay.IsEnabled, "开启游戏退出备份时应启用等待时间。");
        timer.IsOn = false;
        onExit.IsOn = false;
        results.Add("PASS automation-inputs: timer and exit delay independently follow switches");
    }

    private static async Task CheckProfileSwitchingAsync(BackupSettingsView view, DemoController controller, List<string> results)
    {
        ComboBox profiles = Field<ComboBox>(view, "_profilePick");
        TextBox endpoint = Field<TextBox>(view, "_endpoint");
        Guid firstId = controller.Settings.Profiles[0].Id;
        Guid secondId = controller.Settings.Profiles[1].Id;
        profiles.SelectedIndex = 1;
        await Task.Delay(120);
        Check(controller.Settings.ActiveProfileId == secondId, "选择渠道后应同步用于备份与恢复，不应再要求设为当前。");
        string original = endpoint.Text;
        endpoint.Text = "https://edited.invalid/dav/";
        await Task.Delay(80);
        profiles.SelectedIndex = 0;
        ContentDialog cancel = await WaitDialogAsync(view, "渠道信息尚未保存");
        cancel.Hide();
        await WaitIdleAsync(view);
        Check(controller.Settings.ActiveProfileId == secondId && endpoint.Text == "https://edited.invalid/dav/", "继续编辑时应保留原渠道及未保存输入。");
        profiles.SelectedIndex = 0;
        ContentDialog discard = await WaitDialogAsync(view, "渠道信息尚未保存");
        InvokeButton(FindAll<Button>(discard).Single(button => button.Name == "SecondaryButton"));
        await WaitIdleAsync(view);
        Check(controller.Settings.ActiveProfileId == firstId && controller.Settings.Profiles[1].Endpoint == original,
            "放弃修改后应切换渠道，且不保存被放弃的输入。");
        endpoint.Text = "https://saved.invalid/dav/";
        await Task.Delay(80);
        profiles.SelectedIndex = 1;
        ContentDialog save = await WaitDialogAsync(view, "渠道信息尚未保存");
        InvokePrimary(save);
        await WaitIdleAsync(view);
        Check(controller.Settings.ActiveProfileId == secondId && controller.Settings.Profiles[0].Endpoint == "https://saved.invalid/dav/",
            "保存并继续应先保存编辑内容，再切换使用的渠道。");
        Check(typeof(BackupSettingsView).GetField("_remotePath", BindingFlags.Instance | BindingFlags.NonPublic) == null,
            "云端目录不应再作为独立配置输入。");
        Check(Field<Button>(view, "_authorizeChannel").Visibility == Visibility.Collapsed, "WebDAV 不应显示浏览器授权按钮。");
        results.Add("PASS profile-switching: active channel follows selection; cancel/discard/save preserve their intended edits; no directory input or WebDAV OAuth action");
    }

    private static async Task CheckSettingsCommitAsync(BackupSettingsView view, DemoController controller, List<string> results)
    {
        var checks = Field<Dictionary<BackupContent, CheckBox>>(view, "_contentChecks");
        CheckBox covers = checks[BackupContent.Covers];
        ToggleSwitch timer = Field<ToggleSwitch>(view, "_timer");
        BackupContent contentBefore = controller.Settings.Content;
        bool timerBefore = controller.Settings.TimerEnabled;
        covers.IsChecked = !contentBefore.HasFlag(BackupContent.Covers);
        timer.IsOn = !timerBefore;
        Check(controller.Settings.Content == contentBefore && controller.Settings.TimerEnabled == timerBefore,
            "编辑内容与自动开关时不应提前改变已生效配置。");
        Check(Field<TextBlock>(view, "_saveState").Text.Contains("未保存"), "编辑后应显示未保存状态。");
        await SaveSettingsAsync(view);
        Check(controller.Settings.Content.HasFlag(BackupContent.Covers) == covers.IsChecked && controller.Settings.TimerEnabled == timer.IsOn,
            "保存后应提交内容和自动备份设置。");
        Check(Field<TextBlock>(view, "_saveState").Text == "设置已保存", "提交成功后应显示设置已保存。");
        results.Add("PASS settings-commit: content and automation edits remain drafts until save; successful save clears dirty state");
    }

    private static async Task CheckFirstRunSettingsAsync(StackPanel hostPanel, BackupSettingsView originalView, List<string> results)
    {
        var controller = new DemoController();
        controller.Settings.Profiles.Clear();
        controller.Settings.ActiveProfileId = null;
        var view = new BackupSettingsView(controller);
        hostPanel.Children.Clear();
        hostPanel.Children.Add(view);
        await Task.Delay(200);
        Field<TextBox>(view, "_deviceName").Text = "新设备（合成测试）";
        await SaveSettingsAsync(view);
        Check(controller.SaveCalls == 1 && controller.Settings.Profiles.Count == 0 && controller.Settings.DeviceName == "新设备（合成测试）",
            "首次运行应允许先保存其他设置，无需验证空白渠道表单。");
        Field<TextBox>(view, "_endpoint").Text = "https://first-run.invalid/dav/";
        await Task.Delay(80);
        Field<ToggleSwitch>(view, "_timer").IsOn = true;
        Check(controller.Settings.Profiles.Count == 0 && !controller.Settings.TimerEnabled,
            "首次填写渠道并打开自动备份时仍应等待保存。");
        await SaveSettingsAsync(view);
        Check(controller.Settings.Profiles.Count == 1 && controller.Settings.ActiveProfileId == controller.Settings.Profiles[0].Id && controller.Settings.TimerEnabled,
            "首次保存应同时添加渠道、设为当前并保存自动备份。");
        Check(controller.Settings.Profiles[0].RepositoryFolder == "PotatoVN_Data", "新渠道应采用固定的 PotatoVN_Data 目录。");
        results.Add("PASS first-run-settings: other settings save without a channel; completed draft and automation save together into PotatoVN_Data");
        hostPanel.Children.Clear();
        hostPanel.Children.Add(originalView);
        await Task.Delay(200);
    }

    private static Task SaveSettingsAsync(BackupSettingsView view)
    {
        MethodInfo method = typeof(BackupSettingsView).GetMethod("SaveAllSettingsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)method.Invoke(view, null)!;
    }

    private static async Task CheckPendingTargetSwitchAsync(BackupSettingsView view, DemoController controller, List<string> results)
    {
        ListView pending = Field<ListView>(view, "_pendingList");
        PendingRestore entry = controller.GetPending().First(item => item.LocalGameId != null);
        string? previousOverride = entry.TargetOverride;
        entry.TargetOverride = Path.Combine(Program.Output, "old-game-target");
        pending.SelectedItems.Clear();
        pending.SelectedItem = pending.Items.OfType<ListViewItem>().Single(item => ((PendingRestore)item.Tag).Id == entry.Id);
        MethodInfo method = typeof(BackupSettingsView).GetMethod("ConfigurePendingTargetAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        int operationsBefore = controller.OperationCalls;
        Task operation = (Task)method.Invoke(view, [CancellationToken.None])!;
        ContentDialog dialog = await WaitDialogAsync(view, "设置存档恢复位置");
        ComboBox game = FindAll<ComboBox>(dialog).Single();
        TextBox target = FindAll<TextBox>(dialog).Single(input => input.Header as string == "本机目标路径");
        Check(target.Text == entry.TargetOverride, "恢复位置对话框应显示已有自定义目标。");
        Guid anotherGame = controller.GetGames().First(candidate => candidate.Id != entry.LocalGameId).Id;
        game.SelectedItem = game.Items.OfType<ComboBoxItem>().Single(item => item.Tag is Guid id && id == anotherGame);
        Check(target.Text.Length == 0, "更换恢复对应游戏时必须清除旧游戏的自定义路径。");
        await CaptureAsync(dialog, "pending-target-switch.png");
        dialog.Hide();
        await operation;
        Check(controller.OperationCalls == operationsBefore, "取消恢复位置编辑不应保存或触发恢复操作。");
        entry.TargetOverride = previousOverride;
        results.Add("PASS pending-target-switch: changing game clears old custom path; cancelling performs no configuration or restore call");
    }

    private static async Task WaitIdleAsync(BackupSettingsView view)
    {
        FieldInfo operation = typeof(BackupSettingsView).GetField("_operationCts", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (int attempt = 0; attempt < 50; attempt++)
        {
            await Task.Delay(40);
            if (operation.GetValue(view) == null) return;
        }
        throw new InvalidOperationException("操作未在预期时间内完成。");
    }

    private static void InvokeButton(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new InvalidOperationException("测试按钮不支持自动调用。");
        invoke.Invoke();
    }

    private static string CreateEmpty(string path) { File.WriteAllBytes(path, []); return path; }
    private static IEnumerable<T> FindAll<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T target) yield return target;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (T child in FindAll<T>(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }
    private static T? Find<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T target) return target;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (Find<T>(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        return null;
    }
}

internal sealed class DemoController : IBackupUiController
{
    private readonly List<GameIdentity> _games;
    private readonly List<PendingRestore> _pending;
    public BackupSettings Settings { get; } = new();
    public DemoController()
    {
        string local = Path.Combine(Program.Output, "synthetic-game"); Directory.CreateDirectory(local);
        _games = [new GameIdentity { Id = Guid.NewGuid(), Name = "本地游戏（演示）", LocalDirectory = local },
            new GameIdentity { Id = Guid.NewGuid(), Name = "仅保留游戏信息（演示）" }];
        ProviderProfile profile = new() { Name = "演示 WebDAV", Endpoint = "https://example.invalid/dav/", Kind = ProviderKind.WebDav };
        Settings.Profiles.Add(profile); Settings.ActiveProfileId = profile.Id;
        Settings.Profiles.Add(new ProviderProfile { Name = "第二云盘（演示）", Endpoint = "https://second.invalid/dav/", Kind = ProviderKind.WebDav });
        _pending = Enum.GetValues<RestoreStatus>().Select(status => new PendingRestore
        {
            ProfileId = profile.Id, ProfileName = profile.Name, Status = status, LocationId = "demo-root",
            LocalGameId = status == RestoreStatus.PendingLink ? null : _games[0].Id,
            TargetPath = status is RestoreStatus.PendingLink or RestoreStatus.PendingPath ? null : Path.Combine(local, "savedata"),
            Manifest = new SaveManifest { Game = _games[0], Id = status.ToString(), CapturedUtc = DateTimeOffset.UtcNow,
                ManifestPath = "v1/saves/demo/" + status + "/manifest.json", Locations = [new SaveLocation { Id = "demo-root", BaseKind = SaveBaseKind.GameRoot, RelativePath = "savedata" }] }
        }).ToList();
        Settings.LibraryMappings.Add(new PendingLibraryMapping { Id = "demo-library", Name = "旧设备游戏库（演示）", OriginalPath = "D:\\Games" });
    }
    public IReadOnlyList<GameIdentity> GetGames() => _games;
    public IReadOnlyList<PendingRestore> GetPending() => _pending;
    public long PendingBytes => 1024 * 1024 * 12;
    public bool IsBusy => false;
    public string Status => "隔离界面检查：使用合成资料，不连接云端或真实游戏库。";
    public event Action? StateChanged { add { } remove { } }
    public int SaveCalls { get; private set; }
    public int OperationCalls { get; private set; }
    public Task SaveSettingsAsync() { SaveCalls++; return Task.CompletedTask; }
    public Task RefreshPendingAsync(CancellationToken token) => Task.CompletedTask;
    public void CancelCurrentOperation() { }
    private Task Disabled() { OperationCalls++; return Task.FromException(new InvalidOperationException("隔离界面检查不执行真实操作。")); }
    public Task SetSecretAsync(Guid id, string suffix, string value) => Disabled();
    public Task AuthorizeAsync(Guid id, CancellationToken token) => Disabled();
    public Task ProbeAsync(Guid id, CancellationToken token) => Disabled();
    public Task BackupAsync(CancellationToken token) => Disabled();
    public Task<IReadOnlyList<SnapshotManifest>> ListSnapshotsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<SnapshotManifest>>([]);
    public Task RestoreSnapshotAsync(SnapshotManifest snapshot, BackupContent content, CancellationToken token, RestoreMatchingDecisions? matching = null) => Disabled();
    public Task ConfigureGameMappingAsync(Guid backupId, Guid localId, CancellationToken token) => Disabled();
    public Task ConfigurePendingAsync(Guid id, Guid? game, string? path, CancellationToken token) => Disabled();
    public Task RestorePendingAsync(IReadOnlyList<Guid> ids, CancellationToken token) => Disabled();
    public Task ClearPendingAsync(IReadOnlyList<Guid> ids, CancellationToken token) => Disabled();
    public Task ApplyLibraryMappingAsync(string id, string target, CancellationToken token) => Disabled();
}
