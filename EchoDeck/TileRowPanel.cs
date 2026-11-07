using System.Windows;
using System.Windows.Controls;

namespace EchoDeck;

/// <summary>
/// One row of tiles: the available width is split into <see cref="Columns"/> equal cells. Unlike UniformGrid it never asks for
/// more width than it is offered, so a row can't end up wider than the list and get cropped on the right.
/// </summary>
public sealed class TileRowPanel : Panel
{
    public static readonly DependencyProperty ColumnsProperty =
        DependencyProperty.Register(nameof(Columns), typeof(int), typeof(TileRowPanel),
            new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int cols = Math.Max(1, Columns);
        double width = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0 ? 0 : availableSize.Width;
        double cell = width > 0 ? width / cols : 240;

        double height = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(cell, double.PositiveInfinity));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(width > 0 ? width : cell * cols, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int cols = Math.Max(1, Columns);
        double cell = finalSize.Width / cols;
        int i = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(new Rect(i * cell, 0, cell, finalSize.Height));
            i++;
        }
        return finalSize;
    }
}
