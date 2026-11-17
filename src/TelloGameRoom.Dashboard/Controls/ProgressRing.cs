using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TelloGameRoom.Dashboard.Controls;

/// <summary>A circular arc that shows a fraction between 0 and 1, starting at 12 o'clock and running clockwise.</summary>
public sealed class RingArc : Shape
{
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(RingArc), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Fraction
    {
        get => (double)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    protected override Geometry DefiningGeometry
    {
        get
        {
            var fraction = double.IsNaN(Fraction) ? 0 : Math.Clamp(Fraction, 0, 1);
            var size = Math.Min(ActualWidth, ActualHeight);
            var radius = (size - StrokeThickness) / 2;
            if (radius <= 0 || fraction <= 0)
            {
                return Geometry.Empty;
            }

            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            if (fraction >= 0.9999)
            {
                return new EllipseGeometry(center, radius, radius);
            }

            var angle = fraction * 2 * Math.PI;
            var start = new Point(center.X, center.Y - radius);
            var end = new Point(center.X + (radius * Math.Sin(angle)), center.Y - (radius * Math.Cos(angle)));
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(start, isFilled: false, isClosed: false);
                context.ArcTo(end, new Size(radius, radius), 0, angle > Math.PI, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
            }

            geometry.Freeze();
            return geometry;
        }
    }

    protected override Size MeasureOverride(Size constraint) =>
        new(double.IsInfinity(constraint.Width) ? 0 : constraint.Width, double.IsInfinity(constraint.Height) ? 0 : constraint.Height);
}
