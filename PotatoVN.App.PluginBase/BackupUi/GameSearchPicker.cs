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

// 搜索只筛选可见列表；点击条目或键盘选择后才确认游戏 ID。
public sealed class GameSearchPicker : StackPanel
{
    public TextBox SearchBox { get; } = new() { PlaceholderText = "游戏名、目录或来源 ID", HorizontalAlignment = HorizontalAlignment.Stretch };
    public ListView Results { get; } = new() { SelectionMode = ListViewSelectionMode.Single,
        HorizontalContentAlignment = HorizontalAlignment.Stretch, MaxHeight = 220 };
    public StackPanel Filters { get; } = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
    public CheckBox OnlyLocal { get; } = new() { Content = "本机目录有效", Visibility = Visibility.Collapsed };
    public Func<GameSearchItem, bool>? Filter { get; set; }
    private bool _rendering;
    private readonly TextBlock _count = new() { FontSize = 12, Opacity = .8, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _hint = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = .8 };
    private List<GameSearchItem> _items = new();
    private GameSearchItem? _selected;
    private string _confirmedQuery = "";
    public bool HasPendingQuery => SearchBox.Text != _confirmedQuery;
    public Guid? SelectedId
    {
        get => _selected?.Id;
        set => Select(_items.FirstOrDefault(item => item.Id == value), true);
    }
    public event EventHandler? SelectionChanged;

    public GameSearchPicker(string header)
    {
        Spacing = 8;
        Children.Add(new TextBlock { Text = header, TextWrapping = TextWrapping.Wrap });
        Children.Add(SearchBox);
        Filters.Children.Add(OnlyLocal);
        Children.Add(Filters);
        Children.Add(_count);
        Children.Add(Results);
        Children.Add(_hint);
        AutomationProperties.SetName(SearchBox, header);
        AutomationProperties.SetName(Results, header + "列表");
        Results.SelectionChanged += (_, _) =>
        {
            if (!_rendering && Results.SelectedItem is ListViewItem { Tag: GameSearchItem item }) Select(item);
        };
        OnlyLocal.Checked += (_, _) => Refresh();
        OnlyLocal.Unchecked += (_, _) => Refresh();
        SearchBox.TextChanged += (_, _) => Refresh();
        SearchBox.KeyDown += (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Escape) return;
            SearchBox.Text = _confirmedQuery;
            args.Handled = true;
        };
    }

    public void SetItems(IEnumerable<GameSearchItem> items, Guid? selectedId)
    {
        _items = items.ToList();
        OnlyLocal.Visibility = _items.Any(item => item.IsLocal) ? Visibility.Visible : Visibility.Collapsed;
        SelectedId = selectedId;
    }

    public void UpdateDetails(Func<GameSearchItem, string> describe)
    {
        _items = _items.Select(item => item with { Detail = describe(item) }).ToList();
        _selected = _items.FirstOrDefault(item => item.Id == _selected?.Id);
        Refresh();
    }

    private void Select(GameSearchItem? item, bool clearQuery = false)
    {
        Guid? previous = _selected?.Id;
        _selected = item;
        if (clearQuery) SearchBox.Text = "";
        _confirmedQuery = SearchBox.Text;
        // 点击列表时保留滚动位置与键盘焦点，程序切换目标才重建选择。
        if (clearQuery) Refresh();
        else UpdateHint();
        if (previous != item?.Id) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Refresh()
    {
        var matches = _items.Where(item =>
            (Filter == null || Filter(item)) && (OnlyLocal.IsChecked != true || item.Id == null || item.IsLocal) &&
            (item.Id == null || GameSearch.Matches(SearchBox.Text, item + " " + item.SearchTerms))).ToArray();
        _rendering = true;
        Results.Items.Clear();
        foreach (GameSearchItem item in matches)
        {
            ListViewItem row = BackupSettingsView.GameListItem(new GameIdentity { Id = item.Id ?? Guid.Empty, Name = item.Name },
                item.Badge.Length > 0 ? item.Badge : item.Id == null ? "不关联" : "云端资料", item.Tone);
            row.Tag = item;
            if (item.MatchBadge.Length > 0)
            {
                var badges = ((StackPanel)row.Content).Children.OfType<Panel>().Single();
                badges.Children.Add(BackupSettingsView.StatusBadge(item.MatchBadge,
                    item.MatchBadge == "未匹配" ? BackupSettingsView.StatusTone.Warning : BackupSettingsView.StatusTone.Info));
            }
            Results.Items.Add(row);
            if (!HasPendingQuery && item == _selected) Results.SelectedItem = row;
        }
        _rendering = false;
        if (Results.SelectedItem != null) Results.ScrollIntoView(Results.SelectedItem);
        _count.Text = matches.Length == 0 ? "无匹配游戏。" : $"显示 {matches.Count(item => item.Id != null)} 款游戏";
        UpdateHint();
    }

    private void UpdateHint()
    {
        _hint.Text = HasPendingQuery ? "请选择列表中的游戏。" : _selected?.Detail ?? "请选择游戏。";
        bool visible = Results.Items.OfType<ListViewItem>().Any(row => ((GameSearchItem)row.Tag).Id == _selected?.Id);
        if (_selected != null) _hint.Text += " 已选：" + _selected.Name + (!visible ? "（在筛选外）" : "");
    }
}

public sealed record GameSearchItem(Guid? Id, string Name, string Detail)
{
    public string Badge { get; init; } = "";
    public string MatchBadge { get; init; } = "";
    public bool IsLocal { get; init; }
    internal string SearchTerms { get; init; } = "";
    internal BackupSettingsView.StatusTone Tone { get; init; } = BackupSettingsView.StatusTone.Neutral;
    public static GameSearchItem FromGame(GameIdentity game) =>
        new(game.Id, game.Name, GameSearch.Describe(game)) { IsLocal = Directory.Exists(game.LocalDirectory), SearchTerms = SearchText(game),
            Badge = Directory.Exists(game.LocalDirectory) ? "已关联" : "未关联",
            Tone = Directory.Exists(game.LocalDirectory) ? BackupSettingsView.StatusTone.Info : BackupSettingsView.StatusTone.Warning };
    public static GameSearchItem FromCloudGame(GameIdentity game) =>
        new(game.Id, game.Name, game.SourceIds.Count == 0 ? "云端游戏资料" :
            string.Join(" / ", game.SourceIds.Select(pair => pair.Key + ": " + pair.Value))) { Badge = "云端资料", Tone = BackupSettingsView.StatusTone.Info, SearchTerms = SearchText(game) };
    private static string SearchText(GameIdentity game) => game.LocalDirectory + " " + string.Join(" ", game.SourceIds.Select(pair => pair.Key + ":" + pair.Value));
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
