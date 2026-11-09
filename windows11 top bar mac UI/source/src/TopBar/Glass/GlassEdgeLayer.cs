using System;
using System.Windows;
using System.Windows.Media;

namespace GlassDock.TopBar;

// Resolution-independent edge-only decoration. Nothing is painted over the
// central icon area, and every inset follows the same rounded body geometry.
public sealed class GlassEdgeLayer : FrameworkElement
{
    public double CornerRadiusDip {get;set;}=GlassTheme.CornerRadiusDip;
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        double width = ActualWidth, height = ActualHeight;
        if (width < 8 || height < 8) return;
        double radius = Math.Min(CornerRadiusDip, Math.Min(width, height) / 2);
        Geometry Outer(double inset) => new RectangleGeometry(new Rect(inset, inset, Math.Max(0, width - inset * 2), Math.Max(0, height - inset * 2)), Math.Max(0, radius - inset), Math.Max(0, radius - inset));
        Geometry Ring(double start, double end) => new CombinedGeometry(GeometryCombineMode.Exclude, Outer(start), Outer(end));
        Color White(double strength) => GlassTheme.Alpha(GlassTheme.HighlightColor, strength);
        Color Dark(double strength) => GlassTheme.Alpha(GlassTheme.ShadeColor, strength);

        drawing.PushClip(Outer(0));
        drawing.DrawGeometry(new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
            GradientStops = new GradientStopCollection { new(Dark(.025), 0), new(Dark(GlassTheme.EdgeShadeStrength), .67), new(Dark(.055), 1) }
        }, null, Ring(.8, GlassTheme.EdgeShadeWidthDip));
        drawing.DrawGeometry(GlassTheme.Vertical(White(GlassTheme.InnerGlowStrength), White(0), White(GlassTheme.InnerGlowStrength * .48)), null, Ring(2.5, GlassTheme.InnerGlowWidthDip));

        var outline = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
            GradientStops = new GradientStopCollection
            {
                new(White(.78), 0), new(White(.26), .25), new(White(.08), .51), new(Dark(.15), .76), new(White(.49), 1)
            }
        };
        drawing.DrawGeometry(null, new Pen(outline, GlassTheme.OuterOutlineDip), Outer(.5));
        drawing.DrawGeometry(null, new Pen(GlassTheme.Vertical(Dark(.14), Dark(.045), White(.22)), GlassTheme.InnerOutlineDip), Outer(GlassTheme.InnerOutlineInsetDip));

        const double edge = 1.15;
        double r = radius - edge;
        var upperLeft = new PathFigure { StartPoint = new Point(edge, Math.Min(height - radius, radius + 46)) };
        upperLeft.Segments.Add(new LineSegment(new Point(edge, radius), true));
        upperLeft.Segments.Add(new ArcSegment(new Point(radius, edge), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
        upperLeft.Segments.Add(new LineSegment(new Point(Math.Min(width - radius, radius + 48), edge), true));
        var topBrush = new LinearGradientBrush(White(GlassTheme.TopReflectionStrength), White(.04), new Point(0, 0), new Point(.85, 1));
        drawing.DrawGeometry(null, new Pen(topBrush, 1.15) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, new PathGeometry(new[] { upperLeft }));

        drawing.DrawLine(new Pen(GlassTheme.Vertical(White(GlassTheme.LeftReflectionStrength), White(.08), White(0)), .8), new Point(edge, radius + 28), new Point(edge, Math.Max(radius + 28, height * .64)));
        var lowerLeft = new PathFigure { StartPoint = new Point(edge, Math.Max(radius, height - radius - 27)) };
        lowerLeft.Segments.Add(new LineSegment(new Point(edge, height - radius), true));
        lowerLeft.Segments.Add(new ArcSegment(new Point(radius, height - edge), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true));
        lowerLeft.Segments.Add(new LineSegment(new Point(Math.Min(width - radius, radius + 25), height - edge), true));
        drawing.DrawGeometry(null, new Pen(new SolidColorBrush(White(GlassTheme.BottomReflectionStrength)), .9) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, new PathGeometry(new[] { lowerLeft }));

        // Small reflected light spots, clipped to the narrow glass edge band.
        drawing.PushClip(Ring(.3, 5.4));
        double cornerPoint = radius - r * .70710678;
        var spot = new RadialGradientBrush(White(GlassTheme.SpecularSpotStrength), White(0));
        drawing.DrawEllipse(spot, null, new Point(cornerPoint, cornerPoint), 6, 10);
        drawing.DrawEllipse(new RadialGradientBrush(White(GlassTheme.SpecularSpotStrength * .4), White(0)), null, new Point(cornerPoint, height - cornerPoint), 6, 8);
        drawing.Pop();
        drawing.Pop();
    }
}
