using System;
using System.Diagnostics;
using System.Reflection;
using System.IO;
using System.Xml.Linq;
using System.Windows.Markup;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using GlassDock.TopBar;
using Forms=System.Windows.Forms;

// Live integration test: only the helper window is maximized/minimized.
class TestApp : Application
{
    public bool Helper;
    public bool Interactive;
    protected override void OnStartup(StartupEventArgs e)
    {
        ShutdownMode=ShutdownMode.OnExplicitShutdown;
        if(Interactive){new TopBarWindow {ShowInTaskbar=true}.Show();return;}
        if(Helper)
        {
            var helper=new Window {Title="GlassDock auto-hide test window",Width=420,Height=240,Left=300,Top=180};
            helper.Show();Console.WriteLine(new WindowInteropHelper(helper).Handle);Console.Out.Flush();return;
        }
        RunTests();
    }
    static object Field(object target,string name)=>target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(target)!;
    static bool Property(object target,string name)=>(bool)target.GetType().GetProperty(name)!.GetValue(target)!;
    static void Check(bool condition,string text){if(!condition)throw new Exception(text);Console.WriteLine("PASS "+text);}
    static void CheckPosition(TopBarWindow bar,string stage)
    {
        var expected=Forms.Screen.PrimaryScreen!.Bounds;
        GetWindowRect(new WindowInteropHelper(bar).Handle,out var actual);
        Check(actual.Left==expected.Left&&actual.Top==expected.Top&&actual.Right==expected.Right,
            $"{stage}: bar stays at monitor top; actual=({actual.Left},{actual.Top},{actual.Right},{actual.Bottom}) expected top={expected.Top}");
    }
    async void RunTests()
    {
        Process? helper=null;TopBarWindow? bar=null;
        try
        {
            var start=new ProcessStartInfo("dotnet") {UseShellExecute=false,RedirectStandardOutput=true,CreateNoWindow=true};
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);start.ArgumentList.Add("--window");
            helper=Process.Start(start)!;
            nint hwnd=(nint)long.Parse((await helper.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)))!);
            var monitor=Forms.Screen.PrimaryScreen!;
            int baseline=monitor.WorkingArea.Top;
            bar=new TopBarWindow();bar.Show();await Task.Delay(900);
            object host=Field(bar,"appBar"),tray=Field(bar,"trayOverflow"),notifications=Field(bar,"notificationPosition");
            bool positionOnly=Environment.GetEnvironmentVariable("GLASSDOCK_POSITION_ONLY")=="1";
            Check(bar.IsVisible&&Property(host,"Registered"),"restored window: bar visible and work area reserved");
            int reservedTop=Forms.Screen.PrimaryScreen!.WorkingArea.Top;
            CheckPosition(bar,"initial");
            var initial=Forms.Screen.PrimaryScreen!.Bounds;
            SetWindowPos(new WindowInteropHelper(bar).Handle,0,initial.Left,initial.Top+28,initial.Width,28,0x14);
            await Task.Delay(250);CheckPosition(bar,"external position drift recovery");
            ShowWindowAsync(hwnd,3);await Task.Delay(1200);
            Check(!bar.IsVisible&&!Property(host,"Registered"),$"maximized: bar hidden and reservation removed (visible={bar.IsVisible}, registered={Property(host,"Registered")}, zoomed={IsZoomed(hwnd)}, fg={GetForegroundWindow()}, helper={hwnd})");
            Check(Forms.Screen.PrimaryScreen!.WorkingArea.Top==baseline,"maximized window can use the former bar area");
            Check(ReferenceEquals(notifications,Field(bar,"notificationPosition")),"notification watcher stays alive while hidden");
            for(int cycle=0;cycle<6;cycle++)
            {
                ShowWindowAsync(hwnd,9);await Task.Delay(650);
                CheckPosition(bar,$"restore cycle {cycle+1}");
                Check(Forms.Screen.PrimaryScreen!.WorkingArea.Top==reservedTop,"restored reservation remains stable");
                ShowWindowAsync(hwnd,3);await Task.Delay(650);
            }
            if(!positionOnly)
            {
            // The tray menu's explicit Show action uses the same reveal timeout as edge hover.
            typeof(TopBarWindow).GetField("keepVisibleUntil",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(bar,Environment.TickCount64+2500);
            bar.Show();await Task.Delay(250);
            await (Task)tray.GetType().GetMethod("ToggleAsync")!.Invoke(tray,null)!;
            Check(Property(tray,"IsOpen")&&bar.IsVisible,"tray opens from the revealed bar during maximization");
            // Expire the reveal timeout directly: Explorer can dismiss a
            // programmatically opened flyout when no real user click granted focus.
            typeof(TopBarWindow).GetField("keepVisibleUntil",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(bar,Environment.TickCount64-1);
            await Task.Delay(250);
            Check(!Property(tray,"IsOpen")||bar.IsVisible,"an open tray never loses its revealed bar after the timeout");
            tray.GetType().GetMethod("Close")!.Invoke(tray,new object[]{false});await Task.Delay(1300);
            Check(!bar.IsVisible,"bar hides again after the tray closes");
            }
            if(Environment.GetEnvironmentVariable("GLASSDOCK_NOTIFICATION_TEST")=="1")
            {
                ((Forms.NotifyIcon)Field(bar,"tray")).ShowBalloonTip(3500,"GlassDock test","Auto-hide notification placement test",Forms.ToolTipIcon.Info);
                await Task.Delay(1800);
                int count=(int)notifications.GetType().GetProperty("RelocatedCount")!.GetValue(notifications)!;
                Check(count>0&&!bar.IsVisible,$"native notification remains active while the bar is hidden (relocated={count}, barVisible={bar.IsVisible})");
                var toast=FindWindow("Windows.UI.Core.CoreWindow","새 알림");
                if(toast==0)toast=FindWindow("Windows.UI.Core.CoreWindow","New notification");
                GetWindowRect(toast,out var toastBounds);GetWindowRect(new WindowInteropHelper(bar).Handle,out var barBounds);
                Check(toastBounds.Top>=barBounds.Bottom&&toastBounds.Top<=barBounds.Bottom+20&&toastBounds.Right<=barBounds.Right,"hidden bar anchors the real Windows banner at its top-right tray area");
            }
            ShowWindowAsync(hwnd,9);await Task.Delay(1200);
            Check(bar.IsVisible&&Property(host,"Registered"),"restore: same bar and tray return with reservation");
            CheckPosition(bar,"restore");
            Check(Forms.Screen.PrimaryScreen!.WorkingArea.Top==reservedTop&&ReferenceEquals(host,Field(bar,"appBar"))&&ReferenceEquals(notifications,Field(bar,"notificationPosition")),"restore retains one appbar and notification watcher without duplicating work area");
            Check(ReferenceEquals(tray,Field(bar,"trayOverflow")),"restoration retains the tray controller");
            var full=Forms.Screen.PrimaryScreen!.Bounds;
            SetWindowPos(hwnd,0,full.Left,full.Top,full.Width,full.Height,0x14);await Task.Delay(1000);
            Check(!bar.IsVisible&&!Property(host,"Registered"),"fullscreen bounds hide the bar without a maximized flag");
            SetWindowPos(hwnd,0,300,180,420,240,0x14);await Task.Delay(1000);
            Check(bar.IsVisible&&Property(host,"Registered"),"leaving fullscreen restores the bar");
            CheckPosition(bar,"fullscreen exit");
            ShowWindowAsync(hwnd,3);await Task.Delay(800);ShowWindowAsync(hwnd,6);await Task.Delay(1200);
            Check(bar.IsVisible&&Property(host,"Registered"),"minimize: bar returns automatically");
            CheckPosition(bar,"minimize");
            ShowWindowAsync(hwnd,9);await Task.Delay(500);
            if(!positionOnly)
            {
            await (Task)tray.GetType().GetMethod("ToggleAsync")!.Invoke(tray,null)!;await Task.Delay(300);
            Check(Property(tray,"IsOpen"),"native tray reopens after maximize/restore/minimize cycles");
            tray.GetType().GetMethod("Close")!.Invoke(tray,new object[]{false});
            }
            else Console.WriteLine("SKIP tray presentation checks: position-only regression run");
            Console.WriteLine("PASS auto-hide integration checks");
        }
        catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;}
        finally{bar?.Close();if(helper!=null){helper.Kill();helper.Dispose();}Shutdown(Environment.ExitCode);}
    }
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(nint hwnd,int command);
    [DllImport("user32.dll")] static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern nint FindWindow(string cls,string? title);
    [StructLayout(LayoutKind.Sequential)] struct Rect {public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint hwnd,out Rect rect);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd,nint after,int x,int y,int width,int height,uint flags);
    [STAThread] static void Main(string[] args)
    {
        var app=new TestApp {Helper=args.Length>0&&args[0]=="--window",Interactive=args.Length>0&&args[0]=="--interactive"};
        var path=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../src/TopBar/App.xaml"));
        var xml=XDocument.Load(path);
        XNamespace ns="http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources=new XElement(ns+"ResourceDictionary",
            new XAttribute(XNamespace.Xmlns+"x","http://schemas.microsoft.com/winfx/2006/xaml"),
            new XAttribute(XNamespace.Xmlns+"local","clr-namespace:GlassDock.TopBar;assembly=GlassDock.TopBar"),
            xml.Root!.Element(ns+"Application.Resources")!.Elements());
        string markup=resources.ToString().Replace("clr-namespace:GlassDock.TopBar\"","clr-namespace:GlassDock.TopBar;assembly=GlassDock.TopBar\"");
        app.Resources=(ResourceDictionary)XamlReader.Parse(markup);app.Run();
    }
}
