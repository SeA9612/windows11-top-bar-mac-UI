using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using GlassDock.TopBar;
using Forms=System.Windows.Forms;

// Explicit live Windows integration test. Invokes only this test's own icon;
// does not invoke user app icons or change notification-area settings.
class Program
{
    static nint FindPopup()
    {
        var hwnd=BarNative.FindWindow("TopLevelWindowForOverflowXamlIsland",null);
        return hwnd!=0?hwnd:BarNative.FindWindow("NotifyIconOverflowWindow",null);
    }

    [STAThread] static void Main(string[] args)
    {
        bool interactive=Array.Exists(args,x=>x=="--interactive");
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        NativeTrayOpacity.RecoverOrphans();
        const string probeName="GlassDock tray callback test";
        using var probe=new Forms.NotifyIcon {Icon=System.Drawing.SystemIcons.Information,Text=probeName,Visible=true};
        int callbacks=0;
        Window? interactiveResult=null;
        probe.MouseClick+=(_,e)=>{callbacks++;if(interactiveResult!=null)interactiveResult.Title=$"GlassDock tray interactive test · {e.Button} {callbacks}";};
        probe.MouseDoubleClick+=(_,_)=>callbacks++;
        var screen=Forms.Screen.PrimaryScreen!.WorkingArea;
        var monitor=BarNative.MonitorFromPoint(new BarNative.Point {X=screen.Left+10,Y=screen.Top+10},2);
        BarNative.GetDpiForMonitor(monitor,0,out uint dpi,out _);
        double scale=(dpi==0?96:dpi)/96.0;
        var anchor=new Button {Content="∧",Background=Brushes.DarkSlateGray,Foreground=Brushes.White};
        var window=new Window {Title=interactive?"GlassDock tray interactive test":"GlassDock tray integration test",Content=anchor,Width=34,Height=28,
            Left=(screen.Right-250)/scale,Top=screen.Top/scale,WindowStyle=WindowStyle.None,
            ResizeMode=ResizeMode.NoResize,Topmost=true,ShowInTaskbar=interactive};
        if(interactive)
        {
            interactiveResult=window;
            var context=new Forms.ContextMenuStrip();context.Items.Add("확인용 메뉴",null,(_,_)=>window.Title="GlassDock tray interactive test · menu callback");probe.ContextMenuStrip=context;
        }
        var host=new TrayOverflowHost(anchor,_=>{});
        if(interactive){anchor.Click+=async(_,_)=>{try{await host.ToggleAsync();}catch(Exception e){window.Title=e.Message;}};window.Closed+=(_,_)=>{host.Dispose();app.Shutdown();};}
        var taskbar=BarNative.FindWindow("Shell_TrayWnd",null);
        int taskbarStyle=BarNative.GetWindowLong(taskbar,-20);
        var childVisibility=new System.Collections.Generic.Dictionary<nint,int>();
        BarNative.EnumChildWindows(taskbar,(h,_)=>{childVisibility[h]=BarNative.GetWindowLong(h,-16)&0x10000000;return true;},0);
        window.Loaded+=async(_,_)=>
        {
            if(interactive)return;
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
                var verifiedCells=new System.Collections.Generic.List<Int32Rect>();
                var count=await Task.Run(()=>
                {
                    var icons=AutomationElement.FromHandle(popup).FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button));
                    BarNative.GetWindowThreadProcessId(popup,out uint owner);
                    foreach(AutomationElement icon in icons)
                    {
                        var rect=icon.Current.BoundingRectangle;
                        if(!rect.IsEmpty&&(rect.Top<bounds.Top||rect.Bottom>bounds.Bottom))
                            throw new Exception("Native icon hit targets did not move with the flyout");
                        if(!rect.IsEmpty)verifiedCells.Add(new Int32Rect((int)Math.Round(rect.Left-bounds.Left),(int)Math.Round(rect.Top-bounds.Top),(int)Math.Round(rect.Width),(int)Math.Round(rect.Height)));
                        if(!rect.IsEmpty)
                        {
                            var target=BarNative.WindowFromPoint(new BarNative.Point {X=(int)(rect.Left+rect.Width/2),Y=(int)(rect.Top+rect.Height/2)});
                            BarNative.GetWindowThreadProcessId(target,out uint inputOwner);
                            if(inputOwner!=owner)throw new Exception("Glass backdrop blocks the native icon mouse target");
                        }
                    }
                    return icons.Count;
                });
                if(count==0)throw new Exception("No native icons were rendered");
                int visible=host.RenderedArtwork is { } current?NativeTrayFrame.CountVisibleIcons(current,verifiedCells):0;
                if(visible!=count)throw new Exception("Not every native icon has visible pixels in its hit target");
                Console.WriteLine($"PASS native flyout and {count} icon hit targets moved below the top-bar anchor");
                Console.WriteLine($"PASS visible artwork in all {visible} native icon cells");
                if((BarNative.GetWindowLong(popup,-20)&0x80000)==0)throw new Exception("Native input surface was not prepared for glass");
                var artwork=host.RenderedArtwork??throw new Exception("Live native icon artwork was unavailable");
                Console.WriteLine($"Live frames: {host.CaptureFrameCount}, artwork: {artwork.PixelWidth}x{artwork.PixelHeight}");
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
                probe.Icon=System.Drawing.SystemIcons.Warning;
                await Task.Delay(500);
                var changed=host.RenderedArtwork??throw new Exception("Tray disappeared during icon update");
                var updated=new byte[changed.PixelWidth*changed.PixelHeight*4];changed.CopyPixels(updated,changed.PixelWidth*4,0);
                int different=0;for(int i=0;i<Math.Min(updated.Length,pixels.Length);i++)if(updated[i]!=pixels[i])different++;
                if(different<20||host.CaptureFrameCount<2)throw new Exception("Actual notification icon updates did not refresh the glass artwork");
                Console.WriteLine("PASS actual notification icon change updates the live artwork");
                await Task.Run(()=>{
                    var buttons=AutomationElement.FromHandle(popup).FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button));
                    foreach(AutomationElement button in buttons)
                        if(button.Current.Name.Contains(probeName,StringComparison.Ordinal)&&button.TryGetCurrentPattern(InvokePattern.Pattern,out var pattern))
                        {((InvokePattern)pattern).Invoke();return;}
                    throw new Exception("Own test notification icon was not invokable");
                });
                await Task.Delay(250);
                if(callbacks==0)throw new Exception("Explorer did not deliver the notification icon callback");
                Console.WriteLine("PASS native notification icon invokes the application's real callback");
                if(!host.IsOpen){await host.ToggleAsync();await Task.Delay(200);}
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
                var opening=host.ToggleAsync();host.Close(false);await opening;await Task.Delay(200);
                if(host.IsOpen||BarNative.IsWindowVisible(FindPopup()))throw new Exception("Cancelling an opening session left the flyout visible");
                if(BarNative.GetWindowLong(taskbar,-20)!=taskbarStyle)throw new Exception("Taskbar style was not restored");
                foreach(var child in childVisibility)
                    if(BarNative.IsWindow(child.Key)&&(BarNative.GetWindowLong(child.Key,-16)&0x10000000)!=child.Value)
                        throw new Exception("Taskbar child visibility was not restored");
                Console.WriteLine("PASS cancelled opening restores taskbar styles and child visibility");
                await host.ToggleAsync();
                if(!host.IsOpen||host.RenderedIconCount==0)throw new Exception("Tray could not reopen after cancelled initialization");
                host.Close(false);
                Console.WriteLine("PASS reopen after cancelled initialization has visible icons");
                if(Environment.GetEnvironmentVariable("GLASSDOCK_TRAY_GUARD_TEST_APP") is string recoveryApp)
                {
                    using var lease=new NativeTrayOpacity(taskbar);lease.RevealTaskbarContent();
                    var start=new System.Diagnostics.ProcessStartInfo(recoveryApp) {UseShellExecute=false,CreateNoWindow=true,WindowStyle=System.Diagnostics.ProcessWindowStyle.Hidden};
                    foreach(string argument in new[]{"--tray-glass-guard",int.MaxValue.ToString(),taskbar.ToInt64().ToString(),GetProp(taskbar,"GlassDock.TopBar.TrayGlass.Token").ToInt64().ToString(),"Local\\GlassDock.NoSuchTestParent"})start.ArgumentList.Add(argument);
                    using var guard=System.Diagnostics.Process.Start(start)!;await guard.WaitForExitAsync();
                    if(guard.ExitCode!=0||BarNative.GetWindowLong(taskbar,-20)!=taskbarStyle)throw new Exception("Recovery guard failed to restore taskbar style");
                    foreach(var child in childVisibility)
                        if(BarNative.IsWindow(child.Key)&&(BarNative.GetWindowLong(child.Key,-16)&0x10000000)!=child.Value)throw new Exception("Recovery guard failed to restore hidden taskbar content");
                    Console.WriteLine("PASS recovery guard restores hidden taskbar content after parent loss");
                }
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
