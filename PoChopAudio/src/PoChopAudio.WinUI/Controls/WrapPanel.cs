using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace PoChopAudio.WinUI.Controls;

/// <summary>
/// Lays children out in uniform cells, wrapping to a new row when the next cell will not fit.
///
/// WinUI 3 ships no WrapPanel, and the alternative — a fixed four-column <c>Grid</c> — clipped
/// every knob label once the window was anything less than very wide ("Alpha Threshol",
/// "Keep si"). Rather than take a CommunityToolkit dependency for one panel, this is the whole
/// behaviour the page needs: uniform cells, wrap on overflow.
/// </summary>
public sealed partial class WrapPanel : Panel
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(WrapPanel), new PropertyMetadata(200d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(WrapPanel), new PropertyMetadata(60d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty HorizontalSpacingProperty = DependencyProperty.Register(
        nameof(HorizontalSpacing), typeof(double), typeof(WrapPanel), new PropertyMetadata(0d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty VerticalSpacingProperty = DependencyProperty.Register(
        nameof(VerticalSpacing), typeof(double), typeof(WrapPanel), new PropertyMetadata(0d, OnLayoutPropertyChanged));

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        (d as WrapPanel)?.InvalidateMeasure();

    protected override Size MeasureOverride(Size availableSize)
    {
        var cell = new Size(ItemWidth, ItemHeight);
        foreach (var child in Children)
        {
            child.Measure(cell);
        }

        var columns = ColumnsFor(availableSize.Width);
        var rows = (int)Math.Ceiling(Children.Count / (double)columns);

        return new Size(
            (columns * ItemWidth) + (Math.Max(0, columns - 1) * HorizontalSpacing),
            rows == 0 ? 0 : (rows * ItemHeight) + ((rows - 1) * VerticalSpacing));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = ColumnsFor(finalSize.Width);
        var column = 0;
        var row = 0;

        foreach (var child in Children)
        {
            child.Arrange(new Rect(
                column * (ItemWidth + HorizontalSpacing),
                row * (ItemHeight + VerticalSpacing),
                ItemWidth,
                ItemHeight));

            if (++column < columns) continue;
            column = 0;
            row++;
        }

        return finalSize;
    }

    /// <summary>At least one column, so a panel narrower than a single cell still lays out.</summary>
    private int ColumnsFor(double width)
    {
        if (double.IsInfinity(width) || double.IsNaN(width) || width <= 0)
        {
            return Math.Max(1, Children.Count);
        }

        var step = ItemWidth + HorizontalSpacing;
        return Math.Max(1, (int)((width + HorizontalSpacing) / step));
    }
}
