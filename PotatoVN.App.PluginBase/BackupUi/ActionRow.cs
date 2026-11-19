using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace PotatoVN.App.PluginBase.BackupUi;

// 保留原生控件的焦点顺序，按可用宽度换行并在每行内居中。
internal sealed class ActionRow : Panel
{
    private const double Gap = 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in Children)
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        return LayoutLines(availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        LayoutLines(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size LayoutLines(double availableWidth, bool arrange)
    {
        double x = 0, y = 0, lineHeight = 0, width = 0;
        int lineStart = 0;
        for (int index = 0; index < Children.Count; index++)
        {
            UIElement child = Children[index];
            if (child.Visibility != Visibility.Visible) continue;

            Size size = child.DesiredSize;
            if (x > 0 && x + size.Width > availableWidth)
            {
                if (arrange) ArrangeLine(lineStart, index, y, lineHeight, availableWidth);
                width = Math.Max(width, x - Gap);
                y += lineHeight + Gap;
                x = 0;
                lineHeight = 0;
                lineStart = index;
            }
            x += size.Width + Gap;
            lineHeight = Math.Max(lineHeight, size.Height);
        }
        if (arrange) ArrangeLine(lineStart, Children.Count, y, lineHeight, availableWidth);
        return new Size(Math.Max(width, Math.Max(0, x - Gap)), y + lineHeight);
    }

    private void ArrangeLine(int start, int end, double y, double lineHeight, double availableWidth)
    {
        double x = 0;
        for (int index = start; index < end; index++)
        {
            UIElement child = Children[index];
            if (child.Visibility != Visibility.Visible) continue;

            Size size = child.DesiredSize;
            child.Arrange(new Rect(x, y + (lineHeight - size.Height) / 2, Math.Min(size.Width, availableWidth), size.Height));
            x += size.Width + Gap;
        }
    }
}
