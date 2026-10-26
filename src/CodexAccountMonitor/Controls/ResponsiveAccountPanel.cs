using System.Windows;
using System.Windows.Controls;

namespace CodexAccountMonitor.Controls;

/// <summary>
/// Lays out account cards as one fluid column at widget widths and two columns
/// when enough horizontal space is available. Row height follows its tallest
/// card, so the host window can size itself to the visible content.
/// </summary>
public sealed class ResponsiveAccountPanel : Panel
{
    public static readonly DependencyProperty MinimumItemWidthProperty = DependencyProperty.Register(
        nameof(MinimumItemWidth),
        typeof(double),
        typeof(ResponsiveAccountPanel),
        new FrameworkPropertyMetadata(500d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing),
        typeof(double),
        typeof(ResponsiveAccountPanel),
        new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing),
        typeof(double),
        typeof(ResponsiveAccountPanel),
        new FrameworkPropertyMetadata(10d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinimumItemWidth
    {
        get => (double)GetValue(MinimumItemWidthProperty);
        set => SetValue(MinimumItemWidthProperty, value);
    }

    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count == 0)
        {
            return new Size(0, 0);
        }

        var width = double.IsFinite(availableSize.Width)
            ? Math.Max(0, availableSize.Width)
            : Math.Max(0, MinimumItemWidth);
        var columns = GetColumnCount(width);
        var itemWidth = GetItemWidth(width, columns);
        var desiredHeight = 0d;

        for (var index = 0; index < InternalChildren.Count; index += columns)
        {
            var rowHeight = 0d;
            for (var column = 0; column < columns && index + column < InternalChildren.Count; column++)
            {
                var child = InternalChildren[index + column];
                child.Measure(new Size(itemWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }

            if (index > 0)
            {
                desiredHeight += RowSpacing;
            }

            desiredHeight += rowHeight;
        }

        return new Size(width, desiredHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count == 0)
        {
            return finalSize;
        }

        var width = Math.Max(0, finalSize.Width);
        var columns = GetColumnCount(width);
        var itemWidth = GetItemWidth(width, columns);
        var y = 0d;

        for (var index = 0; index < InternalChildren.Count; index += columns)
        {
            var rowHeight = 0d;
            for (var column = 0; column < columns && index + column < InternalChildren.Count; column++)
            {
                rowHeight = Math.Max(rowHeight, InternalChildren[index + column].DesiredSize.Height);
            }

            for (var column = 0; column < columns && index + column < InternalChildren.Count; column++)
            {
                var x = column * (itemWidth + ColumnSpacing);
                InternalChildren[index + column].Arrange(new Rect(x, y, itemWidth, rowHeight));
            }

            y += rowHeight + RowSpacing;
        }

        return finalSize;
    }

    private int GetColumnCount(double width) =>
        width >= (Math.Max(1, MinimumItemWidth) * 2) + Math.Max(0, ColumnSpacing) ? 2 : 1;

    private double GetItemWidth(double width, int columns) =>
        Math.Max(0, (width - ((columns - 1) * Math.Max(0, ColumnSpacing))) / columns);
}
