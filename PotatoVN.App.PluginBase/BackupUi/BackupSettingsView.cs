using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PotatoVN.Backup.Core;

namespace PotatoVN.App.PluginBase.BackupUi;

public sealed partial class BackupSettingsView : UserControl
{
    private readonly IBackupUiController _controller;
    private readonly Func<IntPtr>? _getWindowHandle;
    private readonly List<Control> _operationControls = new();
    private readonly List<Control> _configurationTabs = new();
    private readonly TextBlock _status = Note("");
    private readonly TextBlock _activeChannel = Note("");
    private readonly TextBlock _saveState = Note("设置已保存");
    private readonly InfoBar _feedback = new() { IsClosable = true };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed };
    private readonly Button _cancel = new() { Content = "取消操作", Visibility = Visibility.Collapsed };
    private readonly Pivot _navigation = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ScrollViewer _pageScroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(0, 4, 12, 8)
    };
    private readonly List<StackPanel> _pages = new();
    private readonly List<string> _logEntries = new();
    private readonly TextBox _logText = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        Height = 88, Visibility = Visibility.Collapsed, Header = null, PlaceholderText = "操作结果和备份进度会显示在这里。"
    };
    private CancellationTokenSource? _operationCts;
    private FrameworkElement? _viewportHost;
    private bool _subscribed;
    private bool _loading;
    private bool _settingsDirty;
    private bool _operationDismissed;
    private string? _lastStatus;
    private int? _operationPageIndex;
    private int? _statusPageIndex;
    private int? _feedbackPageIndex;
    private bool _hasStatus;

    public BackupSettingsView(IBackupUiController controller, Func<IntPtr>? getWindowHandle = null)
    {
        _controller = controller;
        _getWindowHandle = getWindowHandle;
        var root = new Grid { Padding = new Thickness(16), RowSpacing = 10, MaxWidth = 1040, HorizontalAlignment = HorizontalAlignment.Stretch };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = Stack(4);
        heading.Children.Add(Heading("云备份", "SubtitleTextBlockStyle"));
        heading.Children.Add(_activeChannel);
        root.Children.Add(heading);

        AutomationProperties.SetName(_navigation, "设置页面");
        AutomationProperties.SetName(_pageScroll, "设置内容");
        AutomationProperties.SetName(_feedback, "操作结果");
        Grid.SetRow(_navigation, 1);
        root.Children.Add(_navigation);
        AddPage("连接云盘", BuildProfilesTab(), true);
        AddPage("备份内容", BuildContentTab(), true);
        AddPage("自动备份", BuildAutomationTab(), true);
        AddPage("恢复备份", BuildHistoryTab());
        AddPage("待恢复存档", BuildPendingTab());
        AddPage("游戏库关联", BuildLibraryTab(), true);
        _navigation.SelectionChanged += (_, _) =>
        {
            int index = _navigation.SelectedIndex;
            if (index >= 0 && index < _pages.Count)
            {
                _pageScroll.Content = _pages[index];
                _pageScroll.ChangeView(null, 0, null, true);
                UpdateFeedbackVisibility();
            }
        };
        _navigation.SelectedIndex = 0;
        _pageScroll.Content = _pages[0];
        Grid.SetRow(_pageScroll, 2);
        root.Children.Add(_pageScroll);

        var footer = Stack(6);
        footer.Children.Add(_progress);
        footer.Children.Add(_status);
        footer.Children.Add(_feedback);
        footer.Children.Add(_logText);
        var actions = Row();
        actions.Children.Add(ActionButton("保存设置", async _ => await SaveAllSettingsAsync(), successMessage: "设置已保存。"));
        Button backup = ActionButton("立即备份", async ct =>
        {
            await SaveAllSettingsAsync();
            await _controller.BackupAsync(ct);
        });
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out object? accent) && accent is Style style) backup.Style = style;
        actions.Children.Add(backup);
        _cancel.Click += (_, _) => { _operationCts?.Cancel(); _controller.CancelCurrentOperation(); };
        actions.Children.Add(_cancel);
        var logToggle = new Button { Content = "查看日志" };
        logToggle.Click += (_, _) =>
        {
            bool opening = _logText.Visibility != Visibility.Visible;
            _logText.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
            logToggle.Content = opening ? "收起日志" : "查看日志";
        };
        actions.Children.Add(logToggle);
        actions.Children.Add(_saveState);
        footer.Children.Add(actions);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        Content = root;
        _feedback.Closed += (_, _) => { _hasStatus = false; UpdateFeedbackVisibility(); };

        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            if (_subscribed) _controller.StateChanged -= ControllerStateChanged;
            _subscribed = false;
            if (_viewportHost != null) _viewportHost.SizeChanged -= HostSizeChanged;
            _viewportHost = null;
        };
        RefreshStatus();
    }

    private void AddPage(string title, StackPanel panel, bool configuration = false)
    {
        panel.MaxWidth = 860;
        panel.HorizontalAlignment = HorizontalAlignment.Stretch;
        _navigation.Items.Add(new PivotItem
        {
            Header = new TextBlock { Text = title, FontSize = 16 },
            Tag = _pages.Count
        });
        _pages.Add(panel);
        // All configuration editors are frozen while a backup or restore is running.
        if (configuration)
        {
            var container = new ContentControl { Content = panel, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            _configurationTabs.Add(container);
            var wrapper = Stack(0);
            wrapper.Children.Add(container);
            _pages[^1] = wrapper;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (!_subscribed)
        {
            _controller.StateChanged += ControllerStateChanged;
            _subscribed = true;
        }
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent != null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ScrollViewer or ScrollView)
            {
                _viewportHost = (FrameworkElement)parent;
                _viewportHost.SizeChanged += HostSizeChanged;
                UpdateViewportHeight();
                break;
            }
        }
        RefreshStatus();
        await RunAsync(async ct => await _controller.RefreshPendingAsync(ct), false, pageIndex: 4);
    }

    private void HostSizeChanged(object sender, SizeChangedEventArgs args) => UpdateViewportHeight();

    private void UpdateViewportHeight()
    {
        if (_viewportHost == null || _viewportHost.ActualHeight <= 0) return;
        double top = TransformToVisual(_viewportHost).TransformPoint(new Windows.Foundation.Point()).Y;
        MaxHeight = Math.Max(260, _viewportHost.ActualHeight - Math.Max(0, top) - 12);
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
        _cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _cancel.IsEnabled = _operationCts is { IsCancellationRequested: false } || _controller.IsBusy;
        foreach (Control control in _operationControls) control.IsEnabled = !busy;
        foreach (Control control in _configurationTabs) control.IsEnabled = !busy;
        _status.Text = _controller.Status;
        _status.MaxLines = 2;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTipService.SetToolTip(_status, _status.Text);
        if (_lastStatus != _status.Text)
        {
            _hasStatus = _lastStatus != null;
            _lastStatus = _status.Text;
            _statusPageIndex = _operationPageIndex;
            AddLog(_status.Text);
        }
        ProviderProfile? active = _controller.Settings.Profiles.FirstOrDefault(p => p.Id == _controller.Settings.ActiveProfileId);
        _activeChannel.Text = active == null ? "尚未连接云盘" : $"当前云盘：{active.Name} · {ProviderLabel(active.Kind)}";
        _saveState.Text = (_settingsDirty || _profileDirty) ? "有未保存的修改" : "设置已保存";
        _saveState.Visibility = (_settingsDirty || _profileDirty) ? Visibility.Visible : Visibility.Collapsed;
        UpdateFeedbackVisibility();
    }

    private void UpdateFeedbackVisibility()
    {
        bool feedbackVisible = _feedback.IsOpen && _feedbackPageIndex == _navigation.SelectedIndex;
        _feedback.Visibility = feedbackVisible ? Visibility.Visible : Visibility.Collapsed;
        bool statusVisible = _hasStatus && !string.IsNullOrWhiteSpace(_status.Text) && !feedbackVisible &&
            (_controller.IsBusy || _statusPageIndex == null || _statusPageIndex == _navigation.SelectedIndex);
        _status.Visibility = statusVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MarkSettingsDirty()
    {
        if (_loading) return;
        _settingsDirty = true;
        RefreshStatus();
    }

    private void MarkSettingsSaved()
    {
        _settingsDirty = false;
        RefreshStatus();
    }

    private void AddLog(string text)
    {
        _logEntries.Add($"{DateTime.Now:HH:mm:ss}  {text}");
        if (_logEntries.Count > 60) _logEntries.RemoveAt(0);
        _logText.Text = string.Join(Environment.NewLine, _logEntries.AsEnumerable().Reverse());
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, bool showSuccess = true, string? successMessage = null, int? pageIndex = null)
    {
        if (_operationCts != null || _controller.IsBusy)
        {
            ShowMessage("请等待当前操作完成，或先取消当前操作。", InfoBarSeverity.Informational);
            return;
        }
        using var cancellation = new CancellationTokenSource();
        _operationCts = cancellation;
        _operationPageIndex = pageIndex ?? _navigation.SelectedIndex;
        _operationDismissed = false;
        _feedback.IsOpen = false;
        _hasStatus = false;
        RefreshStatus();
        try
        {
            await action(cancellation.Token);
            if (showSuccess && !_feedback.IsOpen && !_operationDismissed) ShowMessage(successMessage ?? "操作已完成。", InfoBarSeverity.Success);
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
            _operationPageIndex = null;
            RefreshPendingView();
            RefreshLibraryView();
        }
    }

    private void ShowMessage(string message, InfoBarSeverity severity)
    {
        _feedback.Severity = severity;
        _feedback.Message = message;
        _feedbackPageIndex = _operationCts != null ? _operationPageIndex : _navigation.SelectedIndex;
        _hasStatus = false;
        _feedback.IsOpen = true;
        UpdateFeedbackVisibility();
        AddLog(message);
    }

    private Button ActionButton(string text, Func<CancellationToken, Task> action, bool trackBusy = true, string? successMessage = null)
    {
        var button = new Button { Content = text };
        button.Click += async (_, _) => await RunAsync(action, showSuccess: successMessage != null, successMessage: successMessage);
        if (trackBusy) _operationControls.Add(button);
        return button;
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
        dialog.Closed += (_, args) => { if (args.Result == ContentDialogResult.None) _operationDismissed = true; };
        dialog.Resources["ContentDialogMinWidth"] = Math.Min(500d, Math.Max(280d, (XamlRoot?.Size.Width ?? 760) - 48));
        dialog.Resources["ContentDialogMaxWidth"] = 760d;
        dialog.Resources["ContentDialogMaxHeight"] = 680d;
        return dialog;
    }

    private static StackPanel Stack(double spacing = 12) => new() { Spacing = spacing };
    private static Panel Row() => new ActionRow();
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
