using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TelloGameRoom.Dashboard.Controls;

/// <summary>One bar: a label under it, its value, and the text shown in the tooltip.</summary>
public sealed record BarPoint(string Label, double Value, string ValueText);

/// <summary>
/// A small, dependency-free bar chart drawn directly with WPF. It follows the theme through its brush properties,
/// thins out axis labels when bars get narrow, and shows the exact value in a tooltip.
/// </summary>
public sealed class BarChart : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IEnumerable), typeof(BarChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(BarChart), new FrameworkPropertyMetadata(Brushes.MediumSlateBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(BarChart), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(BarChart), new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double LabelHeight = 22;
    private const double TopPadding = 8;
    private readonly List<(Rect Bounds, BarPoint Point)> _hitAreas = [];

    public BarChart()
    {
        ToolTip = new ToolTip();
        ToolTipService.SetInitialShowDelay(this, 100);
        MouseMove += (_, e) =>
        {
            var position = e.GetPosition(this);
            var hit = _hitAreas.FirstOrDefault(h => position.X >= h.Bounds.Left && position.X <= h.Bounds.Right);
            if (ToolTip is ToolTip tip)
            {
                tip.Content = hit.Point is null ? null : $"{hit.Point.Label}: {hit.Point.ValueText}";
                tip.Visibility = hit.Point is null ? Visibility.Collapsed : Visibility.Visible;
            }
        };
    }

    public IEnumerable? Items
    {
        get => (IEnumerable?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public Brush LabelBrush
    {
        get => (Brush)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        _hitAreas.Clear();

        // A transparent background makes the whole area hit-testable for tooltips.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        var points = Items?.OfType<BarPoint>().ToList() ?? [];
        var plotHeight = ActualHeight - LabelHeight - TopPadding;
        if (points.Count == 0 || plotHeight <= 10 || ActualWidth <= 10)
        {
            return;
        }

        var gridPen = new Pen(GridBrush, 1) { DashStyle = new DashStyle([2, 4], 0) };
        gridPen.Freeze();
        for (var i = 0; i <= 3; i++)
        {
            var y = Math.Round(TopPadding + (plotHeight * i / 3)) + 0.5;
            dc.DrawLine(gridPen, new Point(0, y), new Point(ActualWidth, y));
        }

        var max = Math.Max(points.Max(p => p.Value), double.Epsilon);
        var slot = ActualWidth / points.Count;
        var barWidth = Math.Clamp(slot * 0.62, 2, 44);
        var radius = Math.Min(5, barWidth / 2);
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Show every n-th label so they never overlap.
        var widest = points.Max(p => Measure(p.Label, typeface, dpi).Width) + 8;
        var step = Math.Max(1, (int)Math.Ceiling(widest / slot));

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var centre = (slot * i) + (slot / 2);
            var height = Math.Max(point.Value > 0 ? 2 : 0, plotHeight * Math.Max(0, point.Value) / max);
            var bar = new Rect(centre - (barWidth / 2), TopPadding + plotHeight - height, barWidth, height);
            if (height > 0)
            {
                dc.DrawRoundedRectangle(BarBrush, null, bar, radius, radius);
            }

            _hitAreas.Add((new Rect(slot * i, 0, slot, ActualHeight), point));

            if (i % step == 0)
            {
                var text = Measure(point.Label, typeface, dpi);
                dc.DrawText(text, new Point(centre - (text.Width / 2), ActualHeight - LabelHeight + 4));
            }
        }
    }

    private FormattedText Measure(string label, Typeface typeface, double dpi) =>
        new(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 11, LabelBrush, dpi);
}
