using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;
using System.Threading.Tasks;
namespace GlassDock.TopBar;

// Explorer retains all native hit targets. A separate, windowless guard restores
// its original style if this app exits before the open session is cleaned up.
internal sealed class NativeTrayOpacity : IDisposable
{
    const string Prefix="GlassDock.TopBar.TrayGlass.";
    readonly nint hwnd;
    readonly int token=Random.Shared.Next(1,int.MaxValue);
    EventWaitHandle? cancellation;
    Process? guard;
    bool disposed;
    internal NativeTrayOpacity(nint hwnd)
    {
        this.hwnd=hwnd;RecoverOrphan(hwnd);
        int style=BarNative.GetWindowLong(hwnd,-20);
        bool attributes=GetLayeredWindowAttributes(hwnd,out uint color,out byte alpha,out uint flags);
        try
        {
            SetProp(hwnd,Prefix+"Style",style);SetProp(hwnd,Prefix+"Color",(nint)(long)color);
            SetProp(hwnd,Prefix+"Alpha",alpha);SetProp(hwnd,Prefix+"Flags",(nint)(long)flags);
            SetProp(hwnd,Prefix+"Attributes",attributes?1:0);SetProp(hwnd,Prefix+"Process",Environment.ProcessId);
            if(!SetProp(hwnd,Prefix+"Token",token))throw new Win32Exception(Marshal.GetLastWin32Error());
            StartGuard();
            SetWindowLong(hwnd,-20,style|0x80000);
            if(!SetLayeredWindowAttributes(hwnd,0,1,2))throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch{Dispose();throw;}
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        if(GetProp(hwnd,Prefix+"Token")==token)RestoreSaved(hwnd);
        cancellation?.Set();
        if(guard is { } child&&cancellation is { } signal)
            _=Task.Run(()=>{try{child.WaitForExit(5000);}finally{child.Dispose();signal.Dispose();}});
        else cancellation?.Dispose();
        guard=null;cancellation=null;
    }
    internal void RevealTaskbarContent()
    {
        if((BarNative.GetWindowLong(hwnd,-16)&0x10000000)==0)
        {
            if(!SetProp(hwnd,Prefix+"RestoreHidden",token))throw new Win32Exception(Marshal.GetLastWin32Error());
            BarNative.ShowWindow(hwnd,4);
        }
        BarNative.EnumChildWindows(hwnd,(child,_)=>{
            var name=new StringBuilder(128);BarNative.GetClassName(child,name,name.Capacity);
            if((BarNative.GetWindowLong(child,-16)&0x10000000)==0&&name.ToString() is "Windows.UI.Composition.DesktopWindowContentBridge" or "Windows.UI.Core.CoreWindow" or "Windows.UI.Input.InputSite.WindowClass")
            {
                if(!SetProp(child,Prefix+"RestoreHidden",token))throw new Win32Exception(Marshal.GetLastWin32Error());
                BarNative.ShowWindow(child,4);
            }
            return true;
        },0);
    }
    void StartGuard()
    {
        // Linked-source integration tests do not have the App startup branch.
        if(Assembly.GetEntryAssembly()?.GetName().Name!="GlassDock.TopBar")return;
        string path=Environment.ProcessPath??throw new InvalidOperationException("App path unavailable");
        string name=$"Local\\GlassDock.TopBar.TrayGlass.{Environment.ProcessId}.{token}";
        cancellation=new EventWaitHandle(false,EventResetMode.ManualReset,name);
        var start=new ProcessStartInfo(path) {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
        if(Path.GetFileNameWithoutExtension(path).Equals("dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        foreach(string argument in new[]{"--tray-glass-guard",Environment.ProcessId.ToString(CultureInfo.InvariantCulture),hwnd.ToInt64().ToString(CultureInfo.InvariantCulture),token.ToString(CultureInfo.InvariantCulture),name})start.ArgumentList.Add(argument);
        guard=Process.Start(start)??throw new InvalidOperationException("Tray recovery guard could not start");
    }
    internal static bool RunGuard(string[] args)
    {
        if(args.Length==0||args[0]!="--tray-glass-guard")return false;
        if(args.Length!=5||!int.TryParse(args[1],out int process)||!long.TryParse(args[2],out long window)||!int.TryParse(args[3],out int token))return true;
        var hwnd=(nint)window;
        try
        {
            using var parent=Process.GetProcessById(process);
            using var signal=EventWaitHandle.OpenExisting(args[4]);
            while(!signal.WaitOne(150))
                if(parent.HasExited){if(GetProp(hwnd,Prefix+"Token")==token)RestoreSaved(hwnd);break;}
        }
        catch(ArgumentException){if(GetProp(hwnd,Prefix+"Token")==token)RestoreSaved(hwnd);}
        catch(WaitHandleCannotBeOpenedException){RecoverOrphan(hwnd);}
        return true;
    }
    // True while a tray session has made this taskbar layered and visible.
    internal static bool IsBorrowed(nint hwnd)=>GetProp(hwnd,Prefix+"Token")!=0;
    internal static void RecoverOrphans()
    {
        foreach(string name in new[]{"TopLevelWindowForOverflowXamlIsland","NotifyIconOverflowWindow","Shell_TrayWnd"})
        {
            var hwnd=BarNative.FindWindow(name,null);if(hwnd!=0)RecoverOrphan(hwnd);
        }
    }
    static void RecoverOrphan(nint hwnd)
    {
        if(GetProp(hwnd,Prefix+"Token")==0)return;
        int pid=(int)GetProp(hwnd,Prefix+"Process");
        try{using var process=Process.GetProcessById(pid);if(!process.HasExited)return;}
        catch(ArgumentException){}
        RestoreSaved(hwnd);
    }
    static void RestoreSaved(nint hwnd)
    {
        if(!BarNative.IsWindow(hwnd)||GetProp(hwnd,Prefix+"Token")==0)return;
        var token=GetProp(hwnd,Prefix+"Token");
        BarNative.EnumChildWindows(hwnd,(child,_)=>{if(GetProp(child,Prefix+"RestoreHidden")==token){BarNative.ShowWindow(child,0);RemoveProp(child,Prefix+"RestoreHidden");}return true;},0);
        if(GetProp(hwnd,Prefix+"RestoreHidden")==token){BarNative.ShowWindow(hwnd,0);RemoveProp(hwnd,Prefix+"RestoreHidden");}
        // Window properties disappear when an HWND is destroyed, so a reused
        // handle cannot receive an earlier session's recovery data.
        if(GetProp(hwnd,Prefix+"Attributes")!=0)
            SetLayeredWindowAttributes(hwnd,(uint)(long)GetProp(hwnd,Prefix+"Color"),(byte)(int)GetProp(hwnd,Prefix+"Alpha"),(uint)(long)GetProp(hwnd,Prefix+"Flags"));
        SetWindowLong(hwnd,-20,(int)GetProp(hwnd,Prefix+"Style"));
        foreach(string key in new[]{"Token","Style","Color","Alpha","Flags","Attributes","Process"})RemoveProp(hwnd,Prefix+key);
        BarNative.SetWindowPos(hwnd,0,0,0,0,0,0x37);
    }
    [DllImport("user32.dll",EntryPoint="SetWindowLongW",SetLastError=true)] static extern int SetWindowLong(nint hwnd,int index,int value);
    [DllImport("user32.dll",SetLastError=true)] static extern bool SetLayeredWindowAttributes(nint hwnd,uint color,byte alpha,uint flags);
    [DllImport("user32.dll",SetLastError=true)] static extern bool GetLayeredWindowAttributes(nint hwnd,out uint color,out byte alpha,out uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool SetProp(nint hwnd,string name,nint value);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern nint GetProp(nint hwnd,string name);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern nint RemoveProp(nint hwnd,string name);
}
