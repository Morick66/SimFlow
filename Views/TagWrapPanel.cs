using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace SimFlow.Views;

/// <summary>按标签实际宽度排列；空间不足时换到下一行。</summary>
internal sealed class TagWrapPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var maxWidth = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;
        double rowWidth = 0;
        double rowHeight = 0;
        double totalHeight = 0;
        double widestRow = 0;

        foreach (var child in Children)
        {
            child.Measure(new Size(maxWidth, availableSize.Height));
            var desired = child.DesiredSize;
            if (rowWidth > 0 && rowWidth + desired.Width > maxWidth)
            {
                widestRow = Math.Max(widestRow, rowWidth);
                totalHeight += rowHeight;
                rowWidth = 0;
                rowHeight = 0;
            }

            rowWidth += desired.Width;
            rowHeight = Math.Max(rowHeight, desired.Height);
        }

        return new Size(Math.Min(maxWidth, Math.Max(widestRow, rowWidth)), totalHeight + rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        double y = 0;
        double rowHeight = 0;

        foreach (var child in Children)
        {
            var desired = child.DesiredSize;
            if (x > 0 && x + desired.Width > finalSize.Width)
            {
                x = 0;
                y += rowHeight;
                rowHeight = 0;
            }

            child.Arrange(new Rect(x, y, Math.Min(desired.Width, finalSize.Width), desired.Height));
            x += desired.Width;
            rowHeight = Math.Max(rowHeight, desired.Height);
        }

        return finalSize;
    }
}
