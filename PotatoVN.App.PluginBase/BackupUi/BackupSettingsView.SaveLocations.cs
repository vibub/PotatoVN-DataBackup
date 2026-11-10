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

    private void BuildSaveLocationControls(StackPanel panel)
    {
        panel.Children.Add(_saveGame);
        panel.Children.Add(_saveLocationHint);
        panel.Children.Add(_saveRoots);
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
            { ShowMessage("存档位置保持原样。", InfoBarSeverity.Informational); return; }
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
        else ShowMessage("未添加存档位置。", InfoBarSeverity.Informational);
    }

    private void RefreshSaveGameChoices()
    {
        object? selected = Choice(_saveGame);
        _saveGame.Items.Clear();
        foreach (GameIdentity game in _controller.GetGames().OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
            AddChoice(_saveGame, game.Id, GameLabel(game));
        SelectChoice(_saveGame, selected);
        if (_saveGame.SelectedItem == null && _saveGame.Items.Count > 0) _saveGame.SelectedIndex = 0;
        RefreshSaveLocations();
    }

    private void RefreshSaveLocations()
    {
        _saveRoots.Items.Clear();
        if (Choice(_saveGame) is not Guid gameId)
        {
            _saveLocationHint.Text = "暂无游戏。请先在 PotatoVN 中添加游戏，再配置存档位置。";
            return;
        }
        GameIdentity? game = _controller.GetGames().FirstOrDefault(g => g.Id == gameId);
        GameSaveConfig? config = _controller.Settings.SaveConfigs.FirstOrDefault(c => c.GameId == gameId);
        int count = config?.Locations.Count ?? 0;
        _saveLocationHint.Text = count == 0
            ? "尚未添加存档位置。选择游戏实际使用的存档文件夹或文件即可；游戏本体不会上传。"
            : $"已配置 {count} 个存档位置。请确认这些位置确实保存了游戏进度；可选中后编辑或移除。";
        if (config == null) return;
        foreach (SaveLocation location in config.Locations)
        {
            string path = SaveLocationPath(location, game) ?? $"{SaveBaseLabel(location.BaseKind)}\\{location.RelativePath}";
            _saveRoots.Items.Add(new SaveLocationChoice(location, $"{(location.IsFile ? "文件" : "文件夹")} · {path}"));
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
        var isFile = new CheckBox { Content = "备份单个文件（未勾选时备份整个存档文件夹）", IsChecked = existing?.IsFile ?? pickedFile };
        var preview = Note("");
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };
        var body = Stack();
        body.Children.Add(Note($"游戏：{game.Name}\n选择游戏实际使用的存档位置。插件会自动识别游戏目录、AppData 和文档目录，换设备时按新位置解析。"));
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
                full = Path.GetFullPath(full);
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
                        string.Equals(Path.GetFullPath(root).TrimEnd('\\', '/'), full.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("请选择游戏专用的存档文件或子文件夹，不能备份整个磁盘、游戏目录或用户基础目录。");
                if (isFile.IsChecked == true && Directory.Exists(full)) throw new InvalidOperationException("该路径是文件夹，请取消勾选“备份单个文件”。");
                if (isFile.IsChecked != true && File.Exists(full)) throw new InvalidOperationException("该路径是文件，请勾选“备份单个文件”。");
                selected = SaveArchiveService.Describe(full, game.LocalDirectory, isFile.IsChecked == true);
                error.IsOpen = false;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or NotSupportedException)
            {
                error.Message = exception.Message; error.IsOpen = true; args.Cancel = true;
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || selected == null)
        { ShowMessage("未保存存档位置。", InfoBarSeverity.Informational); return; }
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
