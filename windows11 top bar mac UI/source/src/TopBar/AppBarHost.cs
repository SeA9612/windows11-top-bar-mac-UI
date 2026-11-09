using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms=System.Windows.Forms;
namespace GlassDock.TopBar;
internal sealed class AppBarHost : IDisposable
{
    const uint Callback=0x8065;
    readonly Window window;
    readonly Func<Forms.Screen> screen;
    readonly HwndSource source;
    readonly DispatcherTimer timer=new() {Interval=TimeSpan.FromMilliseconds(90)};
    readonly uint taskbarCreated;
    bool registered,placing,disposed,autoHidden;
    BarNative.Rect last;
    public bool Registered=>registered;
    public AppBarHost(Window window,Func<Forms.Screen> screen)
    {
        this.window=window;this.screen=screen;
        source=HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!;source.AddHook(Hook);
        taskbarCreated=BarNative.RegisterWindowMessage("TaskbarCreated");
        timer.Tick+=(_,_)=>{timer.Stop();Place();};
        try{Register();Place();}catch{Dispose();throw;}
    }
    BarNative.AppBarData Data()=>new(){Size=(uint)Marshal.SizeOf<BarNative.AppBarData>(),Hwnd=source.Handle,Callback=Callback,Edge=1};
    void Register(){var d=Data();registered=BarNative.SHAppBarMessage(0,ref d)!=0;if(!registered)throw new Win32Exception("상단 작업영역을 Windows에 등록하지 못했습니다.");}
    public void Schedule(){if(disposed)return;timer.Stop();timer.Start();}
    public void SetAutoHidden(bool value)
    {
        if(disposed||autoHidden==value)return;
        autoHidden=value;last=default;
        if(value&&registered){registered=false;var d=Data();BarNative.SHAppBarMessage(1,ref d);}
        else if(!value&&!registered)Register();
        Place();
    }
    public void Place()
    {
        if(disposed||placing)return;
        placing=true;
        try
        {
            var bounds=screen().Bounds;var monitor=BarNative.MonitorFromPoint(new BarNative.Point {X=bounds.Left+10,Y=bounds.Top+10},2);
            BarNative.GetDpiForMonitor(monitor,0,out uint dpi,out _);int height=StatusText.HeightForDpi(dpi);
            var d=Data();d.Bounds=new BarNative.Rect {Left=bounds.Left,Top=bounds.Top,Right=bounds.Right,Bottom=bounds.Top+height};
            if(registered)BarNative.SHAppBarMessage(2,ref d);
            d.Bounds.Bottom=d.Bounds.Top+height;
            if(d.Bounds.Right<=d.Bounds.Left)return;
            bool reservationChanged=!SameBounds(last,d.Bounds);
            if(registered&&reservationChanged)BarNative.SHAppBarMessage(3,ref d);
            last=d.Bounds;
            // WPF/Windows can clamp a re-shown window into the work area that
            // this appbar just reserved. The cached reservation is not proof
            // that the actual HWND still occupies it.
            if(!BarNative.GetWindowRect(source.Handle,out var actual)||!SameBounds(actual,last))
                BarNative.SetWindowPos(source.Handle,(nint)(-1),last.Left,last.Top,last.Right-last.Left,last.Bottom-last.Top,0x10);
        }
        finally{placing=false;}
    }
    static bool SameBounds(BarNative.Rect a,BarNative.Rect b)=>a.Left==b.Left&&a.Top==b.Top&&a.Right==b.Right&&a.Bottom==b.Bottom;
    nint Hook(nint hwnd,int message,nint w,nint l,ref bool handled)
    {
        if(message==0x21){handled=true;return 3;} // mouse clicks must not replace the active application
        if(message==Callback)
        {
            if(w.ToInt32()==1)Schedule(); // ABN_POSCHANGED
        }
        if(taskbarCreated!=0&&message==taskbarCreated){registered=false;last=default;if(!autoHidden)Register();Schedule();}
        if(message==0x02E0||message==0x007E)Schedule(); // DPI / display changes
        if(message==0x0006&&registered){var d=Data();BarNative.SHAppBarMessage(6,ref d);}
        if(message==0x0047)
        {
            if(registered){var d=Data();BarNative.SHAppBarMessage(9,ref d);}
            if(!placing)Schedule();
        }
        return 0;
    }
    public void Dispose(){if(disposed)return;disposed=true;timer.Stop();source.RemoveHook(Hook);if(registered){var d=Data();BarNative.SHAppBarMessage(1,ref d);registered=false;}}
}
