using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GlassDock.TopBar;

// All application-controlled glass values live here. Host-backdrop blur is
// selected by Windows; ShadowBlurDip controls only our separate outer shadow.
internal static class GlassTheme
{
    internal const double CornerRadiusDip = 20;
    internal const double OuterOutlineDip = .85;
    internal const double InnerOutlineInsetDip = 1.8;
    internal const double InnerOutlineDip = .65;
    internal const double EdgeShadeWidthDip = 3.6;
    internal const double EdgeShadeStrength = .12;
    internal const double InnerGlowWidthDip = 5.2;
    internal const double InnerGlowStrength = .11;
    internal const double TopReflectionStrength = .92;
    internal const double LeftReflectionStrength = .56;
    internal const double BottomReflectionStrength = .34;
    internal const double SpecularSpotStrength = .62;
    internal const double ShadowPaddingDip = 20;
    internal const double ShadowBlurDip = 14;
    internal const double ShadowOpacity = .23;
    internal const double ShadowDepthDip = 5;
    // The native Composition Gaussian sigma, not an undocumented DWM/Acrylic knob.
    internal const float NativeBackdropSigma = 12;

    internal static readonly Color HighlightColor = Colors.White;
    internal static readonly Color ShadeColor = Color.FromRgb(14, 22, 37);
    internal static readonly Color ShadowColor = Color.FromRgb(10, 16, 28);
    internal static readonly CornerRadius CornerRadius = new(CornerRadiusDip);

    internal static Brush CreateAcrylicTint() => Vertical(
        Color.FromArgb(17, 248, 250, 255), Color.FromArgb(6, 232, 237, 250), Color.FromArgb(12, 246, 248, 255));
    internal static Brush CreateFallbackTint() => Vertical(
        Color.FromArgb(66, 248, 250, 255), Color.FromArgb(42, 232, 237, 250), Color.FromArgb(58, 246, 248, 255));

    internal static Grid CreateLayers(out Border body,double cornerRadiusDip=CornerRadiusDip)
    {
        var layers = new Grid { IsHitTestVisible = false, Focusable = false };
        body = new Border { CornerRadius = new CornerRadius(cornerRadiusDip), Background = CreateAcrylicTint(), IsHitTestVisible = false };
        layers.Children.Add(body);
        layers.Children.Add(new GlassEdgeLayer { IsHitTestVisible = false, Focusable = false,CornerRadiusDip=cornerRadiusDip });
        return layers;
    }

    internal static Color Alpha(Color color, double strength) => Color.FromArgb((byte)System.Math.Clamp(System.Math.Round(strength * 255), 0, 255), color.R, color.G, color.B);
    internal static LinearGradientBrush Vertical(Color top, Color center, Color bottom) => new()
    {
        StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
        GradientStops = new GradientStopCollection { new(top, 0), new(center, .53), new(bottom, 1) }
    };
}
