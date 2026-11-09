using System;
using System.Text;
namespace GlassDock.TopBar;
internal static class BarBackground
{
    internal static bool CoversScreen(BarNative.Rect window,BarNative.Rect screen)=>
        window.Left<=screen.Left+2&&window.Top<=screen.Top+2&&
        window.Right>=screen.Right-2&&window.Bottom>=screen.Bottom-2;

    internal static double OpacityFor(bool hasWindow,bool fillsScreen)=>hasWindow&&fillsScreen?1.0:0;

    internal static double GetOpacity(BarNative.Rect screen)
    {
        var monitor=BarNative.MonitorFromPoint(new BarNative.Point {X=screen.Left+10,Y=screen.Top+10},2);
        bool hasWindow=false,fillsScreen=false;
        BarNative.EnumWindows((hwnd,_)=>
        {
            if(!BarNative.IsWindowVisible(hwnd)||BarNative.IsIconic(hwnd))return true;
            BarNative.GetWindowThreadProcessId(hwnd,out uint pid);
            // Ignore our own popups, docks, taskbars and non-interactive overlay windows.
            if(pid==Environment.ProcessId||(BarNative.GetWindowLong(hwnd,-20)&0x08000080)!=0)return true;
            if(BarNative.DwmGetWindowAttribute(hwnd,14,out int cloaked,4)==0&&cloaked!=0)return true;
            var name=new StringBuilder(128);BarNative.GetClassName(hwnd,name,name.Capacity);
            if(name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")return true;
            if(!BarNative.GetWindowRect(hwnd,out var bounds)||bounds.Right<=bounds.Left||bounds.Bottom<=bounds.Top)return true;
            if(bounds.Right<=screen.Left||bounds.Left>=screen.Right||bounds.Bottom<=screen.Top||bounds.Top>=screen.Bottom)return true;
            hasWindow=true;
            fillsScreen=CoversScreen(bounds,screen)||(BarNative.IsZoomed(hwnd)&&BarNative.MonitorFromWindow(hwnd,2)==monitor);
            return !fillsScreen;
        },0);
        return OpacityFor(hasWindow,fillsScreen);
    }
}
