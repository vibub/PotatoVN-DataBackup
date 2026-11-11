using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PotatoVN.Backup.Core;
using PotatoVN.Backup.Core.Engine;

namespace PotatoVN.App.PluginBase.BackupUi;

public sealed partial class BackupSettingsView
{
    private readonly ListView _saveGame = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 360, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _saveGameSearch = TextInput("搜索存档游戏", "游戏名、目录或来源 ID");
    private readonly CheckBox _saveGameConfigured = new() { Content = "已配置存档" };
    private readonly CheckBox _saveGameUnconfigured = new() { Content = "未配置存档" };
    private readonly CheckBox _saveGameLocal = new() { Content = "本机目录有效" };
    private readonly TextBlock _saveGameCount = Note("");
    private readonly TextBlock _saveGameEmpty = Note("");
    private readonly TextBlock _saveGameTitle = Heading("", "BodyStrongTextBlockStyle");
    private readonly StackPanel _saveGameDetails = Stack();
    private readonly Grid _saveGameLayout = new() { ColumnSpacing = 20, RowSpacing = 16 };
    private IReadOnlyList<GameIdentity> _saveGames = Array.Empty<GameIdentity>();
    private Guid? _saveGameId;
    private static Guid? _lastSaveGameId;
    private bool _refreshingSaveGames;
    private readonly ListView _saveRoots = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 250 };
    private readonly TextBlock _saveLocationHint = Note("");
    private readonly Border _saveCover = new() { Width = 96, Height = 136, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Top };
    private readonly StackPanel _saveGameBadges = Stack(6);
    private readonly StackPanel _saveGameBrowser = Stack(8);
    private Guid? _coverGameId;
    private readonly StackPanel _saveSuggestions = Stack(8);

    private void BuildSaveLocationControls(StackPanel panel)
    {
        var toolbar = Stack(8);
        toolbar.Children.Add(_saveGameSearch);
        var filters = Row();
        filters.Children.Add(_saveGameConfigured);
        filters.Children.Add(_saveGameUnconfigured);
        filters.Children.Add(_saveGameLocal);
        toolbar.Children.Add(filters);
        toolbar.Children.Add(_saveGameCount);
        _saveGameBrowser.Children.Add(_saveGame);
        _saveGameBrowser.Children.Add(_saveGameEmpty);
        _saveGameLayout.ColumnDefinitions.Add(new ColumnDefinition());
        _saveGameLayout.ColumnDefinitions.Add(new ColumnDefinition());
        for (int index = 0; index < 3; index++)
            _saveGameLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _saveGameLayout.Children.Add(toolbar);
        Grid.SetRow(_saveGameBrowser, 1);
        _saveGameLayout.Children.Add(_saveGameBrowser);
        _saveGameLayout.Children.Add(_saveGameDetails);
        void Arrange()
        {
            bool wide = _saveGameLayout.ActualWidth >= 780;
            _saveGameLayout.ColumnDefinitions[0].Width = wide ? new GridLength(300) : new GridLength(1, GridUnitType.Star);
            _saveGameLayout.ColumnDefinitions[1].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(_saveGameDetails, wide ? 1 : 0);
            Grid.SetRow(_saveGameDetails, wide ? 1 : 2);
        }
        _saveGameLayout.SizeChanged += (_, _) => Arrange();
        Arrange();
        panel.Children.Add(_saveGameLayout);
        panel = _saveGameDetails;
        var header = new Grid { ColumnSpacing = 16 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(_saveCover);
        var identity = Stack(10);
        identity.Children.Add(_saveGameTitle);
        identity.Children.Add(_saveGameBadges);
        Grid.SetColumn(identity, 1);
        header.Children.Add(identity);
        panel.Children.Add(header);
        panel.Children.Add(_saveLocationHint);
        panel.Children.Add(_saveRoots);
        panel.Children.Add(_saveSuggestions);
        panel.Children.Add(ActionButton("重新检查存档位置", _ => { _coverGameId = null; RefreshSaveLocations(); return Task.CompletedTask; }));
        var addActions = Row();
        addActions.Children.Add(ActionButton("选择存档文件夹…", async _ => await AddPickedSaveLocationAsync(false), successMessage: "存档位置已保存。"));
        addActions.Children.Add(ActionButton("选择存档文件…", async _ => await AddPickedSaveLocationAsync(true), successMessage: "存档位置已保存。"));
        addActions.Children.Add(ActionButton("手动填写位置…", async _ => await EditSaveLocationAsync(null), successMessage: "存档位置已保存。"));
        panel.Children.Add(addActions);
        var actions = Row();
        actions.Children.Add(ActionButton("编辑所选位置…", async _ =>
        {
            if (_saveRoots.SelectedItem is not ListViewItem { Tag: SaveLocationChoice choice }) throw new InvalidOperationException("请先选择一个存档位置。");
            await EditSaveLocationAsync(choice.Location);
        }, successMessage: "存档位置已保存。"));
        actions.Children.Add(ActionButton("移除所选位置", async _ =>
        {
            if (_saveGameId is not Guid gameId || _saveRoots.SelectedItem is not ListViewItem { Tag: SaveLocationChoice choice })
                throw new InvalidOperationException("请先选择一个存档位置。");
            if (await DialogAsync("移除存档位置", Note("停止备份这个位置？本机存档和云端备份都不会删除。"), "移除") != ContentDialogResult.Primary)
                return;
            _controller.Settings.SaveConfigs.First(c => c.GameId == gameId).Locations.Remove(choice.Location);
            _controller.Settings.CustomSavePaths.Remove(choice.Location.Id);
            await _controller.SaveSettingsAsync();
            RefreshSaveLocations();
        }, successMessage: "已停止备份所选位置。"));
        panel.Children.Add(actions);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_saveGame, "配置存档的游戏");
        _saveGame.SelectionChanged += (_, _) =>
        {
            if (_refreshingSaveGames || _saveGame.SelectedItem is not ListViewItem { Tag: GameIdentity game }) return;
            _saveGameId = _lastSaveGameId = game.Id;
            RefreshSaveLocations();
        };
        _saveGameSearch.TextChanged += (_, _) => RenderSaveGameChoices();
        void FilterChanged(object sender, RoutedEventArgs args)
        {
            if (ReferenceEquals(sender, _saveGameConfigured) && _saveGameConfigured.IsChecked == true)
                _saveGameUnconfigured.IsChecked = false;
            else if (ReferenceEquals(sender, _saveGameUnconfigured) && _saveGameUnconfigured.IsChecked == true)
                _saveGameConfigured.IsChecked = false;
            RenderSaveGameChoices();
        }
        foreach (CheckBox filter in new[] { _saveGameConfigured, _saveGameUnconfigured, _saveGameLocal })
        {
            filter.Checked += FilterChanged;
            filter.Unchecked += FilterChanged;
        }
        RefreshSaveGameChoices();
    }

    private async Task AddPickedSaveLocationAsync(bool isFile)
    {
        if (_saveGameId is not Guid gameId) throw new InvalidOperationException("请先选择一个游戏。");
        string? gameDirectory = _controller.GetGames().First(game => game.Id == gameId).LocalDirectory;
        string? path = await PickPathAsync(isFile, gameDirectory);
        if (path != null) await EditSaveLocationAsync(null, path, isFile);
        else _operationDismissed = true;
    }

    private void RefreshSaveGameChoices()
    {
        _saveGames = _controller.GetGames().OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        Guid? previous = _saveGameId ?? _lastSaveGameId;
        _saveGameId = _saveGames.FirstOrDefault(game => game.Id == previous)?.Id ?? _saveGames.FirstOrDefault()?.Id;
        _lastSaveGameId = _saveGameId;
        RefreshSaveLocations();
    }

    private void RenderSaveGameChoices()
    {
        var counts = _controller.Settings.SaveConfigs.ToDictionary(config => config.GameId, config => config.Locations.Count);
        GameIdentity[] visible = _saveGames.Where(game => GameSearch.Matches(_saveGameSearch.Text, game) &&
            (_saveGameConfigured.IsChecked != true || counts.GetValueOrDefault(game.Id) > 0) &&
            (_saveGameUnconfigured.IsChecked != true || counts.GetValueOrDefault(game.Id) == 0) &&
            (_saveGameLocal.IsChecked != true || Directory.Exists(game.LocalDirectory))).ToArray();
        _refreshingSaveGames = true;
        // 只改状态文字时复用原行，保留列表的滚动位置。
        bool sameRows = _saveGame.Items.OfType<ListViewItem>().Select(item => ((GameIdentity)item.Tag).Id).SequenceEqual(visible.Select(game => game.Id));
        if (!sameRows) _saveGame.Items.Clear();
        for (int index = 0; index < visible.Length; index++)
        {
            GameIdentity game = visible[index];
            int count = counts.GetValueOrDefault(game.Id);
            string state = count == 0 ? "未配置存档" : $"已配置 {count} 处";
            ListViewItem row = GameListItem(game, state, count == 0 ? StatusTone.Neutral : StatusTone.Success,
                !Directory.Exists(game.LocalDirectory));
            if (sameRows)
            {
                var existing = (ListViewItem)_saveGame.Items[index];
                object content = row.Content;
                row.Content = null;
                existing.Content = content;
                existing.Tag = game;
            }
            else _saveGame.Items.Add(row);
        }
        _saveGame.SelectedItem = _saveGame.Items.OfType<ListViewItem>().FirstOrDefault(item => ((GameIdentity)item.Tag).Id == _saveGameId);
        _refreshingSaveGames = false;
        _saveGameCount.Text = $"{visible.Length} / {_saveGames.Count} 款游戏";
        _saveGameEmpty.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _saveGameEmpty.Text = _saveGames.Count == 0 ? "暂无游戏。" : "无匹配游戏。";
    }

    private void RefreshSaveLocations()
    {
        _saveRoots.Items.Clear();
        _saveSuggestions.Children.Clear();
        RenderSaveGameChoices();
        _saveGameDetails.Visibility = _saveGameId == null ? Visibility.Collapsed : Visibility.Visible;
        if (_saveGameId is not Guid gameId)
        {
            _saveLocationHint.Text = "暂无游戏。请先在 PotatoVN 中添加游戏，再配置存档位置。";
            return;
        }
        GameIdentity? game = _controller.GetGames().FirstOrDefault(g => g.Id == gameId);
        GameSaveConfig? config = _controller.Settings.SaveConfigs.FirstOrDefault(c => c.GameId == gameId);
        _saveGameTitle.Text = game?.Name ?? "游戏已不在当前游戏库中";
        int count = config?.Locations.Count ?? 0;
        _saveGameBadges.Children.Clear();
        _saveGameBadges.Children.Add(StatusBadge(count == 0 ? "未配置存档" : $"已配置 {count} 处",
            count == 0 ? StatusTone.Neutral : StatusTone.Success));
        if (!Directory.Exists(game?.LocalDirectory)) _saveGameBadges.Children.Add(StatusBadge("未关联", StatusTone.Warning));
        RefreshGameCover(gameId);
        var configuredPaths = (config?.Locations ?? new List<SaveLocation>())
            .Select(location => SaveLocationPath(location, game)).Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path!))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<SavePathSuggestion> suggestions = _controller.GetSavePathSuggestions(gameId);
        SavePathSuggestion[] available = suggestions
            .Where(candidate => !configuredPaths.Contains(Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate.Path))))
            .DistinctBy(candidate => Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate.Path)), StringComparer.OrdinalIgnoreCase).ToArray();
        _saveLocationHint.Text = count == 0
            ? available.Length > 0 ? $"{available.Length} 个候选位置待确认" : "添加存档文件夹或文件。"
            : "";
        _saveLocationHint.Visibility = string.IsNullOrEmpty(_saveLocationHint.Text) ? Visibility.Collapsed : Visibility.Visible;
        if (config != null)
            foreach (SaveLocation location in config.Locations)
            {
                string path = SaveLocationPath(location, game) ?? $"{SaveBaseLabel(location.BaseKind)}\\{location.RelativePath}";
                var row = Stack(6);
                var labels = Row();
                labels.Children.Add(StatusBadge(location.IsFile ? "文件" : "文件夹"));
                if (!File.Exists(path) && !Directory.Exists(path)) labels.Children.Add(StatusBadge("路径不存在", StatusTone.Warning));
                row.Children.Add(labels);
                row.Children.Add(PathText(path));
                _saveRoots.Items.Add(new ListViewItem { Tag = new SaveLocationChoice(location, path), Content = row,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 8, 8, 10), Margin = new Thickness(0, 0, 0, 6) });
            }
        foreach (SavePathSuggestion candidate in available)
        {
            var suggestion = Stack(4);
            suggestion.Children.Add(StatusBadge(candidate.IsFile ? "候选文件" : "候选文件夹", StatusTone.Info));
            suggestion.Children.Add(PathText(candidate.Path));
            var add = new Button { Content = "检查并添加…" };
            add.Click += async (_, _) => await RunAsync(async _ => await EditSaveLocationAsync(null, candidate.Path, candidate.IsFile), successMessage: "存档位置已加入备份。");
            suggestion.Children.Add(add);
            _saveSuggestions.Children.Add(suggestion);
        }
    }

    private void RefreshGameCover(Guid gameId)
    {
        if (_coverGameId == gameId) return;
        _coverGameId = gameId;
        UIElement EmptyCover() => (UIElement)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            "Background=\"{ThemeResource ControlFillColorSecondaryBrush}\">" +
            "<TextBlock Text=\"暂无封面\" FontSize=\"12\" Opacity=\"0.8\" " +
            "HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\"/></Border>");
        _saveCover.Child = EmptyCover();
        string? path = _controller.GetGameCoverPath(gameId);
        if (string.IsNullOrWhiteSpace(path) || !Uri.TryCreate(path, UriKind.Absolute, out Uri? uri) ||
            !uri.IsFile || !File.Exists(uri.LocalPath)) return;
        var image = new Image { Stretch = Stretch.UniformToFill, Source = new BitmapImage(uri) { DecodePixelWidth = 192 } };
        image.ImageFailed += (_, _) => { if (ReferenceEquals(_saveCover.Child, image)) _saveCover.Child = EmptyCover(); };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(image, "游戏封面");
        _saveCover.Child = image;
    }

    private string? SaveLocationPath(SaveLocation location, GameIdentity? game)
    {
        if (location.BaseKind == SaveBaseKind.Custom)
            return _controller.Settings.CustomSavePaths.GetValueOrDefault(location.Id) ?? location.CustomPath;
        string? root = location.BaseKind switch
        {
            SaveBaseKind.GameRoot => game?.LocalDirectory,
            SaveBaseKind.AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            SaveBaseKind.LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            SaveBaseKind.LocalLow => Path.Combine(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))!, "LocalLow"),
            SaveBaseKind.Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            _ => null
        };
        return string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, location.RelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private async Task EditSaveLocationAsync(SaveLocation? existing, string? pickedPath = null, bool pickedFile = false)
    {
        if (_saveGameId is not Guid gameId) throw new InvalidOperationException("请先选择一个游戏。");
        GameIdentity game = _controller.GetGames().First(g => g.Id == gameId);
        var path = TextInput("存档路径", "粘贴完整路径，或点击下方浏览按钮");
        path.Text = pickedPath ?? (existing == null ? "" : SaveLocationPath(existing, game)) ?? "";
        var isFile = new CheckBox { Content = "备份单个文件", IsChecked = existing?.IsFile ?? pickedFile };
        var preview = Note("");
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };
        var body = Stack();
        body.Children.Add(Note($"游戏：{game.Name}"));
        body.Children.Add(path);
        var browse = Row();
        browse.Children.Add(BrowsePathButton(path, false, () => isFile.IsChecked = false, game.LocalDirectory));
        browse.Children.Add(BrowsePathButton(path, true, () => isFile.IsChecked = true, game.LocalDirectory));
        body.Children.Add(browse);
        body.Children.Add(isFile);
        body.Children.Add(preview);
        body.Children.Add(error);
        void UpdatePreview()
        {
            if (!Path.IsPathFullyQualified(path.Text.Trim())) { preview.Text = "请使用游戏专用的存档文件或子文件夹。"; return; }
            try
            {
                SaveLocation description = SaveArchiveService.Describe(path.Text.Trim(), game.LocalDirectory, isFile.IsChecked == true);
                preview.Text = description.BaseKind == SaveBaseKind.Custom
                    ? "此路径只保存在本机；在其他设备恢复时需要重新选择位置。"
                    : $"已自动识别：{SaveBaseLabel(description.BaseKind)} / {description.RelativePath}";
                if (!File.Exists(path.Text.Trim()) && !Directory.Exists(path.Text.Trim())) preview.Text += "\n当前路径还不存在；产生存档后才会上传。";
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            { preview.Text = "请检查存档路径。"; }
        }
        path.TextChanged += (_, _) => UpdatePreview();
        isFile.Checked += (_, _) => UpdatePreview();
        isFile.Unchecked += (_, _) => UpdatePreview();
        UpdatePreview();
        var dialog = NewDialog(existing == null ? "添加存档位置" : "编辑存档位置", body, "保存位置");
        SaveLocation? selected = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                string full = path.Text.Trim();
                if (!Path.IsPathFullyQualified(full)) throw new InvalidOperationException("请选择或填写完整的存档路径。");
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(full));
                string[] protectedRoots = [Path.GetPathRoot(full) ?? "", game.LocalDirectory ?? "",
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Path.Combine(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))!, "LocalLow"),
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)];
                if (protectedRoots.Where(root => !string.IsNullOrWhiteSpace(root)).Any(root =>
                        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), full, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("请选择游戏专用的存档文件或子文件夹，不能备份整个磁盘、游戏目录或用户基础目录。");
                if (isFile.IsChecked == true && Directory.Exists(full)) throw new InvalidOperationException("该路径是文件夹，请取消勾选“备份单个文件”。");
                if (isFile.IsChecked != true && File.Exists(full)) throw new InvalidOperationException("该路径是文件，请勾选“备份单个文件”。");
                GameSaveConfig? configured = _controller.Settings.SaveConfigs.FirstOrDefault(item => item.GameId == gameId);
                bool duplicate = configured?.Locations.Where(item => item.Id != existing?.Id)
                    .Select(item => SaveLocationPath(item, game)).Where(item => !string.IsNullOrWhiteSpace(item))
                    .Any(item => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(item!)), full, StringComparison.OrdinalIgnoreCase)) == true;
                if (duplicate) throw new InvalidOperationException("这个存档位置已加入备份，请编辑已有位置，不需要重复添加。");
                selected = SaveArchiveService.Describe(full, game.LocalDirectory, isFile.IsChecked == true);
                error.IsOpen = false;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or NotSupportedException)
            {
                error.Message = exception.Message; error.IsOpen = true; args.Cancel = true;
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || selected == null)
            return;
        SaveLocation location = existing ?? new SaveLocation();
        location.BaseKind = selected.BaseKind;
        location.RelativePath = selected.RelativePath;
        location.IsFile = selected.IsFile;
        location.CustomPath = selected.CustomPath;
        if (location.CustomPath != null) _controller.Settings.CustomSavePaths[location.Id] = location.CustomPath;
        else _controller.Settings.CustomSavePaths.Remove(location.Id);
        GameSaveConfig? config = _controller.Settings.SaveConfigs.FirstOrDefault(c => c.GameId == gameId);
        if (config == null)
        {
            config = new GameSaveConfig { GameId = gameId };
            _controller.Settings.SaveConfigs.Add(config);
        }
        if (existing == null) config.Locations.Add(location);
        await _controller.SaveSettingsAsync();
        RefreshSaveLocations();
    }

    private static string SaveBaseLabel(SaveBaseKind kind) => kind switch
    {
        SaveBaseKind.GameRoot => "游戏目录", SaveBaseKind.AppData => "AppData（Roaming）",
        SaveBaseKind.LocalAppData => "AppData（Local）", SaveBaseKind.LocalLow => "AppData（LocalLow）",
        SaveBaseKind.Documents => "文档目录", _ => "本机自定义位置"
    };
    private sealed record SaveLocationChoice(SaveLocation Location, string Label)
    {
        public override string ToString() => Label;
    }
}
