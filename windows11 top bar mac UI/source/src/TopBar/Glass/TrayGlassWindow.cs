using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
namespace GlassDock.TopBar;

// A click-through glass presentation of the native tray. Explorer keeps the
// actual input window, tooltip providers, icon callbacks and context menus.
internal sealed class TrayGlassWindow : Window,IDisposable
{
    readonly nint source;
    readonly Image icons=new() {Stretch=Stretch.Fill,IsHitTestVisible=false};
    readonly DispatcherTimer refresh=new() {Interval=TimeSpan.FromMilliseconds(150)};
    NativeTrayOpacity? nativeOpacity;
    readonly bool ownsOpacity;
    HostBackdrop? material;
    bool disposed;
    internal System.Windows.Media.Imaging.BitmapSource? Artwork=>icons.Source as System.Windows.Media.Imaging.BitmapSource;
    int frameCount=1;
    internal int FrameCount=>frameCount;
    // `concealed` is the caller's lease that already made the native flyout
    // transparent before it was first shown; the caller keeps ownership of it.
    // `backdrop` is a material prepared while Explorer opened the flyout; this
    // window takes ownership of it.
    internal TrayGlassWindow(nint source,System.Windows.Media.Imaging.BitmapSource artwork,NativeTrayOpacity? concealed=null,HostBackdrop? backdrop=null)
    {
        this.source=source;nativeOpacity=concealed;ownsOpacity=concealed==null;material=backdrop;
        icons.Source=artwork;
        Title="GlassDock · 트레이";WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;
        AllowsTransparency=true;Background=Brushes.Transparent;ShowInTaskbar=false;ShowActivated=false;Topmost=true;
        if(!BarNative.GetWindowRect(source,out var bounds))throw new InvalidOperationException("트레이 위치를 확인하지 못했습니다.");
        double scale=Math.Max(1,GetDpiForWindow(source))/96.0;
        Left=bounds.Left/scale;Top=bounds.Top/scale;Width=(bounds.Right-bounds.Left)/scale;Height=(bounds.Bottom-bounds.Top)/scale;
        var layers=GlassTheme.CreateLayers(out _);layers.Children.Add(icons);Content=layers;
        SourceInitialized+=(_,_)=>
        {
            var helper=new WindowInteropHelper(this);
            SetWindowLong(helper.Handle,-20,BarNative.GetWindowLong(helper.Handle,-20)|0x08000020);
            HwndSource.FromHwnd(helper.Handle)?.AddHook((nint hwnd,int message,nint w,nint l,ref bool handled)=>
            {
                if(message==0x84){handled=true;return -1;}
                if(message==0x21){handled=true;return 3;}
                return 0;
            });
        };
        SizeChanged+=(_,_)=>SyncMaterial();
        LocationChanged+=(_,_)=>SyncMaterial();
        Closed+=(_,_)=>Release();
        try
        {
            nativeOpacity??=new NativeTrayOpacity(source);
            // Keep icons and material invisible until the material has reached
            // DWM, then reveal both at once: no icons on a bare background first.
            Opacity=0;
            material??=new HostBackdrop();material.SetOpacity(0);
            Show();Sync(bounds);
            refresh.Tick+=(_,_)=>Refresh();refresh.Start();
            Reveal();
        }
        catch{Dispose();throw;}
    }
    internal void Sync(BarNative.Rect bounds)
    {
        if(disposed)return;
        var hwnd=new WindowInteropHelper(this).Handle;
        if(BarNative.GetWindowRect(hwnd,out var current)&&
           (current.Left!=bounds.Left||current.Top!=bounds.Top||current.Right!=bounds.Right||current.Bottom!=bounds.Bottom))
            BarNative.SetWindowPos(hwnd,(nint)(-1),bounds.Left,bounds.Top,bounds.Right-bounds.Left,bounds.Bottom-bounds.Top,0x10);
        // Keep the non-layered Composition backdrop below Explorer's input
        // window. HTTRANSPARENT alone cannot pass input across UI threads.
        material?.Sync(source,bounds,(float)VisualTreeHelper.GetDpi(this).DpiScaleX);
    }
    internal bool Revealed {get;private set;}
    async void Reveal()
    {
        try{if(material!=null)await material.CommittedAsync();}
        catch(Exception e){BarSettings.Log(e);}
        if(disposed)return;
        // WPF and Composition present on separate clocks; a short shared fade
        // hides the frame of skew so neither layer appears alone.
        BeginAnimation(OpacityProperty,new System.Windows.Media.Animation.DoubleAnimation(0,1,TimeSpan.FromMilliseconds(RevealMs)));
        material?.Fade(1,RevealMs);
        Revealed=true;
    }
    const int RevealMs=120;
    void SyncMaterial(){if(BarNative.GetWindowRect(source,out var bounds))material?.Sync(source,bounds,(float)VisualTreeHelper.GetDpi(this).DpiScaleX);}
    void Refresh()
    {
        if(disposed||!BarNative.IsWindowVisible(source))return;
        try
        {
            if(NativeTrayFrame.Capture(source) is { } artwork){icons.Source=artwork;frameCount++;}
            if(BarNative.GetWindowRect(source,out var bounds))Sync(bounds);
        }
        catch(Exception e){BarSettings.Log(e);refresh.Stop();}
    }
    void Release()
    {
        if(disposed)return;disposed=true;refresh.Stop();
        if(ownsOpacity)nativeOpacity?.Dispose();
        nativeOpacity=null;
        material?.Dispose();material=null;icons.Source=null;
    }
    public void Dispose(){Release();Close();}
    [DllImport("user32.dll",EntryPoint="SetWindowLongW")] static extern int SetWindowLong(nint hwnd,int index,int value);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint hwnd);
}
