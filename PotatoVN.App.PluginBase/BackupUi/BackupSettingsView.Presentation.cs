using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PotatoVN.App.PluginBase.BackupUi;

public sealed partial class BackupSettingsView
{
    internal enum StatusTone { Neutral, Info, Success, Warning, Error }

    internal static Border StatusBadge(string text, StatusTone tone = StatusTone.Neutral)
    {
        var label = new TextBlock { Text = text, FontSize = 12 };
        var badge = new Border { Child = label, CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 3, 7, 3), HorizontalAlignment = HorizontalAlignment.Left };
        void ApplyTheme()
        {
            bool dark = badge.ActualTheme == ElementTheme.Dark;
            (uint foreground, uint background) = (tone, dark) switch
            {
                (StatusTone.Info, false) => (0x155D99u, 0xE5F0FAu),
                (StatusTone.Info, true) => (0xAED4FFu, 0x1C3651u),
                (StatusTone.Success, false) => (0x106A3Bu, 0xE7F4ECu),
                (StatusTone.Success, true) => (0x9DE2B5u, 0x17392Au),
                (StatusTone.Warning, false) => (0x7A4B00u, 0xFFF1CEu),
                (StatusTone.Warning, true) => (0xF7D17Bu, 0x493615u),
                (StatusTone.Error, false) => (0xA4262Cu, 0xFDE9E8u),
                (StatusTone.Error, true) => (0xFFBCC0u, 0x4A2027u),
                (_, false) => (0x4B5058u, 0xECEEF1u),
                _ => (0xD0D3D8u, 0x34383Fu)
            };
            static SolidColorBrush Brush(uint rgb) => new(Windows.UI.Color.FromArgb(255,
                (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            label.Foreground = Brush(foreground);
            badge.Background = Brush(background);
        }
        badge.ActualThemeChanged += (_, _) => ApplyTheme();
        ApplyTheme();
        AutomationProperties.SetName(badge, text);
        return badge;
    }

    private static Grid GameColumns(FrameworkElement cloud, FrameworkElement local)
    {
        var grid = new Grid { ColumnSpacing = 20, RowSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(cloud);
        grid.Children.Add(local);
        void Arrange()
        {
            bool wide = grid.ActualWidth >= 640;
            grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            grid.ColumnDefinitions[1].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(local, wide ? 1 : 0);
            Grid.SetRow(local, wide ? 0 : 1);
        }
        grid.SizeChanged += (_, _) => Arrange();
        Arrange();
        return grid;
    }

    private static TextBlock SecondaryText(string text) =>
        new() { Text = text, FontSize = 12, Opacity = .8, TextWrapping = TextWrapping.Wrap };

    // 按实际可用宽度省略路径中段，完整路径保留在工具提示和无障碍名称中。
    private static TextBlock PathText(string path)
    {
        var block = new TextBlock { Text = path, FontSize = 12, Opacity = .8, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Stretch };
        ToolTipService.SetToolTip(block, path);
        AutomationProperties.SetName(block, path);
        int[] elements = StringInfo.ParseCombiningCharacters(path);
        var measure = new TextBlock { FontSize = block.FontSize, FontFamily = block.FontFamily };
        void Fit()
        {
            if (block.ActualWidth <= 0 || elements.Length < 4) return;
            bool Fits(string value)
            {
                measure.Text = value;
                measure.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                return measure.DesiredSize.Width <= block.ActualWidth;
            }
            if (Fits(path)) { block.Text = path; return; }
            string Shorten(int length)
            {
                int head = Math.Max(1, length * 2 / 5);
                int tail = Math.Max(1, length - head);
                return path[..elements[head]] + "…" + path[elements[^tail]..];
            }
            int low = 2, high = elements.Length - 1;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                if (Fits(Shorten(middle))) low = middle;
                else high = middle - 1;
            }
            block.Text = Shorten(low);
        }
        block.SizeChanged += (_, _) => Fit();
        return block;
    }

    private static Grid PathLine(string label, string path)
    {
        var line = new Grid { ColumnSpacing = 8 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.Children.Add(SecondaryText(label));
        TextBlock value = PathText(path);
        Grid.SetColumn(value, 1);
        line.Children.Add(value);
        return line;
    }
}
