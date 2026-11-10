using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Forms=System.Windows.Forms;
namespace GlassDock.TopBar;

// The glass Start screen opened by the Windows key: a search box over the
// app categories, centered on the monitor under the mouse like Spotlight.
// Same material, tint and edges as the Dock and the top bar menus.
internal sealed class StartMenuWindow : Window
{
    internal const double PanelWidth=820,MaxPanelHeight=580,CornerDip=28;
    const double TileWidth=164,TileHeight=144,ItemWidth=116;
    // Vertical layout of the category screen, used to fit the panel to the rows.
    const double PadTop=26,PadBottom=24,SearchHeight=48,GridGap=18,LabelGap=9,CellGap=18,TileColumns=4,LabelFontSize=13;
    static readonly FontFamily SymbolFont=new("Segoe Fluent Icons, Segoe MDL2 Assets");
    readonly StartApps catalog=new();
    readonly TextBox search=new();
    readonly TextBlock placeholder=new() {Text="앱 검색",FontSize=17,IsHitTestVisible=false,VerticalAlignment=VerticalAlignment.Center,Opacity=.6,Margin=new Thickness(3,0,0,0)};
    readonly ScrollViewer scroller=new() {VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Margin=new Thickness(0,GridGap,0,0),Focusable=false};
    readonly Border searchBox;
    List<StartApp> apps=new();
    List<StartApp> results=new();
    readonly List<Border> resultItems=new();
    int selected;
    string? openCategory;
    HostBackdrop? material;
    bool presenting,dismissing,bright;
    bool activatedWhilePresenting; // focused during PresentAsync, so a later loss is real
    bool reporting;                // our own launch-failure MessageBox has focus
    double panelHeight=MaxPanelHeight;
    Task? loading;
    // Re-reads the glass behind the text while the screen is open, so the text
    // colour follows a backdrop that changes (a window opening, a moving wallpaper).
    readonly DispatcherTimer backdropTimer=new() {Interval=TimeSpan.FromMilliseconds(400)};
    // Hysteresis around GlassPopup.BrightLevel so the colour does not flicker.
    internal const double DarkenBelow=130,BrightenAbove=160;
    internal bool BrightBackdrop=>bright;
    internal bool IsOpen=>IsVisible&&!dismissing;
    internal string? OpenCategory=>openCategory;
    internal IReadOnlyList<StartApp> Results=>results;
    internal IReadOnlyList<StartApp> Apps=>apps;
    internal Task Loading=>loading??Task.CompletedTask;
    // Height in DIP chosen for the current opening (fixed while it is open).
    internal double PanelHeight=>panelHeight;
    internal double ScrollableHeight=>scroller.ScrollableHeight;
    internal string SearchText {get=>search.Text;set=>search.Text=value;}
    internal bool SearchFocused=>search.IsKeyboardFocused;
    // Category tiles, or app items in a category or in search results.
    internal int ShownCount=>scroller.Content switch
    {
        UniformGrid grid=>grid.Children.Count,
        WrapPanel wrap=>wrap.Children.Count,
        StackPanel panel when panel.Children.Count>1&&panel.Children[1] is WrapPanel wrap=>wrap.Children.Count,
        _=>0,
    };

    internal StartMenuWindow()
    {
        Title="GlassDock · 시작";WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;AllowsTransparency=true;
        Background=Brushes.Transparent;ShowInTaskbar=false;Topmost=true;Width=PanelWidth;Height=MaxPanelHeight;
        FontFamily=new FontFamily("Segoe UI, Malgun Gothic");Foreground=Brushes.White;
        var root=new Grid();
        root.Children.Add(GlassTheme.CreateLayers(out _,CornerDip));
        var content=new Grid {Margin=new Thickness(28,PadTop,28,PadBottom)};
        content.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
        content.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(content);

        search.Background=Brushes.Transparent;search.BorderThickness=new Thickness(0);search.FontSize=17;
        search.VerticalContentAlignment=VerticalAlignment.Center;search.Padding=new Thickness(0);
        search.TextChanged+=(_,_)=>{selected=0;Refresh();};
        var field=new Grid {Margin=new Thickness(16,0,16,0)};
        field.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});field.ColumnDefinitions.Add(new ColumnDefinition());
        var glass=new TextBlock {Text="",FontFamily=SymbolFont,FontSize=17,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,12,0),Opacity=.8};
        field.Children.Add(glass);
        var input=new Grid();Grid.SetColumn(input,1);input.Children.Add(placeholder);input.Children.Add(search);field.Children.Add(input);
        searchBox=new Border {Height=SearchHeight,CornerRadius=new CornerRadius(24),BorderThickness=new Thickness(1),Child=field};
        content.Children.Add(searchBox);
        Grid.SetRow(scroller,1);content.Children.Add(scroller);
        Content=root;

        PreviewKeyDown+=OnKey;
        Activated+=(_,_)=>{if(presenting)activatedWhilePresenting=true;};
        Deactivated+=(_,_)=>{if(!presenting&&!reporting)Dismiss();};
        SourceInitialized+=(_,_)=>
        {
            var hwnd=new WindowInteropHelper(this).Handle;
            // Tool window: no Alt+Tab entry, like the Start menu it replaces.
            SetWindowLong(hwnd,-20,BarNative.GetWindowLong(hwnd,-20)|0x80);
        };
        backdropTimer.Tick+=(_,_)=>FollowBackdrop();
        IsVisibleChanged+=(_,_)=>{if(!IsVisible)backdropTimer.Stop();};
        Closed+=(_,_)=>{backdropTimer.Stop();material?.Dispose();material=null;catalog.Dispose();};
        loading=ReloadAsync();
    }

    internal async void Toggle()
    {
        try{if(IsOpen)Dismiss();else await PresentAsync();}
        catch(Exception e){BarSettings.Log(e);Dismiss();}
    }

    internal async Task PresentAsync()
    {
        if(IsOpen||presenting)return;
        presenting=true;dismissing=false;activatedWhilePresenting=false;
        try
        {
            openCategory=null;search.Text="";selected=0;
            var screen=Forms.Screen.FromPoint(Forms.Cursor.Position).Bounds;
            var monitor=BarNative.MonitorFromPoint(new BarNative.Point {X=screen.Left+screen.Width/2,Y=screen.Top+screen.Height/2},2);
            BarNative.GetDpiForMonitor(monitor,0,out uint dpi,out _);
            double scale=(dpi==0?96:dpi)/96.0;
            panelHeight=HeightFor(StartCatalog.Group(apps).Count);Height=panelHeight;
            int width=(int)Math.Round(PanelWidth*scale),height=(int)Math.Round(panelHeight*scale);
            int left=screen.Left+(screen.Width-width)/2,top=screen.Top+(screen.Height-height)/2;
            ApplyForeground(GlassPopup.IsBrightBehind(left,top,width,height));
            Refresh();
            BeginAnimation(OpacityProperty,null);Opacity=0;
            Show();
            var hwnd=new WindowInteropHelper(this).Handle;
            BarNative.SetWindowPos(hwnd,(nint)(-1),left,top,width,height,0x10);
            Activate();BarNative.SetForegroundWindow(hwnd);
            search.Focus();Keyboard.Focus(search);
            try
            {
                material??=new HostBackdrop(CornerDip);
                material.SetOpacity(0);
                material.Sync(hwnd,new BarNative.Rect {Left=left,Top=top,Right=left+width,Bottom=top+height},(float)scale);
                await material.CommittedAsync();
            }
            catch(Exception e){BarSettings.Log(e);material?.Dispose();material=null;}
            if(!IsVisible||dismissing)return;
            // WPF and Composition run on separate clocks; one short shared fade
            // keeps the glass and the icons appearing together.
            BeginAnimation(OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(130)));
            material?.Fade(1,130);
            backdropTimer.Start();
            if(loading?.IsCompleted!=false)loading=ReloadAsync(); // pick up apps installed since the last open
        }
        finally
        {
            presenting=false;
            // Deactivated is ignored while opening, so a focus change during the
            // fade-in would leave an unfocused screen on top. Re-check now.
            if(IsVisible&&!dismissing&&!reporting&&!HasFocus())
            {
                // Never focused at all (e.g. the foreground lock refused the Windows
                // key's activation): try once more. Focused and then lost it: the
                // user moved on, so close like Deactivated would have.
                if(!activatedWhilePresenting)
                {
                    Activate();BarNative.SetForegroundWindow(new WindowInteropHelper(this).Handle);
                    search.Focus();Keyboard.Focus(search);
                }
                if(!HasFocus())Dismiss();
            }
        }
    }

    bool HasFocus()=>IsActive||BarNative.GetForegroundWindow()==new WindowInteropHelper(this).Handle;

    internal void Dismiss()
    {
        if(!IsVisible||dismissing)return;
        dismissing=true;
        backdropTimer.Stop();
        material?.Hide();
        BeginAnimation(OpacityProperty,null);
        Hide();
        dismissing=false;
    }

    async Task ReloadAsync()
    {
        try
        {
            var loaded=await catalog.LoadAsync();
            bool changed=loaded.Count!=apps.Count||!loaded.Select(a=>a.Id).SequenceEqual(apps.Select(a=>a.Id));
            apps=loaded;
            if(changed&&IsVisible)Refresh();
        }
        catch(Exception e){BarSettings.Log(e);}
    }

    // Fits the panel to the category rows (at least two, at most MaxPanelHeight;
    // more rows scroll). Chosen before opening; the glass is sized to match.
    internal double HeightFor(int categories)
    {
        int rows=Math.Max(2,(int)Math.Ceiling(categories/TileColumns));
        var probe=new TextBlock {Text="생산성 Ag",FontSize=LabelFontSize,FontFamily=FontFamily};
        probe.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
        double row=TileHeight+LabelGap+Math.Ceiling(probe.DesiredSize.Height)+CellGap;
        return Math.Min(MaxPanelHeight,Math.Ceiling(PadTop+SearchHeight+GridGap+rows*row+PadBottom)+2);
    }

    void ApplyForeground(bool bright)
    {
        this.bright=bright;
        var text=bright?new SolidColorBrush(Color.FromRgb(24,32,46)):Brushes.White;
        Foreground=text;search.Foreground=text;search.CaretBrush=text;placeholder.Foreground=text;
        searchBox.Background=new SolidColorBrush(bright?Color.FromArgb(46,255,255,255):Color.FromArgb(34,255,255,255));
        searchBox.BorderBrush=new SolidColorBrush(Color.FromArgb((byte)(bright?70:56),255,255,255));
    }

    // Samples the empty margins of this window (no icons or text there) on the
    // composed screen, i.e. the blurred and tinted glass the text sits on.
    // Points stay clear of the 3-5 px edge highlight.
    internal static List<(int X,int Y)> MarginSamples(int left,int top,int width,int height)
    {
        var points=new List<(int X,int Y)>();
        foreach(double f in new[]{.25,.5,.75})
        {
            int y=top+(int)(height*f);
            points.Add((left+12,y));points.Add((left+width-12,y));
            points.Add((left+(int)(width*f),top+height-10));
        }
        foreach(double f in new[]{.25,.75})points.Add((left+(int)(width*f),top+10));
        return points;
    }

    internal static bool NextBright(bool current,double level)
    {
        if(double.IsNaN(level))return current;
        return current?level>=DarkenBelow:level>BrightenAbove;
    }

    void FollowBackdrop()
    {
        if(!IsVisible||dismissing||presenting)return;
        try
        {
            var hwnd=new WindowInteropHelper(this).Handle;
            if(hwnd==0||!BarNative.GetWindowRect(hwnd,out var r))return;
            int width=r.Right-r.Left,height=r.Bottom-r.Top;
            if(width<40||height<40)return;
            bool next=NextBright(bright,GlassPopup.MedianLevel(MarginSamples(r.Left,r.Top,width,height)));
            if(next==bright)return;
            ApplyForeground(next);
            // Redraw in place: same search, category and selection, same scroll offset.
            double offset=scroller.VerticalOffset;
            Refresh();
            scroller.UpdateLayout();
            scroller.ScrollToVerticalOffset(offset);
        }
        catch(Exception e){BarSettings.Log(e);backdropTimer.Stop();}
    }

    // Search results replace the categories while there is text in the box.
    void Refresh()
    {
        placeholder.Visibility=search.Text.Length==0?Visibility.Visible:Visibility.Collapsed;
        if(search.Text.Trim().Length>0)ShowResults(StartCatalog.Search(apps,search.Text));
        else if(openCategory!=null)ShowCategory(openCategory);
        else ShowCategories();
    }

    void ShowCategories()
    {
        results=new();resultItems.Clear();
        if(apps.Count==0){scroller.Content=Note(loading?.IsCompleted==false?"앱 목록을 불러오는 중…":"표시할 앱이 없습니다.");return;}
        var grid=new UniformGrid {Columns=4,VerticalAlignment=VerticalAlignment.Top};
        foreach(var (category,members) in StartCatalog.Group(apps))grid.Children.Add(Tile(category,members));
        scroller.Content=grid;scroller.ScrollToTop();
    }

    FrameworkElement Tile(string category,List<StartApp> members)
    {
        var slots=new UniformGrid {Rows=2,Columns=2,Margin=new Thickness(14)};
        int direct=members.Count<=4?members.Count:3;
        for(int i=0;i<direct;i++)slots.Children.Add(TileIcon(members[i]));
        if(members.Count>4)
        {
            // Fourth slot: the next apps as a small cluster, like the Start menu.
            var cluster=new UniformGrid {Rows=2,Columns=2,Width=46,Height=46,Background=Brushes.Transparent,Cursor=Cursors.Hand,ToolTip=$"{category} · 앱 {members.Count}개"};
            foreach(var app in members.Skip(3).Take(4))cluster.Children.Add(AppIcon(app,20,new Thickness(1.5)));
            cluster.MouseLeftButtonUp+=(_,e)=>{e.Handled=true;Open(category);};
            slots.Children.Add(cluster);
        }
        var tile=new Border {Width=TileWidth,Height=TileHeight,CornerRadius=new CornerRadius(20),BorderThickness=new Thickness(1),Child=slots,Cursor=Cursors.Hand};
        Highlight(tile,false,true);
        tile.MouseEnter+=(_,_)=>Highlight(tile,true,true);tile.MouseLeave+=(_,_)=>Highlight(tile,false,true);
        tile.MouseLeftButtonUp+=(_,e)=>{if(!e.Handled)Open(category);};
        var label=new TextBlock {Text=category,FontSize=LabelFontSize,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,LabelGap,0,0),Cursor=Cursors.Hand};
        label.MouseLeftButtonUp+=(_,_)=>Open(category);
        var cell=new StackPanel {Margin=new Thickness(0,0,0,CellGap),HorizontalAlignment=HorizontalAlignment.Center};
        cell.Children.Add(tile);cell.Children.Add(label);
        AutomationName(cell,category);
        return cell;
    }

    FrameworkElement TileIcon(StartApp app)
    {
        var host=new Border {Background=Brushes.Transparent,CornerRadius=new CornerRadius(12),Cursor=Cursors.Hand,ToolTip=app.Name,Child=AppIcon(app,46,new Thickness(0))};
        host.MouseEnter+=(_,_)=>host.Background=new SolidColorBrush(Color.FromArgb((byte)(bright?120:40),255,255,255));
        host.MouseLeave+=(_,_)=>host.Background=Brushes.Transparent;
        host.MouseLeftButtonUp+=(_,e)=>{e.Handled=true;Launch(app);};
        AutomationName(host,app.Name);
        return host;
    }

    internal void Open(string category){openCategory=category;Refresh();}

    void ShowCategory(string category)
    {
        results=StartCatalog.Group(apps).FirstOrDefault(g=>g.Category==category).Apps??new();
        var panel=new StackPanel();
        var header=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(2,0,0,14)};
        var back=new Border {Width=34,Height=34,CornerRadius=new CornerRadius(17),Cursor=Cursors.Hand,BorderThickness=new Thickness(1),ToolTip="모든 범주",
            Child=new TextBlock {Text="",FontFamily=SymbolFont,FontSize=14,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center}};
        Highlight(back,false,true);
        back.MouseEnter+=(_,_)=>Highlight(back,true,true);back.MouseLeave+=(_,_)=>Highlight(back,false,true);
        back.MouseLeftButtonUp+=(_,_)=>{openCategory=null;Refresh();};
        AutomationName(back,"모든 범주");
        header.Children.Add(back);
        header.Children.Add(new TextBlock {Text=category,FontSize=17,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,0,0)});
        panel.Children.Add(header);
        panel.Children.Add(AppGrid(results,-1));
        scroller.Content=panel;scroller.ScrollToTop();
    }

    void ShowResults(List<StartApp> found)
    {
        results=found;selected=Math.Clamp(selected,0,Math.Max(0,found.Count-1));
        if(found.Count==0){resultItems.Clear();scroller.Content=Note($"'{search.Text.Trim()}'와(과) 일치하는 앱이 없습니다.");return;}
        scroller.Content=AppGrid(found,selected);scroller.ScrollToTop();
    }

    WrapPanel AppGrid(List<StartApp> list,int highlighted)
    {
        resultItems.Clear();
        var wrap=new WrapPanel();
        for(int i=0;i<list.Count;i++)
        {
            var app=list[i];
            var stack=new StackPanel {Margin=new Thickness(6,10,6,8)};
            stack.Children.Add(AppIcon(app,48,new Thickness(0,0,0,8)));
            stack.Children.Add(new TextBlock {Text=app.Name,FontSize=12,TextAlignment=TextAlignment.Center,TextWrapping=TextWrapping.Wrap,MaxHeight=34,TextTrimming=TextTrimming.CharacterEllipsis});
            var item=new Border {Width=ItemWidth,Margin=new Thickness(2),CornerRadius=new CornerRadius(14),BorderThickness=new Thickness(1),Child=stack,Cursor=Cursors.Hand,ToolTip=app.Name};
            int index=i;
            Highlight(item,i==highlighted,false);
            item.MouseEnter+=(_,_)=>Highlight(item,true,false);
            item.MouseLeave+=(_,_)=>Highlight(item,index==highlighted&&index==selected&&search.Text.Length>0,false);
            item.MouseLeftButtonUp+=(_,_)=>Launch(app);
            AutomationName(item,app.Name);
            resultItems.Add(item);wrap.Children.Add(item);
        }
        return wrap;
    }

    // Tiles and the back button are always drawn, like the Start menu's light
    // grey category boxes; app items only show while hovered or selected.
    void Highlight(Border border,bool on,bool tile)
    {
        byte fill,edge;
        if(tile)(fill,edge)=bright?(on?((byte)150,(byte)190):((byte)110,(byte)150)):(on?((byte)64,(byte)90):((byte)40,(byte)60));
        else (fill,edge)=on?(bright?((byte)120,(byte)160):((byte)52,(byte)80)):((byte)0,(byte)0);
        border.Background=new SolidColorBrush(Color.FromArgb(fill,255,255,255));
        border.BorderBrush=new SolidColorBrush(Color.FromArgb(edge,255,255,255));
    }

    Image AppIcon(StartApp app,double size,Thickness margin)
    {
        var image=new Image {Width=size,Height=size,Margin=margin,HorizontalAlignment=HorizontalAlignment.Center,IsHitTestVisible=false};
        RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);
        LoadIcon(image,app,size);
        return image;
    }

    async void LoadIcon(Image image,StartApp app,double size)
    {
        try
        {
            double scale=VisualTreeHelper.GetDpi(this).DpiScaleX;
            var icon=await catalog.IconAsync(app.Id,(int)Math.Ceiling(size*Math.Max(1,scale)));
            image.Source=icon??Fallback;
        }
        catch(Exception e){BarSettings.Log(e);image.Source=Fallback;}
    }

    static readonly ImageSource Fallback=CreateFallback();
    static ImageSource CreateFallback()
    {
        var geometry=Geometry.Parse("M4,2 H20 A2,2 0 0 1 22,4 V20 A2,2 0 0 1 20,22 H4 A2,2 0 0 1 2,20 V4 A2,2 0 0 1 4,2 Z");
        var drawing=new DrawingImage(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(150,140,152,176)),null,geometry));drawing.Freeze();return drawing;
    }

    TextBlock Note(string text)=>new() {Text=text,FontSize=14,Opacity=.8,Margin=new Thickness(6,12,0,0)};

    static void AutomationName(UIElement element,string name)=>System.Windows.Automation.AutomationProperties.SetName(element,name);

    void Launch(StartApp app)
    {
        try{StartApps.Launch(app);Dismiss();}
        catch(Exception e)
        {
            BarSettings.Log(e);
            reporting=true; // the MessageBox takes focus; keep the screen open behind it
            try{MessageBox.Show(this,$"{app.Name}을(를) 실행하지 못했습니다.\n{e.Message}","시작");}
            finally{reporting=false;}
            if(IsVisible&&!IsActive)Dismiss(); // focus went elsewhere once the box closed
        }
    }

    void OnKey(object sender,KeyEventArgs e)
    {
        switch(e.Key)
        {
            case Key.Escape:
                e.Handled=true;
                if(search.Text.Length>0)search.Text="";
                else if(openCategory!=null){openCategory=null;Refresh();}
                else Dismiss();
                break;
            case Key.Enter:
                e.Handled=true;
                if(results.Count>0&&(search.Text.Trim().Length>0||openCategory!=null))Launch(results[Math.Clamp(selected,0,results.Count-1)]);
                break;
            case Key.Left: case Key.Right: case Key.Up: case Key.Down:
                if(search.Text.Trim().Length==0||results.Count==0)return;
                e.Handled=true;
                int columns=Math.Max(1,(int)((scroller.ActualWidth-4)/(ItemWidth+4)));
                int step=e.Key switch {Key.Left=>-1,Key.Right=>1,Key.Up=>-columns,_=>columns};
                Select(Math.Clamp(selected+step,0,results.Count-1));
                break;
        }
    }

    void Select(int index)
    {
        if(index<0||index>=resultItems.Count)return;
        Highlight(resultItems[selected<resultItems.Count?selected:0],false,false);
        selected=index;Highlight(resultItems[selected],true,false);
        resultItems[selected].BringIntoView();
    }

    [DllImport("user32.dll",EntryPoint="SetWindowLongW")] static extern int SetWindowLong(nint hwnd,int index,int value);
}
