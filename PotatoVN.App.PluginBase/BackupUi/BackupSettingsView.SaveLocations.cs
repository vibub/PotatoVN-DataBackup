using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.Backup.Core;

namespace PotatoVN.App.PluginBase.BackupUi;

public sealed partial class BackupSettingsView
{
    private readonly ComboBox _saveGame = new() { Header = "选择游戏", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListView _saveRoots = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 250 };

    private void BuildSaveLocationControls(StackPanel panel)
    {
        panel.Children.Add(_saveGame);
        panel.Children.Add(_saveRoots);
        var actions = Row();
        actions.Children.Add(ActionButton("添加位置", async _ => await EditSaveLocationAsync(null)));
        actions.Children.Add(ActionButton("编辑位置", async _ =>
        {
            if (_saveRoots.SelectedItem is not SaveLocationChoice choice) throw new InvalidOperationException("请先选择一个存档位置。");
            await EditSaveLocationAsync(choice.Location);
        }));
        actions.Children.Add(ActionButton("移除位置", async _ =>
        {
            if (Choice(_saveGame) is not Guid gameId || _saveRoots.SelectedItem is not SaveLocationChoice choice)
                throw new InvalidOperationException("请先选择一个存档位置。");
            if (await DialogAsync("移除存档位置", Note("停止备份这个位置？本机存档和云端备份都不会删除。"), "移除") != ContentDialogResult.Primary) return;
            _controller.Settings.SaveConfigs.First(c => c.GameId == gameId).Locations.Remove(choice.Location);
            _controller.Settings.CustomSavePaths.Remove(choice.Location.Id);
            await _controller.SaveSettingsAsync();
            RefreshSaveLocations();
        }));
        panel.Children.Add(actions);
        _saveGame.SelectionChanged += (_, _) => RefreshSaveLocations();
        RefreshSaveGameChoices();
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
        if (Choice(_saveGame) is not Guid gameId) return;
        GameSaveConfig? config = _controller.Settings.SaveConfigs.FirstOrDefault(c => c.GameId == gameId);
        if (config == null) return;
        foreach (SaveLocation location in config.Locations)
        {
            string path = location.BaseKind == SaveBaseKind.Custom
                ? _controller.Settings.CustomSavePaths.GetValueOrDefault(location.Id) ?? location.CustomPath ?? "未指定本机路径"
                : $"{SaveBaseLabel(location.BaseKind)}\\{location.RelativePath}";
            _saveRoots.Items.Add(new SaveLocationChoice(location, $"{(location.IsFile ? "文件" : "目录")} · {path}"));
        }
    }

    private async Task EditSaveLocationAsync(SaveLocation? existing)
    {
        if (Choice(_saveGame) is not Guid gameId) throw new InvalidOperationException("请先选择一个游戏。");
        var kind = new ComboBox { Header = "位置基准", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (SaveBaseKind value in Enum.GetValues<SaveBaseKind>()) AddChoice(kind, value, SaveBaseLabel(value));
        SelectChoice(kind, existing?.BaseKind ?? SaveBaseKind.GameRoot);
        var relative = TextInput("相对位置", "例如：savedata 或 Company\\Title");
        relative.Text = existing?.RelativePath ?? "";
        var custom = TextInput("本机绝对路径", "例如：D:\\GameSaves\\Title");
        custom.Text = existing == null ? "" : _controller.Settings.CustomSavePaths.GetValueOrDefault(existing.Id) ?? existing.CustomPath ?? "";
        var isFile = new CheckBox { Content = "这是单个文件（不勾选则为目录）", IsChecked = existing?.IsFile ?? false };
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };
        var body = Stack();
        body.Children.Add(Note("请指定游戏实际使用的存档位置。系统目录会按本机当前用户解析；自定义绝对路径只保存在本机。"));
        foreach (UIElement item in new UIElement[] { kind, relative, custom, isFile, error }) body.Children.Add(item);
        void Update()
        {
            bool absolute = Choice(kind) is SaveBaseKind.Custom;
            relative.Visibility = absolute ? Visibility.Collapsed : Visibility.Visible;
            custom.Visibility = absolute ? Visibility.Visible : Visibility.Collapsed;
        }
        kind.SelectionChanged += (_, _) => Update();
        Update();
        var dialog = NewDialog(existing == null ? "添加存档位置" : "编辑存档位置", body, "保存位置");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            bool absolute = Choice(kind) is SaveBaseKind.Custom;
            string candidate = relative.Text.Trim();
            string? message = absolute
                ? (Path.IsPathFullyQualified(custom.Text.Trim()) ? null : "请填写完整的本机绝对路径。")
                : (string.IsNullOrWhiteSpace(candidate) || candidate == "." || Path.IsPathRooted(candidate) ||
                    candidate.Split(['\\', '/']).Any(part => part == "..")
                    ? "请填写基准目录下的存档相对路径，不可选择整个基准目录或使用上级路径。" : null);
            if (message == null) return;
            error.Message = message;
            error.IsOpen = true;
            args.Cancel = true;
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        SaveLocation location = existing ?? new SaveLocation();
        location.BaseKind = (SaveBaseKind)Choice(kind)!;
        location.RelativePath = location.BaseKind == SaveBaseKind.Custom ? "" : relative.Text.Trim();
        location.IsFile = isFile.IsChecked == true;
        if (location.BaseKind == SaveBaseKind.Custom)
        {
            location.CustomPath = custom.Text.Trim();
            _controller.Settings.CustomSavePaths[location.Id] = location.CustomPath;
        }
        else
        {
            location.CustomPath = null;
            _controller.Settings.CustomSavePaths.Remove(location.Id);
        }
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
        SaveBaseKind.GameRoot => "游戏根目录", SaveBaseKind.AppData => "%AppData%",
        SaveBaseKind.LocalAppData => "%LocalAppData%", SaveBaseKind.LocalLow => "%LocalLow%",
        SaveBaseKind.Documents => "文档目录", _ => "自定义绝对路径"
    };
    private sealed record SaveLocationChoice(SaveLocation Location, string Label)
    {
        public override string ToString() => Label;
    }
}
