using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace GlassDock.TopBar;

// The native desktop material sits below the sharp WPF foreground. Sampling
// happens before this HWND is drawn, so icons are never part of the blur input.
internal sealed class HostBackdrop : IDisposable
{
    const string WindowClass = "GlassDock.TopBar.NativeGlass";
    static readonly WindowProcedure Procedure = ProcessMessage;
    static bool classRegistered;
    readonly nint hwnd;
    nint dispatcherController;
    readonly Compositor compositor;
    readonly DesktopWindowTarget target;
    readonly ContainerVisual root;
    readonly SpriteVisual material, shadowVisual;
    readonly CompositionRoundedRectangleGeometry shape, shadowShape;
    readonly CompositionBackdropBrush brush;
    readonly CompositionEffectFactory effectFactory;
    readonly CompositionEffectBrush effectBrush;
    readonly CompositionVisualSurface shadowSurface;
    readonly ShapeVisual shadowMaskVisual;
    readonly CompositionSurfaceBrush shadowMask;
    readonly DropShadow shadow;
    bool disposed;
    readonly double cornerRadiusDip;

    internal HostBackdrop(double cornerRadiusDip=GlassTheme.CornerRadiusDip)
    {
        this.cornerRadiusDip=cornerRadiusDip;
        if (Windows.System.DispatcherQueue.GetForCurrentThread() == null)
        {
            var options = new DispatcherQueueOptions { Size = Marshal.SizeOf<DispatcherQueueOptions>(), ThreadType = 2, ApartmentType = 2 };
            Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out dispatcherController));
        }
        if (!classRegistered)
        {
            var windowClass = new WindowClassEx { Size = (uint)Marshal.SizeOf<WindowClassEx>(), Procedure = Marshal.GetFunctionPointerForDelegate(Procedure), Instance = GetModuleHandle(null), ClassName = WindowClass };
            if (RegisterClassEx(ref windowClass) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            classRegistered = true;
        }
        bool review = Environment.GetEnvironmentVariable("GLASSDOCK_MATERIAL_REVIEW") == "1";
        uint exStyle = 0x00200000 | 0x08000000 | 0x20 | 0x8 | (review ? 0x40000u : 0x80u);
        hwnd = CreateWindowEx(exStyle, WindowClass, "GlassDock top bar native material", 0x80000000, 0, 0, 1, 1, 0, 0, GetModuleHandle(null), 0);
        if (hwnd == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        int enabled = 1;
        Marshal.ThrowExceptionForHR(BarNative.DwmSetWindowAttribute(hwnd, 17, ref enabled, 4));
        compositor = new Compositor();
        compositor.As<ICompositorDesktopInterop>().CreateDesktopWindowTarget(hwnd, true, out nint pointer);
        try { target = MarshalInterface<DesktopWindowTarget>.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
        root = compositor.CreateContainerVisual();
        target.Root = root;
        brush = compositor.CreateHostBackdropBrush();
        effectFactory = compositor.CreateEffectFactory(new NativeBlurEffect { Source = new CompositionEffectSourceParameter("Desktop") });
        effectBrush = effectFactory.CreateBrush();
        effectBrush.SetSourceParameter("Desktop", brush);
        material = compositor.CreateSpriteVisual();
        material.Brush = effectBrush;
        shape = compositor.CreateRoundedRectangleGeometry();
        material.Clip = compositor.CreateGeometricClip(shape);
        shadowShape = compositor.CreateRoundedRectangleGeometry();
        var maskShape = compositor.CreateSpriteShape(shadowShape);
        maskShape.FillBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
        shadowMaskVisual = compositor.CreateShapeVisual();
        shadowMaskVisual.Shapes.Add(maskShape);
        shadowSurface = compositor.CreateVisualSurface();
        shadowSurface.SourceVisual = shadowMaskVisual;
        shadowMask = compositor.CreateSurfaceBrush(shadowSurface);
        shadow = compositor.CreateDropShadow();
        shadow.Mask = shadowMask;
        shadow.Color = Windows.UI.Color.FromArgb(255, GlassTheme.ShadowColor.R, GlassTheme.ShadowColor.G, GlassTheme.ShadowColor.B);
        shadowVisual = compositor.CreateSpriteVisual();
        shadowVisual.Shadow = shadow;
        root.Children.InsertAtBottom(shadowVisual);
        root.Children.InsertAtTop(material);
    }

    internal void Sync(Window window,Rect? localBounds=null)
    {
        if (disposed) return;
        var foreground = new WindowInteropHelper(window).Handle;
        if (!window.IsVisible || !BarNative.GetWindowRect(foreground, out var bounds)) { BarNative.ShowWindowAsync(hwnd, 0); return; }
        float scale = (float)(HwndSource.FromHwnd(foreground)?.CompositionTarget?.TransformToDevice.M11 ?? 1d);
        if(localBounds is Rect area)
        {
            int x=bounds.Left,y=bounds.Top;
            bounds.Left=x+(int)Math.Round(area.Left*scale);bounds.Top=y+(int)Math.Round(area.Top*scale);
            bounds.Right=x+(int)Math.Round(area.Right*scale);bounds.Bottom=y+(int)Math.Round(area.Bottom*scale);
        }
        Sync(foreground,bounds,scale);
    }

    internal void SyncElement(FrameworkElement element)
    {
        if(disposed)return;
        if(!element.IsVisible||PresentationSource.FromVisual(element) is not HwndSource source||source.RootVisual is not System.Windows.Media.Visual rootVisual)
        {BarNative.ShowWindowAsync(hwnd,0);return;}
        if(!BarNative.GetWindowRect(source.Handle,out var bounds))return;
        var local=element.TransformToAncestor(rootVisual).TransformBounds(new Rect(element.RenderSize));
        float scale=(float)(source.CompositionTarget?.TransformToDevice.M11??1);
        int left=bounds.Left,top=bounds.Top;
        bounds.Left=left+(int)Math.Round(local.Left*scale);bounds.Top=top+(int)Math.Round(local.Top*scale);
        bounds.Right=left+(int)Math.Round(local.Right*scale);bounds.Bottom=top+(int)Math.Round(local.Bottom*scale);
        Sync(source.Handle,bounds,scale);
    }

    internal void Sync(nint foreground,BarNative.Rect bounds,float scale)
    {
        if(disposed)return;
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
        if(width<=0||height<=0)return;
        int padding = (int)Math.Ceiling(GlassTheme.ShadowPaddingDip * scale);
        Vector2 size = new(width, height);
        root.Size = new Vector2(width + padding * 2, height + padding * 2);
        material.Size = shadowVisual.Size = shadowMaskVisual.Size = shape.Size = shadowShape.Size = shadowSurface.SourceSize = size;
        material.Offset = shadowVisual.Offset = new Vector3(padding, padding, 0);
        shape.CornerRadius = shadowShape.CornerRadius = new Vector2((float)cornerRadiusDip * scale);
        shadow.BlurRadius = (float)GlassTheme.ShadowBlurDip * scale;
        shadow.Opacity = (float)GlassTheme.ShadowOpacity;
        shadow.Offset = new Vector3(0, (float)GlassTheme.ShadowDepthDip * scale, 0);
        BarNative.SetWindowPos(hwnd, foreground, bounds.Left - padding, bounds.Top - padding, (int)root.Size.X, (int)root.Size.Y, 0x0010 | 0x0040);
    }

    internal void Fade(float opacity, int milliseconds)
    {
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1, opacity);
        animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
        root.StartAnimation(nameof(Visual.Opacity), animation);
    }
    internal void SetOpacity(float opacity)=>root.Opacity=opacity;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        target.Root = null;
        shadowVisual.Dispose(); material.Dispose(); shape.Dispose(); shadowShape.Dispose(); shadow.Dispose();
        shadowMask.Dispose(); shadowSurface.Dispose(); shadowMaskVisual.Dispose(); effectBrush.Dispose(); effectFactory.Dispose(); brush.Dispose(); root.Dispose(); target.Dispose(); compositor.Dispose();
        DestroyWindow(hwnd);
        if (dispatcherController != 0) { Marshal.Release(dispatcherController); dispatcherController = 0; }
    }

    static nint ProcessMessage(nint window, uint message, nint w, nint l) => message switch
    {
        0x84 => -1,
        0x21 => 3,
        0x14 => 1,
        _ => DefWindowProc(window, message, w, l)
    };

    delegate nint WindowProcedure(nint window, uint message, nint w, nint l);
    [StructLayout(LayoutKind.Sequential)] struct DispatcherQueueOptions { internal int Size, ThreadType, ApartmentType; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WindowClassEx
    {
        internal uint Size, Style;
        internal nint Procedure;
        internal int ClassExtra, WindowExtra;
        internal nint Instance, Icon, Cursor, Background;
        internal string? MenuName;
        internal string ClassName;
        internal nint SmallIcon;
    }
    [DllImport("CoreMessaging.dll")] static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out nint controller);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassEx(ref WindowClassEx value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint DefWindowProc(nint window, uint message, nint w, nint l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandle(string? name);
    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ICompositorDesktopInterop { void CreateDesktopWindowTarget(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool topmost, out nint target); }
}
