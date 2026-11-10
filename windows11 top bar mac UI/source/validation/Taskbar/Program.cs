using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using GlassDock.TopBar;
using Forms=System.Windows.Forms;

// Live integration test for hiding Explorer's taskbars. Run with the real top bar closed.
// Always puts the user's original auto-hide state back, even on failure.
class Program
{
    static int count;
    static void Check(bool condition,string text){if(!condition)throw new Exception("FAIL "+text);count++;Console.WriteLine("PASS "+text);}
    static bool AllHidden()=>TaskbarHider.TrayWindows().All(h=>!BarNative.IsWindowVisible(h));
    static bool AllVisible()=>TaskbarHider.TrayWindows().All(BarNative.IsWindowVisible);
    static string StateFile=>Path.Combine(BarSettings.Folder,"taskbar-state.json");
    static Forms.Screen Primary=>Forms.Screen.PrimaryScreen!;

    [STAThread] static void Main()
    {
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        uint user=TaskbarHider.GetState();
        Console.WriteLine($"Original taskbar state: {user} · taskbars: {TaskbarHider.TrayWindows().Count}");
        app.Startup+=async(_,_)=>
        {
            var owner=new Window {Width=1,Height=1,Left=-100,Top=-100,ShowInTaskbar=false,WindowStyle=WindowStyle.None,ShowActivated=false};
            owner.Show();
            nint hwnd=new WindowInteropHelper(owner).Handle;
            Window? helper=null;
            try
            {
                Check(!File.Exists(StateFile),"no leftover taskbar record (close the real top bar first)");
                Check(AllVisible(),"taskbar visible before the test");

                // 1. User's own state is kept.
                var hider=new TaskbarHider(hwnd,()=>false);await Task.Delay(900);
                Check(hider.Applied&&AllHidden(),"all taskbars hidden");
                Check((TaskbarHider.GetState()&1)!=0,"auto-hide on while hidden, so the work area is released");
                Check(Primary.WorkingArea.Bottom==Primary.Bounds.Bottom,"work area reaches the bottom of the screen");
                var saved=JsonSerializer.Deserialize<TaskbarHider.Saved>(File.ReadAllText(StateFile))!;
                Check(saved.Process==Environment.ProcessId&&saved.Original==user,"original state recorded on disk before hiding");
                foreach(var t in TaskbarHider.TrayWindows())BarNative.ShowWindow(t,8);
                await Task.Delay(700);
                Check(AllHidden(),"a taskbar shown again by Explorer is hidden again");
                hider.Dispose();await Task.Delay(600);
                Check(AllVisible(),"exit: taskbar visible again");
                Check(TaskbarHider.GetState()==user,"exit: original auto-hide setting restored");
                Check(!File.Exists(StateFile),"exit: record removed");

                // 2. Original auto-hide OFF: maximized windows must gain the taskbar area, then lose it again.
                uint off=user&~1u;
                TaskbarHider.SetState(off);await Task.Delay(1200);
                int reservedBottom=Primary.WorkingArea.Bottom;
                Check(reservedBottom<Primary.Bounds.Bottom,$"auto-hide off reserves the taskbar area (work bottom {reservedBottom})");
                hider=new TaskbarHider(hwnd,()=>false);await Task.Delay(1200);
                Check(AllHidden()&&Primary.WorkingArea.Bottom==Primary.Bounds.Bottom,"hidden taskbar gives its area to the screen");
                helper=new Window {Title="GlassDock taskbar test window",Width=400,Height=300,Left=200,Top=200};
                helper.Show();helper.WindowState=WindowState.Maximized;await Task.Delay(900);
                BarNative.GetWindowRect(new WindowInteropHelper(helper).Handle,out var max);
                Check(max.Bottom>=Primary.Bounds.Bottom,$"maximized window fills to the screen bottom (bottom {max.Bottom} / screen {Primary.Bounds.Bottom})");
                helper.Close();helper=null;
                hider.Dispose();await Task.Delay(1200);
                Check(TaskbarHider.GetState()==off&&AllVisible(),"exit: auto-hide stays off as originally set");
                Check(Primary.WorkingArea.Bottom==reservedBottom,"exit: taskbar area reserved again");

                // 3. Crash recovery from the record left by a dead process.
                using(var dead=Process.Start(new ProcessStartInfo("cmd.exe","/c exit") {CreateNoWindow=true,UseShellExecute=false})!)
                {
                    dead.WaitForExit();
                    Directory.CreateDirectory(BarSettings.Folder);
                    File.WriteAllText(StateFile,JsonSerializer.Serialize(new TaskbarHider.Saved {Process=dead.Id,Started=1,Original=off}));
                }
                TaskbarHider.SetState(off|1);foreach(var t in TaskbarHider.TrayWindows())BarNative.ShowWindow(t,0);await Task.Delay(800);
                TaskbarHider.RecoverOrphan();await Task.Delay(800);
                Check(AllVisible()&&TaskbarHider.GetState()==off&&!File.Exists(StateFile),"crash record: taskbar and setting restored on next start");
                Console.WriteLine($"PASS {count} taskbar checks");
            }
            catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;}
            finally
            {
                helper?.Close();
                TaskbarHider.SetState(user);
                foreach(var t in TaskbarHider.TrayWindows())if(!BarNative.IsWindowVisible(t))BarNative.ShowWindow(t,8);
                try{File.Delete(StateFile);}catch{}
                await Task.Delay(500);
                Console.WriteLine($"Final taskbar state: {TaskbarHider.GetState()} (original {user}) · visible: {AllVisible()}");
                owner.Close();app.Shutdown(Environment.ExitCode);
            }
        };
        app.Run();
    }
}
