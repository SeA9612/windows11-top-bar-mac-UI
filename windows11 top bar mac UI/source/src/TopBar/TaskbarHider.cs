using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Threading;
namespace GlassDock.TopBar;

// Hides every Explorer taskbar while the top bar runs; the dock launches and
// switches apps. Auto-hide is switched on only so the hidden taskbar gives its
// work area back to maximized windows. The user's original taskbar state is
// written to disk first, so a normal exit, logoff, the guard process (after a
// forced kill) or the next start can always put it back.
internal sealed class TaskbarHider : IDisposable
{
    const uint AutoHide=1;
    static string StateFile=>Path.Combine(BarSettings.Folder,"taskbar-state.json");
    static readonly object Gate=new();
    readonly HwndSource source;
    readonly Func<bool> busy;
    readonly uint taskbarCreated=BarNative.RegisterWindowMessage("TaskbarCreated");
    readonly DispatcherTimer timer=new() {Interval=TimeSpan.FromMilliseconds(500)};
    readonly BarNative.WinEventCallback callback;
    nint showHook;
    uint original;
    bool captured,applied,disposed,hidePending;
    EventWaitHandle? cancellation;

    public sealed class Saved {public int Process {get;set;} public long Started {get;set;} public uint Original {get;set;}}
    public bool Applied=>applied;
    public uint OriginalState=>original;

    public TaskbarHider(nint hwnd,Func<bool> busy)
    {
        this.busy=busy;callback=OnShow;
        source=HwndSource.FromHwnd(hwnd)??throw new InvalidOperationException("상단 바 창을 찾지 못했습니다.");
        RecoverOrphan();
        source.AddHook(Hook);
        AppDomain.CurrentDomain.ProcessExit+=OnProcessExit;
        // Out-of-context, so Explorer is never injected; covers new secondary taskbars too.
        showHook=BarNative.SetWinEventHook(0x8002,0x8002,0,callback,0,0,2);
        timer.Tick+=(_,_)=>Enforce();
        StartGuard();
        Apply();timer.Start();
    }

    void Apply()
    {
        if(disposed)return;
        lock(Gate)
        {
            if(!captured)
            {
                if(BarNative.FindWindow("Shell_TrayWnd",null)==0)return; // Explorer not ready: TaskbarCreated retries.
                // Another live hider (for example a test run) already replaced the state.
                original=Load() is { } other&&IsAlive(other)?other.Original:GetState();
                captured=true;
            }
            if(!applied){Save(original);SetLogonRestore(true);applied=true;}
            uint state=GetState();
            if((state&AutoHide)==0)SetState(state|AutoHide);
        }
        HideAll();
    }

    void Enforce()
    {
        if(disposed)return;
        // Not captured yet: Explorer was missing at start. Captured but not applied: logoff in progress.
        if(!applied){if(!captured)Apply();return;}
        lock(Gate)
        {
            if(!applied)return;
            uint state=GetState();
            if(BarNative.FindWindow("Shell_TrayWnd",null)!=0&&(state&AutoHide)==0)
            {
                // The user changed the taskbar setting while it was hidden: restore that choice later.
                original=state;Save(original);SetState(state|AutoHide);
            }
        }
        HideAll();
    }

    void HideAll()
    {
        if(disposed||!applied||busy())return;
        foreach(var hwnd in TrayWindows())
            if(BarNative.IsWindowVisible(hwnd)&&!NativeTrayOpacity.IsBorrowed(hwnd))BarNative.ShowWindow(hwnd,0);
    }

    void OnShow(nint hook,uint eventId,nint hwnd,int objectId,int childId,uint thread,uint time)
    {
        if(disposed||!applied||objectId!=0||childId!=0||hidePending||!IsTray(hwnd))return;
        hidePending=true;
        source.Dispatcher.BeginInvoke(DispatcherPriority.Send,new Action(()=>{hidePending=false;HideAll();}));
    }

    nint Hook(nint hwnd,int message,nint w,nint l,ref bool handled)
    {
        if(taskbarCreated!=0&&message==taskbarCreated)
        {
            // A restarted Explorer creates new taskbars; the timer catches late secondary ones.
            source.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(Apply));
        }
        else if(message==0x0011)Restore(); // WM_QUERYENDSESSION: logoff/shutdown, before Explorer saves its settings
        else if(message==0x0016&&w==0&&!disposed)Apply(); // WM_ENDSESSION, logoff cancelled
        return 0;
    }

    void Restore()
    {
        lock(Gate)
        {
            if(!applied)return;
            applied=false;
            ShowAll();SetState(original);
            try{File.Delete(StateFile);}catch(Exception e) when(e is IOException or UnauthorizedAccessException){BarSettings.Log(e);}
            SetLogonRestore(false);
        }
    }

    void OnProcessExit(object? sender,EventArgs e)=>Restore();

    public void Dispose()
    {
        if(disposed)return;disposed=true;
        timer.Stop();
        if(showHook!=0){BarNative.UnhookWinEvent(showHook);showHook=0;}
        source.RemoveHook(Hook);
        AppDomain.CurrentDomain.ProcessExit-=OnProcessExit;
        Restore();
        cancellation?.Set();
        cancellation?.Dispose();cancellation=null;
    }

    static bool IsMainApp=>Assembly.GetEntryAssembly()?.GetName().Name=="GlassDock.TopBar";

    void StartGuard()
    {
        // Linked-source integration tests do not have the App startup branch.
        if(!IsMainApp)return;
        string path=Environment.ProcessPath??throw new InvalidOperationException("App path unavailable");
        string name=$"Local\\GlassDock.TopBar.Taskbar.{Environment.ProcessId}.{Random.Shared.Next(1,int.MaxValue)}";
        cancellation=new EventWaitHandle(false,EventResetMode.ManualReset,name);
        // A short-lived launcher starts the real guard and exits, so the guard is
        // outside this process tree ("end process tree" cannot reach it) and, when
        // allowed, outside any job object that would kill it together with us.
        StartDetached(path,"--taskbar-guard-launch",Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),name);
    }

    internal static bool RunGuard(string[] args)
    {
        if(args.Length==0)return false;
        if(args[0]=="--restore-taskbar"){RestoreAtLogon();return true;}
        if(args[0]=="--taskbar-guard-launch")
        {
            if(args.Length==3&&Environment.ProcessPath is string path)
                try{StartDetached(path,"--taskbar-guard",args[1],args[2]);}catch(Exception e){BarSettings.Log(e);}
            return true;
        }
        if(args[0]!="--taskbar-guard")return false;
        if(args.Length!=3||!int.TryParse(args[1],out int pid))return true;
        try
        {
            using var parent=Process.GetProcessById(pid);
            using var signal=EventWaitHandle.OpenExisting(args[2]);
            while(!signal.WaitOne(150))
                if(parent.HasExited){RecoverOrphan(true);break;}
        }
        catch(ArgumentException){RecoverOrphan(true);}
        catch(WaitHandleCannotBeOpenedException){RecoverOrphan(true);}
        return true;
    }

    // Restores a session whose process is gone. Safe to call at any time.
    internal static void RecoverOrphan()=>RecoverOrphan(false);
    static void RecoverOrphan(bool settle)
    {
        lock(Gate)
        {
            var saved=Load();
            if(saved==null||IsAlive(saved))return;
            // Without Explorer the state cannot be applied; keep the record for the next start.
            if(BarNative.FindWindow("Shell_TrayWnd",null)==0)return;
            ShowAll();SetState(saved.Original);
            try{File.Delete(StateFile);}catch(Exception e) when(e is IOException or UnauthorizedAccessException){BarSettings.Log(e);}
            SetLogonRestore(false);
        }
        // The tray-glass guard of the same dead process may hide the taskbar
        // again while it undoes a borrowed tray session, or may itself have been
        // killed and left the taskbar transparent. Undo both, then show it.
        for(int i=0;settle&&i<20;i++)
        {
            Thread.Sleep(150);
            if(i==13)NativeTrayOpacity.RecoverOrphans();
            ShowAll();
        }
    }

    // Runs once at the next logon when the last session ended while the taskbar
    // was hidden (forced shutdown, power loss). Explorer may still be starting.
    static void RestoreAtLogon()
    {
        for(int i=0;i<120&&BarNative.FindWindow("Shell_TrayWnd",null)==0;i++)Thread.Sleep(500);
        NativeTrayOpacity.RecoverOrphans();
        RecoverOrphan();
    }

    const string RunOnceKey=@"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    const string RunOnceName="GlassDock.TopBar.RestoreTaskbar";
    static void SetLogonRestore(bool enabled)
    {
        if(!IsMainApp||Environment.ProcessPath is not string path)return;
        try
        {
            using var key=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunOnceKey);
            if(enabled)key.SetValue(RunOnceName,$"\"{path}\" --restore-taskbar");
            else key.DeleteValue(RunOnceName,false);        }
        catch(Exception e) when(e is UnauthorizedAccessException or IOException or System.Security.SecurityException){BarSettings.Log(e);}
    }

    static void StartDetached(string path,params string[] arguments)
    {
        var line=new StringBuilder("\"").Append(path).Append('"');
        foreach(var argument in arguments)line.Append(' ').Append(argument); // no spaces in our arguments
        var startup=new StartupInfo {Size=Marshal.SizeOf<StartupInfo>()};
        const uint NoWindow=0x08000000,Breakaway=0x01000000;
        // Breakaway fails with access denied when the job forbids it; then start normally.
        if(!CreateProcess(path,line.ToString(),0,0,false,NoWindow|Breakaway,0,null,ref startup,out var info)&&
           !CreateProcess(path,line.ToString(),0,0,false,NoWindow,0,null,ref startup,out info))
            throw new Win32Exception(Marshal.GetLastWin32Error(),"작업 표시줄 복구 감시를 시작하지 못했습니다.");
        CloseHandle(info.Process);CloseHandle(info.Thread);
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct StartupInfo
    {
        public int Size;public string? Reserved,Desktop,Title;
        public int X,Y,Width,Height,CountX,CountY,FillAttribute,Flags;public short ShowWindow,Reserved2;public nint Reserved3,StdInput,StdOutput,StdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInformation {public nint Process,Thread;public int ProcessId,ThreadId;}
    [DllImport("kernel32.dll",EntryPoint="CreateProcessW",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern bool CreateProcess(string application,string commandLine,nint processAttributes,nint threadAttributes,bool inherit,uint flags,nint environment,string? directory,ref StartupInfo startup,out ProcessInformation information);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);

    static void ShowAll()
    {
        foreach(var hwnd in TrayWindows())
            if(!BarNative.IsWindowVisible(hwnd)&&!NativeTrayOpacity.IsBorrowed(hwnd))BarNative.ShowWindow(hwnd,8); // SW_SHOWNA
    }

    internal static List<nint> TrayWindows()
    {
        var list=new List<nint>();
        BarNative.EnumWindows((hwnd,_)=>{if(IsTray(hwnd))list.Add(hwnd);return true;},0);
        return list;
    }

    static bool IsTray(nint hwnd)
    {
        var name=new StringBuilder(32);BarNative.GetClassName(hwnd,name,name.Capacity);
        return name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    static BarNative.AppBarData Data()=>new() {Size=(uint)Marshal.SizeOf<BarNative.AppBarData>(),Hwnd=BarNative.FindWindow("Shell_TrayWnd",null)};
    internal static uint GetState(){var d=Data();return (uint)BarNative.SHAppBarMessage(4,ref d);} // ABM_GETSTATE
    internal static void SetState(uint state){var d=Data();d.Param=(nint)state;BarNative.SHAppBarMessage(10,ref d);} // ABM_SETSTATE

    static long StartTicks(Process process)=>process.StartTime.ToUniversalTime().Ticks;
    static bool IsAlive(Saved saved)
    {
        try{using var process=Process.GetProcessById(saved.Process);return !process.HasExited&&StartTicks(process)==saved.Started;}
        catch(Exception e) when(e is ArgumentException or InvalidOperationException or Win32Exception){return false;}
    }
    static Saved? Load()
    {
        try{return JsonSerializer.Deserialize<Saved>(File.ReadAllText(StateFile));}
        catch(Exception e) when(e is IOException or JsonException or UnauthorizedAccessException){return null;}
    }
    static void Save(uint original)
    {
        using var self=Process.GetCurrentProcess();
        Directory.CreateDirectory(BarSettings.Folder);
        File.WriteAllText(StateFile+".tmp",JsonSerializer.Serialize(new Saved {Process=Environment.ProcessId,Started=StartTicks(self),Original=original}));
        File.Move(StateFile+".tmp",StateFile,true);
    }
}
