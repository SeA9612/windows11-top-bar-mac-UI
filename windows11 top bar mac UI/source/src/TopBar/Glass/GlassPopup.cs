using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
namespace GlassDock.TopBar;

// Uses the same compositor material, tint and edge artwork as the running Dock.
public static class GlassPopup
{
    public static readonly DependencyProperty MaterialProperty=DependencyProperty.RegisterAttached(
        "Material",typeof(bool),typeof(GlassPopup),new PropertyMetadata(false,Changed));
    static readonly DependencyProperty SessionProperty=DependencyProperty.RegisterAttached("Session",typeof(Session),typeof(GlassPopup));
    public static bool GetMaterial(DependencyObject element)=>(bool)element.GetValue(MaterialProperty);
    public static void SetMaterial(DependencyObject element,bool value)=>element.SetValue(MaterialProperty,value);
    static void Changed(DependencyObject target,DependencyPropertyChangedEventArgs args)
    {
        if(target is not Border border)return;
        (border.GetValue(SessionProperty) as Session)?.Dispose();
        border.SetValue(SessionProperty,(bool)args.NewValue?new Session(border):null);
    }

    sealed class Session : IDisposable
    {
        readonly Border border;
        HostBackdrop? host;
        bool failed,connected;
        Rect lastBounds=Rect.Empty;
        public Session(Border border)
        {
            this.border=border;
            border.Loaded+=Loaded;border.Unloaded+=Unloaded;border.LayoutUpdated+=Layout;
            if(border.IsLoaded)Connect();
        }
        void Loaded(object sender,RoutedEventArgs e)=>Connect();
        void Unloaded(object sender,RoutedEventArgs e){host?.Dispose();host=null;connected=false;lastBounds=Rect.Empty;}
        void Layout(object? sender,EventArgs e){if(connected)Sync();}
        void Connect()
        {
            if(connected)return;connected=true;failed=false;
            // The material is a separate HWND: keep its lifetime in step with
            // the menu instead of leaving an empty surface during WPF's fade.
            if(border.TemplatedParent is ContextMenu menu&&menu.Parent is Popup popup)popup.PopupAnimation=PopupAnimation.None;
            try
            {
                host=new HostBackdrop(border.CornerRadius.TopLeft);
                border.Background=GlassTheme.CreateAcrylicTint();
            }
            catch(Exception e){failed=true;border.Background=GlassTheme.CreateFallbackTint();BarSettings.Log(e);}
            Sync();
            border.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=>{if(connected)AdaptForeground(border);}));
        }
        void Sync()
        {
            if(failed||host==null||border.ActualWidth<=0||border.ActualHeight<=0)return;
            try
            {
                var origin=border.PointToScreen(new Point());
                var bounds=new Rect(origin,border.RenderSize);
                if(bounds==lastBounds)return;
                lastBounds=bounds;
                if(PresentationSource.FromVisual(border) is HwndSource source&&source.CompositionTarget!=null)source.CompositionTarget.BackgroundColor=Colors.Transparent;
                host.SyncElement(border);
            }
            catch(Exception e){host.Dispose();host=null;failed=true;border.Background=GlassTheme.CreateFallbackTint();BarSettings.Log(e);}
        }
        public void Dispose()
        {
            host?.Dispose();host=null;connected=false;
            border.Loaded-=Loaded;border.Unloaded-=Unloaded;border.LayoutUpdated-=Layout;
        }
    }

    static void AdaptForeground(Border border)
    {
        if(!border.IsVisible||border.ActualWidth<=0)return;
        var p=border.PointToScreen(new Point());var dpi=VisualTreeHelper.GetDpi(border);
        // Read only a few backing pixels to select legible text. No screen image
        // or message contents are collected, retained or written to disk.
        var levels=new System.Collections.Generic.List<double>();var dc=GetDC(0);
        try
        {
            foreach(double x in new[]{.15,.5,.85})foreach(double y in new[]{.2,.5,.8})
            {
                uint color=GetPixel(dc,(int)(p.X+border.ActualWidth*dpi.DpiScaleX*x),(int)(p.Y+border.ActualHeight*dpi.DpiScaleY*y));
                if(color!=0xFFFFFFFF)levels.Add(.2126*(color&255)+.7152*((color>>8)&255)+.0722*((color>>16)&255));
            }
        }
        finally{if(dc!=0)ReleaseDC(0,dc);}
        levels.Sort();
        Brush foreground=levels.Count>0&&levels[levels.Count/2]>145?new SolidColorBrush(Color.FromRgb(24,32,46)):Brushes.White;
        if(border.TemplatedParent is ContextMenu menu)
        {
            menu.Foreground=foreground;foreach(var item in menu.Items)if(item is MenuItem entry)SetMenuForeground(entry,foreground);
        }
        else if(border.TemplatedParent is MenuItem submenu)
        {
            foreach(var item in submenu.Items)if(item is MenuItem entry)SetMenuForeground(entry,foreground);
        }
        else SetPanelForeground(border,foreground);
    }
    static void SetMenuForeground(MenuItem item,Brush brush)
    {
        item.Foreground=brush;
        foreach(var child in item.Items)if(child is MenuItem entry)SetMenuForeground(entry,brush);
    }
    static void SetPanelForeground(DependencyObject element,Brush brush)
    {
        if(element is Calendar)return;
        if(element is TextBlock text)text.Foreground=brush;
        if(element is ButtonBase or Slider)((Control)element).Foreground=brush;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(element);i++)SetPanelForeground(VisualTreeHelper.GetChild(element,i),brush);
    }
    [DllImport("user32.dll")] static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint hwnd,nint dc);
    [DllImport("gdi32.dll")] static extern uint GetPixel(nint dc,int x,int y);
}
