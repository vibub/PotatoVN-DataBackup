using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.Backup.Core;
using PotatoVN.Backup.Core.Engine;

namespace PotatoVN.App.PluginBase.BackupUi;

public sealed partial class BackupSettingsView
{
    private readonly ComboBox _saveGame = new() { Header = "选择游戏", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListView _saveRoots = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 250 };
    private readonly TextBlock _saveLocationHint = Note("");
    private readonly TextBlock _saveInstallation = Note("");
    private readonly StackPanel _saveSuggestions = Stack(8);

    private void BuildSaveLocationControls(StackPanel panel)
    {
        panel.Children.Add(_saveGame);
        panel.Children.Add(_saveInstallation);
        panel.Children.Add(_saveLocationHint);
        panel.Children.Add(_saveRoots);
        panel.Children.Add(_saveSuggestions);
        panel.Children.Add(ActionButton("重新检查存档位置", _ => { RefreshSaveLocations(); return Task.CompletedTask; }));
        var addActions = Row();
        addActions.Children.Add(ActionButton("选择存档文件夹…", async _ => await AddPickedSaveLocationAsync(false), successMessage: "存档位置已保存。"));
        addActions.Children.Add(ActionButton("选择存档文件…", async _ => await AddPickedSaveLocationAsync(true), successMessage: "存档位置已保存。"));
        addActions.Children.Add(ActionButton("手动填写位置…", async _ => await EditSaveLocationAsync(null), successMessage: "存档位置已保存。"));
        panel.Children.Add(addActions);
        var actions = Row();
        actions.Children.Add(ActionButton("编辑所选位置…", async _ =>
        {
            if (_saveRoots.SelectedItem is not SaveLocationChoice choice) throw new InvalidOperationException("请先选择一个存档位置。");
            await EditSaveLocationAsync(choice.Location);
        }, successMessage: "存档位置已保存。"));
        actions.Children.Add(ActionButton("移除所选位置", async _ =>
        {
            if (Choice(_saveGame) is not Guid gameId || _saveRoots.SelectedItem is not SaveLocationChoice choice)
                throw new InvalidOperationException("请先选择一个存档位置。");
            if (await DialogAsync("移除存档位置", Note("停止备份这个位置？本机存档和云端备份都不会删除。"), "移除") != ContentDialogResult.Primary)
                return;
            _controller.Settings.SaveConfigs.First(c => c.GameId == gameId).Locations.Remove(choice.Location);
            _controller.Settings.CustomSavePaths.Remove(choice.Location.Id);
            await _controller.SaveSettingsAsync();
            RefreshSaveLocations();
        }, successMessage: "已停止备份所选位置。"));
        panel.Children.Add(actions);
        _saveGame.SelectionChanged += (_, _) => RefreshSaveLocations();
        RefreshSaveGameChoices();
    }

    private async Task AddPickedSaveLocationAsync(bool isFile)
    {
        if (Choice(_saveGame) is not Guid) throw new InvalidOperationException("请先选择一个游戏。");
        string? path = await PickPathAsync(isFile);
        if (path != null) await EditSaveLocationAsync(null, path, isFile);
        else _operationDismissed = true;
    }

    private void RefreshSaveGameChoices()
    {
        object? selected = Choice(_saveGame);
        _saveGame.Items.Clear();
        foreach (GameIdentity game in _controller.GetGames().OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
            AddChoice(_saveGame, game.Id, game.Name);
        SelectChoice(_saveGame, selected);
        if (_saveGame.SelectedItem == null && _saveGame.Items.Count > 0) _saveGame.SelectedIndex = 0;
        RefreshSaveLocations();
    }

    private void RefreshSaveLocations()
    {
        _saveRoots.Items.Clear();
        _saveSuggestions.Children.Clear();
        _saveInstallation.Text = "";
        if (Choice(_saveGame) is not Guid gameId)
        {
            _saveLocationHint.Text = "暂无游戏。请先在 PotatoVN 中添加游戏，再配置存档位置。";
            return;
        }
        GameIdentity? game = _controller.GetGames().FirstOrDefault(g => g.Id == gameId);
        GameSaveConfig? config = _controller.Settings.SaveConfigs.FirstOrDefault(c => c.GameId == gameId);
        _saveInstallation.Text = string.IsNullOrWhiteSpace(game?.LocalDirectory)
            ? "游戏尚未关联本机目录"
            : $"游戏安装目录：{game.LocalDirectory}";
        int count = config?.Locations.Count ?? 0;
        var configuredPaths = (config?.Locations ?? new List<SaveLocation>())
            .Select(location => SaveLocationPath(location, game)).Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path!))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<SavePathSuggestion> suggestions = _controller.GetSavePathSuggestions(gameId);
        SavePathSuggestion[] available = suggestions
            .Where(candidate => !configuredPaths.Contains(Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate.Path))))
            .DistinctBy(candidate => Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate.Path)), StringComparer.OrdinalIgnoreCase).ToArray();
        _saveLocationHint.Text = count == 0
            ? available.Length > 0 ? $"发现 {available.Length} 个候选位置，确认后可加入备份。" : "暂无存档位置。可手动添加，或在游戏中保存进度后重新检查。"
            : $"已加入备份：{count} 个存档位置";
        if (config != null)
            foreach (SaveLocation location in config.Locations)
            {
                string path = SaveLocationPath(location, game) ?? $"{SaveBaseLabel(location.BaseKind)}\\{location.RelativePath}";
                string state = File.Exists(path) || Directory.Exists(path) ? "" : " · 当前路径不存在";
                _saveRoots.Items.Add(new SaveLocationChoice(location, $"{(location.IsFile ? "文件" : "文件夹")}\n{path}{state}"));
            }
        foreach (SavePathSuggestion candidate in available)
        {
            var suggestion = Stack(4);
            suggestion.Children.Add(Heading($"候选位置：{candidate.Source}", "BodyStrongTextBlockStyle"));
            suggestion.Children.Add(Note($"{(candidate.IsFile ? "文件" : "文件夹")}：{candidate.Path}"));
            var add = new Button { Content = "检查并添加…" };
            add.Click += async (_, _) => await RunAsync(async _ => await EditSaveLocationAsync(null, candidate.Path, candidate.IsFile), successMessage: "存档位置已加入备份。");
            suggestion.Children.Add(add);
            _saveSuggestions.Children.Add(suggestion);
        }
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
        if (Choice(_saveGame) is not Guid gameId) throw new InvalidOperationException("请先选择一个游戏。");
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
        browse.Children.Add(BrowsePathButton(path, false, () => isFile.IsChecked = false));
        browse.Children.Add(BrowsePathButton(path, true, () => isFile.IsChecked = true));
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
