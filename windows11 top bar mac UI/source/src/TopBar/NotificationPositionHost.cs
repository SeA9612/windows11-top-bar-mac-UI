using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms=System.Windows.Forms;
namespace GlassDock.TopBar;

// Moves the native banner host, without reading messages, recreating cards,
// changing app notification preferences, or taking keyboard focus.
internal sealed class NotificationPositionHost : IDisposable
{
    readonly FrameworkElement bar;
    readonly BarNative.WinEventCallback callback;
    readonly DispatcherTimer timer=new(DispatcherPriority.Send) {Interval=TimeSpan.FromMilliseconds(40)};
    readonly Dictionary<nint,(uint Process,BarNative.Rect Bounds)> originals=new();
    nint visibilityHook,locationHook;
    bool positioning,disposed;
    public int RelocatedCount=>originals.Count;
    internal IReadOnlyCollection<nint> RelocatedWindows=>new List<nint>(originals.Keys);

    public NotificationPositionHost(FrameworkElement bar)
    {
        this.bar=bar;callback=OnWindowEvent;
        visibilityHook=BarNative.SetWinEventHook(0x8001,0x8003,0,callback,0,0,2);
        locationHook=BarNative.SetWinEventHook(0x800B,0x800B,0,callback,0,0,2);
        if(visibilityHook==0||locationHook==0)
        {
            int error=Marshal.GetLastWin32Error();Dispose();
            throw new Win32Exception(error,"Windows 알림 위치 감시를 시작하지 못했습니다.");
        }
        timer.Tick+=(_,_)=>
        {
            foreach(var hwnd in new List<nint>(originals.Keys))TryPlace(hwnd);
            if(originals.Count==0)timer.Stop();
        };
        BarNative.EnumWindows((hwnd,_)=>{TryPlace(hwnd);return true;},0);
    }

    static bool IsToast(nint hwnd)
    {
        var name=new StringBuilder(128);BarNative.GetClassName(hwnd,name,name.Capacity);
        if(name.ToString()!="Windows.UI.Core.CoreWindow")return false;
        // This is the shell host's fixed title, not the sender or message text.
        var title=new StringBuilder(128);BarNative.GetWindowText(hwnd,title,title.Capacity);
        if(title.ToString() is not ("새 알림" or "New notification"))return false;
        BarNative.GetWindowThreadProcessId(hwnd,out uint processId);
        try
        {
            using var process=Process.GetProcessById((int)processId);
            return NotificationPlacement.IsWindowsToast(process.ProcessName,name.ToString(),title.ToString());
        }
        catch(ArgumentException){return false;}
        catch(InvalidOperationException){return false;}
        catch(Win32Exception){return false;}
    }

    void OnWindowEvent(nint hook,uint eventId,nint hwnd,int objectId,int childId,uint thread,uint time)
    {
        if(disposed||positioning||hwnd==0||objectId!=0||childId!=0)return;
        if(eventId==0x8001){originals.Remove(hwnd);return;}
        if(eventId==0x8003){Restore(hwnd);return;}
        if(originals.ContainsKey(hwnd)||IsToast(hwnd))TryPlace(hwnd);
    }

    void TryPlace(nint hwnd)
    {
        if(disposed||positioning)return;
        try
        {
            if(!BarNative.IsWindow(hwnd)||!BarNative.IsWindowVisible(hwnd)){Restore(hwnd);return;}
            if(!originals.ContainsKey(hwnd)&&!IsToast(hwnd))return;
            if(BarNative.DwmGetWindowAttribute(hwnd,14,out int cloaked,4)==0&&cloaked!=0)return;
            if(!BarNative.GetWindowRect(hwnd,out var bounds))return;
            int width=bounds.Right-bounds.Left,height=bounds.Bottom-bounds.Top;
            if(width<=1||height<=1)return;
            BarNative.GetWindowThreadProcessId(hwnd,out uint processId);
            if(originals.TryGetValue(hwnd,out var saved)&&saved.Process!=processId){originals.Remove(hwnd);return;}
            // The HWND remains alive while WPF hides the bar. Use its physical
            // bounds so notifications keep their anchor without a visible visual tree.
            var window=Window.GetWindow(bar);
            if(window==null||!BarNative.GetWindowRect(new WindowInteropHelper(window).Handle,out var anchor))return;
            var screen=Forms.Screen.FromPoint(new System.Drawing.Point(anchor.Right-1,anchor.Top)).WorkingArea;
            var position=NotificationPlacement.BelowBar(
                anchor,
                new BarNative.Rect {Left=screen.Left,Top=screen.Top,Right=screen.Right,Bottom=screen.Bottom},
                width,height,(int)Math.Round(8*VisualTreeHelper.GetDpi(bar).DpiScaleY));
            if(!originals.ContainsKey(hwnd))originals.Add(hwnd,(processId,bounds));
            timer.Start();
            if(bounds.Left==position.X&&bounds.Top==position.Y)return;
            positioning=true;
            try
            {
                // Keep the shell's size, Z order, activation and dismissal rules.
                if(!BarNative.SetWindowPos(hwnd,0,position.X,position.Y,0,0,0x15))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),"Windows 알림 위치를 이동하지 못했습니다.");
            }
            finally{positioning=false;}
        }
        catch(Exception e){BarSettings.Log(e);Restore(hwnd);}
    }

    void Restore(nint hwnd)
    {
        if(!originals.Remove(hwnd,out var saved)||!BarNative.IsWindow(hwnd))return;
        BarNative.GetWindowThreadProcessId(hwnd,out uint processId);
        if(processId!=saved.Process)return;
        positioning=true;
        try{BarNative.SetWindowPos(hwnd,0,saved.Bounds.Left,saved.Bounds.Top,0,0,0x15);}
        finally{positioning=false;}
    }

    public void Dispose()
    {
        if(disposed)return;disposed=true;timer.Stop();
        if(visibilityHook!=0){BarNative.UnhookWinEvent(visibilityHook);visibilityHook=0;}
        if(locationHook!=0){BarNative.UnhookWinEvent(locationHook);locationHook=0;}
        foreach(var hwnd in new List<nint>(originals.Keys))Restore(hwnd);
    }
}
