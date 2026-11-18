using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;
namespace GlassDock.TopBar;

// Keeps this program's windows on every Windows virtual desktop.
// 1) Pins each window the way "Show this window on all desktops" does. That shell
//    API has no official documentation and can be missing on some Windows builds.
// 2) Fallback: when a desktop switch hides one of our windows, move it to the
//    desktop now shown, with the documented IVirtualDesktopManager.
internal sealed class AllDesktops : IDisposable
{
    const uint ObjectCloaked=0x8017,ShellCloaked=2;
    static readonly Guid ImmersiveShell=new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    static readonly Guid PinnedAppsService=new("B5A399E7-1C87-46B8-88E9-FC5747B171BD");
    static readonly Guid DesktopManager=new("AA509086-5CA9-4C25-8F95-589D3C07B48A");
    readonly Dispatcher dispatcher;
    readonly Action<string> log;
    readonly DispatcherTimer pinTimer,followTimer;
    readonly WinEventProc callback;
    readonly nint hook;
    readonly uint ownProcess=(uint)Environment.ProcessId;
    readonly HashSet<nint> pinnedWindows=new();
    IVirtualDesktopPinnedApps? pins;
    IApplicationViewCollection? views;
    IVirtualDesktopManager? manager;
    bool pinErrorLogged,followLogged,disposed;
    int followRetries;

    internal AllDesktops(Dispatcher dispatcher,Action<string> log)
    {
        this.dispatcher=dispatcher;this.log=log;
        try {manager=(IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(DesktopManager)!)!;}
        catch(Exception e){log("Virtual desktop manager unavailable: "+e.Message);}
        callback=OnCloaked;
        hook=SetWinEventHook(ObjectCloaked,ObjectCloaked,0,callback,ownProcess,0,0);
        followTimer=new DispatcherTimer(DispatcherPriority.Normal,dispatcher) {Interval=TimeSpan.FromMilliseconds(60)};
        followTimer.Tick+=(_,_)=>{followTimer.Stop();Follow();};
        // New windows (and pins lost when Explorer restarts) are picked up here.
        pinTimer=new DispatcherTimer(DispatcherPriority.Background,dispatcher) {Interval=TimeSpan.FromSeconds(2)};
        pinTimer.Tick+=(_,_)=>PinAll();
        // GLASSDOCK_TOPBAR_NO_DESKTOP_PIN=1 tests the fallback alone.
        if(Environment.GetEnvironmentVariable("GLASSDOCK_TOPBAR_NO_DESKTOP_PIN")=="1"){log("All-desktop pin disabled for testing");return;}
        pinTimer.Start();
        dispatcher.BeginInvoke(PinAll,DispatcherPriority.ApplicationIdle);
    }

    void PinAll()
    {
        if(disposed)return;
        try
        {
            if(pins==null||views==null)
            {
                var shell=(IServiceProvider10)Activator.CreateInstance(Type.GetTypeFromCLSID(ImmersiveShell)!)!;
                Guid service=PinnedAppsService,pinIid=typeof(IVirtualDesktopPinnedApps).GUID,viewIid=typeof(IApplicationViewCollection).GUID;
                Marshal.ThrowExceptionForHR(shell.QueryService(ref service,ref pinIid,out object pinObject));
                Marshal.ThrowExceptionForHR(shell.QueryService(ref viewIid,ref viewIid,out object viewObject));
                pins=(IVirtualDesktopPinnedApps)pinObject;views=(IApplicationViewCollection)viewObject;
            }
            foreach(var hwnd in OwnWindows())
            {
                if(views.GetViewForHwnd(hwnd,out nint view)!=0||view==0)continue;
                try
                {
                    if(pins.IsViewPinned(view,out bool already)==0&&already){pinnedWindows.Add(hwnd);continue;}
                    int result=pins.PinView(view);
                    if(result==0){if(pinnedWindows.Add(hwnd))log($"Window 0x{hwnd:X} shown on all desktops");}
                    else if(!pinErrorLogged){pinErrorLogged=true;log($"Pin to all desktops failed: 0x{result:X8}; following desktop switches instead");}
                }
                finally {Marshal.Release(view);}
            }
        }
        catch(Exception e)
        {
            // Explorer restarted or this Windows build lacks the API: retry later,
            // and rely on Follow() meanwhile.
            pins=null;views=null;
            if(!pinErrorLogged){pinErrorLogged=true;log("Pin to all desktops unavailable; following desktop switches instead: "+e.Message);}
        }
    }

    void OnCloaked(nint hook,uint evt,nint hwnd,int obj,int child,uint thread,uint time)
    {
        if(obj!=0||disposed)return;
        followRetries=0;followTimer.Stop();followTimer.Start();
    }

    void Follow()
    {
        if(disposed||manager==null)return;
        var desktop=CurrentDesktop();
        bool stillHidden=false;
        foreach(var hwnd in OwnWindows())
        {
            if(DwmGetWindowAttribute(hwnd,14,out int cloaked,4)!=0||(cloaked&ShellCloaked)==0)continue;
            if(desktop is not Guid id){stillHidden=true;continue;}
            int result=manager.MoveWindowToDesktop(hwnd,ref id);
            if(!followLogged){followLogged=true;log($"Moved window 0x{hwnd:X} to the current desktop: 0x{result:X8}");}
            stillHidden=true;
        }
        // The switch may still be settling; check again a few times.
        if(stillHidden&&++followRetries<=5){followTimer.Interval=TimeSpan.FromMilliseconds(150);followTimer.Start();}
        else followTimer.Interval=TimeSpan.FromMilliseconds(60);
    }

    static Guid? CurrentDesktop()
    {
        foreach(var key in new[]{@"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops",
                                 $@"Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\{Process.GetCurrentProcess().SessionId}\VirtualDesktops"})
        {
            using var k=Registry.CurrentUser.OpenSubKey(key);
            if(k?.GetValue("CurrentVirtualDesktop") is byte[] {Length:16} bytes)return new Guid(bytes);
        }
        return null;
    }

    List<nint> OwnWindows()
    {
        var list=new List<nint>();
        EnumWindows((hwnd,_)=>{
            GetWindowThreadProcessId(hwnd,out uint pid);
            if(pid==ownProcess&&IsWindowVisible(hwnd))list.Add(hwnd);
            return true;
        },0);
        return list;
    }

    public void Dispose()
    {
        if(disposed)return;disposed=true;
        pinTimer.Stop();followTimer.Stop();
        if(hook!=0)UnhookWinEvent(hook);
    }

    delegate bool EnumProc(nint hwnd,nint data);
    delegate void WinEventProc(nint hook,uint evt,nint hwnd,int obj,int child,uint thread,uint time);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback,nint data);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd,out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] static extern nint SetWinEventHook(uint min,uint max,nint module,WinEventProc callback,uint pid,uint thread,uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(nint hook);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint hwnd,int attribute,out int value,int size);

    [ComImport,Guid("6D5140C1-7436-11CE-8034-00AA006009FA"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IServiceProvider10
    { [PreserveSig] int QueryService(ref Guid service,ref Guid riid,[MarshalAs(UnmanagedType.IUnknown)] out object result); }
    [ComImport,Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationViewCollection
    {
        [PreserveSig] int GetViews(out nint array);
        [PreserveSig] int GetViewsByZOrder(out nint array);
        [PreserveSig] int GetViewsByAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string id,out nint array);
        [PreserveSig] int GetViewForHwnd(nint hwnd,out nint view);
    }
    [ComImport,Guid("4CE81583-1E4C-4632-A621-07A53543148F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktopPinnedApps
    {
        [PreserveSig] int IsAppIdPinned([MarshalAs(UnmanagedType.LPWStr)] string appId,[MarshalAs(UnmanagedType.Bool)] out bool pinned);
        [PreserveSig] int PinAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        [PreserveSig] int UnpinAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        [PreserveSig] int IsViewPinned(nint view,[MarshalAs(UnmanagedType.Bool)] out bool pinned);
        [PreserveSig] int PinView(nint view);
        [PreserveSig] int UnpinView(nint view);
    }
    [ComImport,Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(nint hwnd,[MarshalAs(UnmanagedType.Bool)] out bool current);
        [PreserveSig] int GetWindowDesktopId(nint hwnd,out Guid id);
        [PreserveSig] int MoveWindowToDesktop(nint hwnd,ref Guid id);
    }
}
