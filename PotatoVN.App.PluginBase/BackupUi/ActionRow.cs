using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace PotatoVN.App.PluginBase.BackupUi;

// Native controls retain their focus/keyboard behavior, with actions wrapping at narrow widths.
internal sealed class ActionRow : Panel
{
    private const double Gap = 8;
    protected override Size MeasureOverride(Size availableSize)
    {
        double x = 0, y = 0, lineHeight = 0, width = 0;
        foreach (UIElement child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            Size size = child.DesiredSize;
            if (x > 0 && x + size.Width > availableSize.Width)
            {
                width = Math.Max(width, x - Gap);
                y += lineHeight + Gap;
                x = 0;
                lineHeight = 0;
            }
            x += size.Width + Gap;
            lineHeight = Math.Max(lineHeight, size.Height);
        }
        return new Size(Math.Max(width, Math.Max(0, x - Gap)), y + lineHeight);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, lineHeight = 0;
        foreach (UIElement child in Children)
        {
            Size size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width)
            {
                y += lineHeight + Gap;
                x = 0;
                lineHeight = 0;
            }
            child.Arrange(new Rect(x, y, Math.Min(size.Width, finalSize.Width), size.Height));
            x += size.Width + Gap;
            lineHeight = Math.Max(lineHeight, size.Height);
        }
        return finalSize;
    }
}
