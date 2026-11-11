using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.Backup.Core;

namespace PotatoVN.App.PluginBase.BackupUi;

// 搜索文字只用于查找。只有明确选择候选项，才提交游戏 ID。
public sealed class GameSearchPicker : StackPanel
{
    public AutoSuggestBox SearchBox { get; } = new()
    {
        PlaceholderText = "输入游戏名、目录或来源 ID",
        TextMemberPath = nameof(GameSearchItem.Name),
        MaxSuggestionListHeight = 280,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Opacity = .8 };
    private List<GameSearchItem> _items = new();
    private GameSearchItem? _selected;
    public bool HasPendingQuery => SearchBox.Text != (_selected?.Name ?? "");
    public Guid? SelectedId
    {
        get => _selected?.Id;
        set => Select(_items.FirstOrDefault(item => item.Id == value));
    }
    public event EventHandler? SelectionChanged;

    public GameSearchPicker(string header)
    {
        Spacing = 4;
        Children.Add(new TextBlock { Text = header, TextWrapping = TextWrapping.Wrap });
        Children.Add(SearchBox);
        Children.Add(_hint);
        AutomationProperties.SetName(SearchBox, header);
        SearchBox.TextChanged += (_, args) =>
        {
            // 方向键预览候选时保持原筛选，只在输入变化后更新列表。
            if (args.Reason != AutoSuggestionBoxTextChangeReason.SuggestionChosen) UpdateSuggestions();
        };
        SearchBox.GotFocus += (_, args) =>
        {
            UpdateSuggestions();
            SearchBox.IsSuggestionListOpen = true;
            if (args.OriginalSource is TextBox input) input.SelectAll();
        };
        SearchBox.QuerySubmitted += (_, args) =>
        {
            if (args.ChosenSuggestion is GameSearchItem item) Select(item);
            else if (HasPendingQuery) _hint.Text = "请选择一个搜索结果。";
        };
        SearchBox.KeyDown += (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Escape) return;
            Select(_selected);
            SearchBox.IsSuggestionListOpen = false;
            args.Handled = true;
        };
    }

    public void SetItems(IEnumerable<GameSearchItem> items, Guid? selectedId)
    {
        _items = items.ToList();
        SelectedId = selectedId;
    }

    public void UpdateDetails(Func<GameSearchItem, string> describe)
    {
        _items = _items.Select(item => item with { Detail = describe(item) }).ToList();
        _selected = _items.FirstOrDefault(item => item.Id == _selected?.Id);
        UpdateSuggestions();
    }

    private void Select(GameSearchItem? item)
    {
        Guid? previous = _selected?.Id;
        _selected = item;
        SearchBox.Text = item?.Name ?? "";
        SearchBox.ItemsSource = _items;
        SearchBox.IsSuggestionListOpen = false;
        _hint.Text = item?.Detail ?? (_items.Count == 0 ? "暂无可选游戏。" : "请选择游戏。");
        if (previous != item?.Id) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSuggestions()
    {
        var matches = _items.Where(item => !HasPendingQuery ||
            item.Id == null || GameSearch.Matches(SearchBox.Text, item.ToString())).ToArray();
        SearchBox.ItemsSource = matches;
        _hint.Text = HasPendingQuery
            ? matches.Any(item => item.Id != null) ? "请选择结果。" : "无匹配游戏。"
            : _selected?.Detail ?? "请选择游戏。";
    }
}

public sealed record GameSearchItem(Guid? Id, string Name, string Detail)
{
    public static GameSearchItem FromGame(GameIdentity game) =>
        new(game.Id, game.Name, GameSearch.Describe(game));
    public static GameSearchItem FromCloudGame(GameIdentity game) =>
        new(game.Id, game.Name, game.SourceIds.Count == 0 ? "云端游戏资料" :
            string.Join(" / ", game.SourceIds.Select(pair => pair.Key + ": " + pair.Value)));
    public override string ToString() => string.IsNullOrWhiteSpace(Detail) ? Name : $"{Name} · {Detail}";
}

internal static class GameSearch
{
    public static bool Matches(string search, string text) =>
        search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).All(term =>
            CultureInfo.CurrentCulture.CompareInfo.IndexOf(text, term,
                CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType) >= 0);

    public static bool Matches(string search, GameIdentity game) =>
        Matches(search, $"{game.Name} {game.LocalDirectory} {string.Join(" ", game.SourceIds.Select(pair => pair.Key + ":" + pair.Value))}");

    public static string Describe(GameIdentity game)
    {
        string directory = string.IsNullOrWhiteSpace(game.LocalDirectory) || !Directory.Exists(game.LocalDirectory)
            ? "未关联" : "已关联";
        string sources = string.Join(" / ", game.SourceIds.Select(pair => pair.Key + ": " + pair.Value));
        return sources.Length == 0 ? directory : directory + " · " + sources;
    }
}
