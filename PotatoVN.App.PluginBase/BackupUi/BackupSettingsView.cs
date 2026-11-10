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

public sealed partial class BackupSettingsView : UserControl
{
    private readonly IBackupUiController _controller;
    private readonly List<Control> _operationControls = new();
    private readonly List<Control> _configurationTabs = new();
    private readonly TextBlock _status = Note("");
    private readonly TextBlock _activeChannel = Note("");
    private readonly InfoBar _feedback = new() { IsClosable = true };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed };
    private readonly Button _cancel = new() { Content = "取消当前操作", IsEnabled = false };
    private CancellationTokenSource? _operationCts;
    private bool _subscribed;
    private bool _loading;

    public BackupSettingsView(IBackupUiController controller)
    {
        _controller = controller;
        var root = new Grid { Padding = new Thickness(20), RowSpacing = 12 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = Stack();
        heading.Children.Add(Heading("云备份", "TitleTextBlockStyle"));
        heading.Children.Add(Note("使用自己的云盘，备份资料与存档。未关联游戏的存档会保存在待恢复区。"));
        heading.Children.Add(_activeChannel);
        root.Children.Add(heading);
        Grid.SetRow(_feedback, 1);
        root.Children.Add(_feedback);

        var tabs = new Pivot();
        tabs.Items.Add(Tab("渠道", BuildProfilesTab(), true));
        tabs.Items.Add(Tab("备份内容", BuildContentTab(), true));
        tabs.Items.Add(Tab("自动备份", BuildAutomationTab(), true));
        tabs.Items.Add(Tab("历史恢复", BuildHistoryTab()));
        tabs.Items.Add(Tab("待恢复区", BuildPendingTab()));
        tabs.Items.Add(Tab("库路径映射", BuildLibraryTab(), true));
        Grid.SetRow(tabs, 2);
        root.Children.Add(tabs);

        var footer = Stack();
        footer.Children.Add(_progress);
        footer.Children.Add(_status);
        var actions = Row();
        actions.Children.Add(ActionButton("保存设置", async _ => await SaveAllSettingsAsync()));
        actions.Children.Add(ActionButton("立即备份", async ct =>
        {
            await SaveAllSettingsAsync();
            if (_controller.Settings.Content == BackupContent.None)
                throw new InvalidOperationException("请至少选择一种备份内容。");
            await _controller.BackupAsync(ct);
        }));
        _cancel.Click += (_, _) => { _operationCts?.Cancel(); _controller.CancelCurrentOperation(); };
        actions.Children.Add(_cancel);
        footer.Children.Add(actions);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        Content = root;

        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            if (_subscribed) _controller.StateChanged -= ControllerStateChanged;
            _subscribed = false;
        };
        RefreshStatus();
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (!_subscribed)
        {
            _controller.StateChanged += ControllerStateChanged;
            _subscribed = true;
        }
        RefreshStatus();
        await RunAsync(async ct => await _controller.RefreshPendingAsync(ct), false);
    }

    private void ControllerStateChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            RefreshStatus();
            RefreshPendingView();
            RefreshLibraryView();
        });
    }

    private void RefreshStatus()
    {
        bool busy = _operationCts != null || _controller.IsBusy;
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _cancel.IsEnabled = _operationCts is { IsCancellationRequested: false } || _controller.IsBusy;
        foreach (Control control in _operationControls) control.IsEnabled = !busy;
        foreach (Control control in _configurationTabs) control.IsEnabled = !busy;
        _status.Text = string.IsNullOrWhiteSpace(_controller.Status) ? "就绪" : _controller.Status;
        ProviderProfile? active = _controller.Settings.Profiles.FirstOrDefault(p => p.Id == _controller.Settings.ActiveProfileId);
        _activeChannel.Text = active == null ? "尚未设置当前渠道" : $"当前渠道：{active.Name} · {ProviderLabel(active.Kind)}";
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, bool showSuccess = true)
    {
        if (_operationCts != null || _controller.IsBusy)
        {
            ShowMessage("请等待当前操作完成，或先取消当前操作。", InfoBarSeverity.Informational);
            return;
        }
        using var cancellation = new CancellationTokenSource();
        _operationCts = cancellation;
        _feedback.IsOpen = false;
        RefreshStatus();
        try
        {
            await action(cancellation.Token);
            if (showSuccess) ShowMessage("操作完成。", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            ShowMessage("操作已取消。", InfoBarSeverity.Informational);
        }
        catch (Exception error)
        {
            ShowMessage(error.GetBaseException().Message, InfoBarSeverity.Error);
        }
        finally
        {
            _operationCts = null;
            RefreshStatus();
            RefreshPendingView();
            RefreshLibraryView();
        }
    }

    private void ShowMessage(string message, InfoBarSeverity severity)
    {
        _feedback.Severity = severity;
        _feedback.Message = message;
        _feedback.IsOpen = true;
    }

    private Button ActionButton(string text, Func<CancellationToken, Task> action, bool trackBusy = true)
    {
        var button = new Button { Content = text };
        button.Click += async (_, _) => await RunAsync(action);
        if (trackBusy) _operationControls.Add(button);
        return button;
    }

    private PivotItem Tab(string title, StackPanel panel, bool configuration = false)
    {
        var scroll = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 8, 16, 16)
        };
        if (configuration) _configurationTabs.Add(scroll);
        return new PivotItem { Header = title, Content = scroll };
    }

    private async Task<ContentDialogResult> DialogAsync(string title, UIElement content, string primary = "确定", string close = "取消")
    {
        var dialog = NewDialog(title, content, primary, close);
        return await dialog.ShowAsync();
    }

    private ContentDialog NewDialog(string title, UIElement content, string primary = "确定", string close = "取消")
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = title,
            Content = content,
            PrimaryButtonText = primary,
            CloseButtonText = close,
            DefaultButton = ContentDialogButton.Close
        };
        dialog.Resources["ContentDialogMinWidth"] = 500d;
        dialog.Resources["ContentDialogMaxWidth"] = 760d;
        dialog.Resources["ContentDialogMaxHeight"] = 680d;
        return dialog;
    }

    private static StackPanel Stack(double spacing = 12) => new() { Spacing = spacing };
    private static StackPanel Row() => new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = .8 };
    private static TextBlock Heading(string text, string style = "SubtitleTextBlockStyle")
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (Application.Current.Resources.TryGetValue(style, out object? value) && value is Style found) block.Style = found;
        return block;
    }
    private static TextBox TextInput(string header, string placeholder = "") => new()
    {
        Header = header, PlaceholderText = placeholder,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private static void AddChoice(ComboBox combo, object? value, string label) =>
        combo.Items.Add(new ComboBoxItem { Tag = value, Content = label });
    private static object? Choice(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag;
    private static void SelectChoice(ComboBox combo, object? value)
    {
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, value));
    }
    private static string ProviderLabel(ProviderKind kind) => kind switch
    {
        ProviderKind.WebDav => "WebDAV", ProviderKind.OneDrive => "OneDrive", _ => "Google Drive"
    };
    private static string GameLabel(GameIdentity game) =>
        $"{game.Name} · {(string.IsNullOrWhiteSpace(game.LocalDirectory) || !Directory.Exists(game.LocalDirectory) ? "未关联有效目录" : game.LocalDirectory)}";
    private static string SizeLabel(long length)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = length;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}
