using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        _window = new Window { Title = "PotatoVN 云备份 · 隔离界面测试", Content = view };
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 850));
        view.Loaded += async (_, _) =>
        {
            try
            {
                await Task.Delay(700);
                Pivot pivot = Find<Pivot>(view) ?? throw new InvalidOperationException("没有找到页面选项卡。");
                List<string> results = [];
                for (int index = 0; index < pivot.Items.Count; index++)
                {
                    pivot.SelectedIndex = index;
                    await Task.Delay(450);
                    view.UpdateLayout();
                    Button backupButton = FindAll<Button>(view).Single(button => button.Content as string == "立即备份");
                    Windows.Foundation.Point footer = backupButton.TransformToVisual(view).TransformPoint(new Windows.Foundation.Point());
                    if (footer.Y < 0 || footer.Y + backupButton.ActualHeight > view.ActualHeight + 1)
                        throw new InvalidOperationException("底部备份按钮超出了可见窗口。");
                    RenderTargetBitmap bitmap = new();
                    await bitmap.RenderAsync(view);
                    if (bitmap.PixelWidth < 800 || bitmap.PixelHeight < 500) throw new InvalidOperationException("页面未完成有效布局。");
                    var pixels = await bitmap.GetPixelsAsync();
                    StorageFile file = await StorageFile.GetFileFromPathAsync(CreateEmpty(Path.Combine(Program.Output, $"tab-{index}.png")));
                    using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
                    BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                    using DataReader reader = DataReader.FromBuffer(pixels);
                    byte[] data = new byte[pixels.Length]; reader.ReadBytes(data);
                    encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth,
                        (uint)bitmap.PixelHeight, 96, 96, data);
                    await encoder.FlushAsync();
                    results.Add($"PASS {((PivotItem)pivot.Items[index]).Header}: {bitmap.PixelWidth}x{bitmap.PixelHeight}");
                }
                await RunMatchingSmokeAsync(view, controller, results);
                File.WriteAllLines(Path.Combine(Program.Output, "result.txt"), results);
            }
            catch (Exception error) { File.WriteAllText(Path.Combine(Program.Output, "error.txt"), error.ToString()); Environment.ExitCode = 1; }
            finally { _window.Close(); Exit(); }
        };
        _window.Activate();
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
