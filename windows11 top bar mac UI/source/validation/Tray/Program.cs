using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using GlassDock.TopBar;
using Forms=System.Windows.Forms;

// Explicit live Windows integration test. Opens the real tray twice without
// invoking an icon or changing any notification-area settings.
class Program
{
    static nint FindPopup()
    {
        var hwnd=BarNative.FindWindow("TopLevelWindowForOverflowXamlIsland",null);
        return hwnd!=0?hwnd:BarNative.FindWindow("NotifyIconOverflowWindow",null);
    }

    [STAThread] static void Main()
    {
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var screen=Forms.Screen.PrimaryScreen!.WorkingArea;
        var monitor=BarNative.MonitorFromPoint(new BarNative.Point {X=screen.Left+10,Y=screen.Top+10},2);
        BarNative.GetDpiForMonitor(monitor,0,out uint dpi,out _);
        double scale=(dpi==0?96:dpi)/96.0;
        var anchor=new Button {Content="∧",Background=Brushes.DarkSlateGray,Foreground=Brushes.White};
        var window=new Window {Title="GlassDock tray integration test",Content=anchor,Width=34,Height=28,
            Left=(screen.Right-250)/scale,Top=screen.Top/scale,WindowStyle=WindowStyle.None,
            ResizeMode=ResizeMode.NoResize,Topmost=true,ShowInTaskbar=false};
        var host=new TrayOverflowHost(anchor,_=>{});
        window.Loaded+=async(_,_)=>
        {
            Window? outside=null;
            try
            {
                var oldPopup=FindPopup();
                int originalStyle=oldPopup!=0?BarNative.GetWindowLong(oldPopup,-20):0;
                BarNative.GetWindowRect(oldPopup,out var original);
                await host.ToggleAsync();await Task.Delay(300);
                var popup=FindPopup();
                var bottom=anchor.PointToScreen(new Point(0,anchor.ActualHeight));
                if(!host.IsOpen||!BarNative.GetWindowRect(popup,out var bounds)||bounds.Top<bottom.Y||bounds.Top>bottom.Y+16*scale)
                    throw new Exception("Native flyout did not open below the top-bar anchor");
                var count=await Task.Run(()=>
                {
                    var icons=AutomationElement.FromHandle(popup).FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button));
                    foreach(AutomationElement icon in icons)
                    {
                        var rect=icon.Current.BoundingRectangle;
                        if(!rect.IsEmpty&&(rect.Top<bounds.Top||rect.Bottom>bounds.Bottom))
                            throw new Exception("Native icon hit targets did not move with the flyout");
                    }
                    return icons.Count;
                });
                if(count==0)throw new Exception("No native icons were rendered");
                Console.WriteLine($"PASS native flyout and {count} icon hit targets moved below the top-bar anchor");
                if((BarNative.GetWindowLong(popup,-20)&0x80000)==0)throw new Exception("Native input surface was not prepared for glass");
                var artwork=NativeTrayFrame.Capture(popup,false)??throw new Exception("Live native icon artwork was unavailable");
                var pixels=new byte[artwork.PixelWidth*artwork.PixelHeight*4];artwork.CopyPixels(pixels,artwork.PixelWidth*4,0);
                int clear=0,ink=0;for(int i=3;i<pixels.Length;i+=4){if(pixels[i]==0)clear++;if(pixels[i]>128)ink++;}
                if(clear<pixels.Length/8||ink<40)throw new Exception("Tray glass artwork lost transparency or visible icons");
                if(Environment.GetEnvironmentVariable("GLASSDOCK_TRAY_TEST_IMAGE") is string path)
                {
                    using var picture=new System.Drawing.Bitmap(bounds.Right-bounds.Left+40,bounds.Bottom-bounds.Top+40);
                    using(var graphics=System.Drawing.Graphics.FromImage(picture))graphics.CopyFromScreen(bounds.Left-20,bounds.Top-20,0,0,picture.Size,System.Drawing.CopyPixelOperation.SourceCopy);
                    picture.Save(path,System.Drawing.Imaging.ImageFormat.Png);
                }
                Console.WriteLine("PASS live tray artwork is sharp over a transparent glass surface");
                if(Environment.GetEnvironmentVariable("GLASSDOCK_TRAY_GUARD_TEST_APP") is string guardApp)
                {
                    var start=new System.Diagnostics.ProcessStartInfo(guardApp) {UseShellExecute=false,CreateNoWindow=true,WindowStyle=System.Diagnostics.ProcessWindowStyle.Hidden};
                    foreach(string argument in new[]{"--tray-glass-guard",int.MaxValue.ToString(),popup.ToInt64().ToString(),GetProp(popup,"GlassDock.TopBar.TrayGlass.Token").ToInt64().ToString(),"Local\\GlassDock.NoSuchTestParent"})start.ArgumentList.Add(argument);
                    using var guard=System.Diagnostics.Process.Start(start)!;
                    await guard.WaitForExitAsync();
                    if(guard.ExitCode!=0||(BarNative.GetWindowLong(popup,-20)&0x80000)!=(originalStyle&0x80000))throw new Exception("Recovery guard did not restore the original native tray style");
                    Console.WriteLine("PASS recovery guard restores native tray style after parent loss");
                }
                outside=new Window {Title="GlassDock outside-focus test",Width=180,Height=80,
                    Left=(screen.Left+screen.Width/2)/scale,Top=(screen.Top+screen.Height/2)/scale};
                outside.Show();outside.Activate();await Task.Delay(500);
                if(host.IsOpen)throw new Exception("Native flyout did not dismiss on outside focus");
                outside.Close();outside=null;
                if(popup==oldPopup&&original.Right>original.Left)
                {
                    BarNative.GetWindowRect(popup,out var restored);
                    if(restored.Left!=original.Left||restored.Top!=original.Top||restored.Right!=original.Right||restored.Bottom!=original.Bottom)
                        throw new Exception("Original native flyout bounds were not restored");
                    if(BarNative.GetWindowLong(popup,-20)!=originalStyle)throw new Exception("Original native tray style was not restored");
                }
                Console.WriteLine("PASS outside focus dismissal and original flyout position restoration");
                await host.ToggleAsync();await Task.Delay(300);
                if(!host.IsOpen)throw new Exception("Native flyout did not reopen");
                host.Close();await Task.Delay(100);
                if(host.IsOpen)throw new Exception("Native flyout did not close");
                Console.WriteLine("PASS native flyout reopens and closes");
                app.Shutdown(0);
            }
            catch(Exception e){Console.Error.WriteLine(e);app.Shutdown(1);}
            finally{host.Dispose();outside?.Close();window.Close();}
        };
        app.Run(window);
    }
    [System.Runtime.InteropServices.DllImport("user32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)] static extern nint GetProp(nint hwnd,string name);
}

namespace GlassDock.TopBar
{
    internal static class BarSettings {internal static void Log(Exception e)=>Console.Error.WriteLine(e);}
}
