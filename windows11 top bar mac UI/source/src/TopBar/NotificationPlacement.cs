using System;
namespace GlassDock.TopBar;
internal static class NotificationPlacement
{
    internal static bool IsWindowsToast(string process,string windowClass,string title)=>
        (process.Equals("ShellExperienceHost",StringComparison.OrdinalIgnoreCase)||process.Equals("ShellHost",StringComparison.OrdinalIgnoreCase))&&
        windowClass=="Windows.UI.Core.CoreWindow"&&title is "새 알림" or "New notification";

    internal static BarNative.Point BelowBar(BarNative.Rect bar,BarNative.Rect workArea,int width,int height,int gap)=>new() {
        X=Math.Clamp(bar.Right-width-gap,workArea.Left,Math.Max(workArea.Left,workArea.Right-width)),
        Y=Math.Clamp(bar.Bottom+gap,workArea.Top,Math.Max(workArea.Top,workArea.Bottom-height))
    };
}
