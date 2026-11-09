using System;
namespace GlassDock.TopBar;
internal static class TrayPopupPlacement
{
    // All coordinates are physical screen pixels, including on mixed-DPI monitors.
    internal static BarNative.Point Below(BarNative.Rect anchor,BarNative.Rect workArea,int width,int height,int gap)=>new() {
        X=Math.Clamp(anchor.Left+(anchor.Right-anchor.Left-width)/2,workArea.Left,Math.Max(workArea.Left,workArea.Right-width)),
        Y=Math.Clamp(anchor.Bottom+gap,workArea.Top,Math.Max(workArea.Top,workArea.Bottom-height))
    };
}
