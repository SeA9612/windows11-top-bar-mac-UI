using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms=System.Windows.Forms;
namespace GlassDock.TopBar;
public sealed class TopBarWindow : Window
{
    readonly BarSettings settings=BarSettings.Load();
    readonly DispatcherTimer clockTimer=new() {Interval=TimeSpan.FromSeconds(1)};
    readonly DispatcherTimer statusTimer=new() {Interval=TimeSpan.FromSeconds(3)};
    readonly DispatcherTimer backgroundTimer=new() {Interval=TimeSpan.FromMilliseconds(200)};
    readonly SolidColorBrush backgroundBrush=new(Color.FromRgb(25,28,36)) {Opacity=0};
    readonly TextBlock appName=new() {Text="바탕화면",FontWeight=FontWeights.SemiBold,FontSize=13,MaxWidth=220,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(7,0,10,0)};
    readonly StackPanel menus=new() {Orientation=Orientation.Horizontal};
    readonly Button start=new(),language=new(),network=new(),sound=new(),battery=new(),overflow=new(),clock=new();
    readonly Forms.NotifyIcon tray;
    AppBarHost? appBar;
    Popup? openPopup;
    TrayOverflowHost? trayOverflow;
    NotificationPositionHost? notificationPosition;
    bool closeTrayOnClick;
    nint currentWindow;
    bool shuttingDown;
    bool autoHiddenMode;
    long edgeSince,keepVisibleUntil;
    static readonly FontFamily SymbolFont=new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public TopBarWindow()
    {
        Title="GlassDock · 상단 바";WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;ShowActivated=false;Topmost=true;
        Width=800;Height=28;Foreground=Brushes.White;AllowsTransparency=true;Background=Brushes.Transparent;
        var surface=new Border {Background=backgroundBrush};
        Content=surface;
        var grid=new Grid {Margin=new Thickness(8,0,10,0)};grid.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});surface.Child=grid;
        var left=new StackPanel {Orientation=Orientation.Horizontal};
        Configure(start,WindowsIcon(),ShowSystemMenu,"Windows 시스템 메뉴");start.Padding=new Thickness(9,0,9,0);left.Children.Add(start);left.Children.Add(appName);left.Children.Add(menus);grid.Children.Add(left);
        var apps=MakeButton("앱",()=>ShowAppsMenu(),"글래스독 앱 메뉴");
        var windows=MakeButton("창",()=>ShowWindowMenu(),"현재 앱 창 조작");
        var help=MakeButton("도움말",()=>ShowHelp(),"상단 바 안내");menus.Children.Add(apps);menus.Children.Add(windows);menus.Children.Add(help);
        var right=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Stretch};Grid.SetColumn(right,2);grid.Children.Add(right);
        Configure(language,"한국어",ShowLanguages,"키보드 언어 선택 · 한/영 모드 자체를 표시하는 기능은 아닙니다.");right.Children.Add(language);
        Configure(network,Symbol("\uE774"),ShowNetwork,"네트워크 상태");right.Children.Add(network);
        Configure(sound,Symbol("\uE767"),ShowSound,"시스템 음량");right.Children.Add(sound);
        Configure(battery,"",()=>Launch("ms-settings:powersleep"),"배터리 및 전원");battery.Visibility=Visibility.Collapsed;right.Children.Add(battery);
        Configure(overflow,Symbol("\uE70E"),ShowTray,"숨겨진 트레이 아이콘");right.Children.Add(overflow);
        trayOverflow=new TrayOverflowHost(overflow,isOpen=>overflow.Content=Symbol(isOpen?"\uE70D":"\uE70E"));
        overflow.PreviewMouseLeftButtonDown+=(_,_)=>closeTrayOnClick=trayOverflow.IsOpen;
        Configure(clock,"",ShowCalendar,"현재 PC의 날짜와 시간");right.Children.Add(clock);
        surface.ContextMenu=BuildContextMenu();
        tray=new Forms.NotifyIcon {Icon=System.Drawing.SystemIcons.Application,Text="GlassDock 상단 바",Visible=true};
        var trayMenu=new Forms.ContextMenuStrip();trayMenu.Items.Add("상단 바 표시",null,(_,_)=>Dispatcher.Invoke(()=>{keepVisibleUntil=Environment.TickCount64+3000;ShowBar();}));trayMenu.Items.Add("상단 바 종료",null,(_,_)=>Dispatcher.Invoke(Close));tray.ContextMenuStrip=trayMenu;
        Loaded+=(_,_)=>{appBar=new AppBarHost(this,SelectedScreen);notificationPosition=new NotificationPositionHost(this);Tick();RefreshStatus();RefreshBackground();clockTimer.Start();statusTimer.Start();backgroundTimer.Start();};
        backgroundTimer.Tick+=(_,_)=>RefreshBackground();
        clockTimer.Tick+=(_,_)=>Tick();statusTimer.Tick+=(_,_)=>RefreshStatus();
        SizeChanged+=(_,_)=>{menus.Visibility=ActualWidth<850?Visibility.Collapsed:Visibility.Visible;appName.MaxWidth=ActualWidth<850?120:220;appName.Visibility=ActualWidth<600?Visibility.Collapsed:Visibility.Visible;};
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged+=OnDisplayChanged;
        Closed+=(_,_)=>{shuttingDown=true;clockTimer.Stop();statusTimer.Stop();backgroundTimer.Stop();if(openPopup!=null)openPopup.IsOpen=false;notificationPosition?.Dispose();trayOverflow?.Dispose();appBar?.Dispose();tray.Dispose();Microsoft.Win32.SystemEvents.DisplaySettingsChanged-=OnDisplayChanged;};
    }
    Forms.Screen SelectedScreen()=>Forms.Screen.AllScreens.FirstOrDefault(s=>s.DeviceName==settings.Monitor)??Forms.Screen.PrimaryScreen??Forms.Screen.AllScreens[0];
    void ShowBar(){Show();appBar?.Place();}
    void RefreshBackground()
    {
        var bounds=SelectedScreen().Bounds;
        bool maximized=BarBackground.GetOpacity(new BarNative.Rect {Left=bounds.Left,Top=bounds.Top,Right=bounds.Right,Bottom=bounds.Bottom})>0;
        if(autoHiddenMode!=maximized){autoHiddenMode=maximized;edgeSince=0;keepVisibleUntil=0;appBar?.SetAutoHidden(maximized);}
        backgroundBrush.Opacity=maximized?1:0;
        if(!maximized){if(!IsVisible)ShowBar();return;}
        long now=Environment.TickCount64;
        var cursor=Forms.Cursor.Position;
        bool atEdge=cursor.X>=bounds.Left&&cursor.X<bounds.Right&&cursor.Y>=bounds.Top&&cursor.Y<bounds.Top+2;
        if(atEdge){if(edgeSince==0)edgeSince=now;}else edgeSince=0;
        bool interacting=IsVisible&&(IsMouseOver||IsKeyboardFocusWithin||System.Windows.Input.Mouse.Captured!=null||openPopup?.IsOpen==true||trayOverflow?.IsInteracting==true||start.ContextMenu?.IsOpen==true);
        if(interacting||(edgeSince!=0&&now-edgeSince>=400))keepVisibleUntil=now+800;
        if(now<keepVisibleUntil){if(!IsVisible)ShowBar();}
        else if(IsVisible)Hide();
    }
    void OnDisplayChanged(object? sender,EventArgs e)=>Dispatcher.BeginInvoke(()=>{if(!shuttingDown){if(openPopup!=null)openPopup.IsOpen=false;trayOverflow?.Close();appBar?.Schedule();}});
    async void ShowTray()
    {
        if(trayOverflow==null)return;
        if(closeTrayOnClick){closeTrayOnClick=false;trayOverflow.Close();return;}
        if(openPopup!=null)openPopup.IsOpen=false;
        if(start.ContextMenu!=null)start.ContextMenu.IsOpen=false;
        try{await trayOverflow.ToggleAsync();}
        catch(Exception e){BarSettings.Log(e);if(!shuttingDown)MessageBox.Show(this,e.Message,"트레이 아이콘");}
    }
    static Image WindowsIcon()
    {
        var geometry=Geometry.Parse("M0,0 H6 V6 H0 Z M8,0 H14 V6 H8 Z M0,8 H6 V14 H0 Z M8,8 H14 V14 H8 Z");var drawing=new DrawingImage(new GeometryDrawing(Brushes.White,null,geometry));drawing.Freeze();return new Image {Source=drawing,Width=13,Height=13};
    }
    static TextBlock Symbol(string glyph)=>new() {Text=glyph,FontFamily=SymbolFont,FontSize=14,VerticalAlignment=VerticalAlignment.Center};
    Button MakeButton(object content,Action action,string tooltip){var b=new Button();Configure(b,content,action,tooltip);return b;}
    void Configure(Button button,object content,Action action,string tooltip)
    {
        // Fully zero-alpha pixels in a layered HWND pass native mouse input to
        // the desktop. Keep each button's entire rectangle clickable.
        button.Background=new SolidColorBrush(Color.FromArgb(1,255,255,255));
        button.Content=content;button.ToolTip=tooltip;button.Click+=(_,_)=>Run(action);
    }
    void Run(Action action){try{action();}catch(Exception e){BarSettings.Log(e);MessageBox.Show(this,e.Message,"상단 바");}}
    void Launch(string target)=>Run(()=>Process.Start(new ProcessStartInfo(target){UseShellExecute=true}));
    void Tick()
    {
        clock.Content=StatusText.Clock(DateTime.Now,ActualWidth<650);
        var hwnd=BarNative.GetForegroundWindow();BarNative.GetWindowThreadProcessId(hwnd,out uint pid);
        if(hwnd==0||pid==Environment.ProcessId)return;
        currentWindow=IsNormalAppWindow(hwnd)?hwnd:0;
        var title=new StringBuilder(512);BarNative.GetWindowText(hwnd,title,title.Capacity);appName.ToolTip=title.ToString();
        try
        {
            using var process=Process.GetProcessById((int)pid);
            string name=process.ProcessName;
            appName.Text=name.ToLowerInvariant() switch {"explorer"=>title.Length==0||title.ToString()=="Program Manager"?"바탕화면":"파일 탐색기","code"=>"Visual Studio Code","chrome"=>"Chrome","msedge"=>"Microsoft Edge","chatgpt"=>"ChatGPT","notepad"=>"메모장",_=>name};
        }
        catch{appName.Text=title.Length>0?title.ToString():"바탕화면";}
        try
        {
            uint thread=BarNative.GetWindowThreadProcessId(hwnd,out _);int id=(int)(BarNative.GetKeyboardLayout(thread).ToInt64()&0xFFFF);
            language.Content=StatusText.Keyboard(id);
        }
        catch{language.Content="언어";}
    }
    void RefreshStatus()
    {
        try
        {
            var connected=NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up&&n.NetworkInterfaceType!=NetworkInterfaceType.Loopback&&n.NetworkInterfaceType!=NetworkInterfaceType.Tunnel).ToArray();
            bool wireless=connected.Any(n=>n.NetworkInterfaceType==NetworkInterfaceType.Wireless80211);
            network.Content=Symbol(connected.Length==0?"\uEB55":wireless?"\uE701":"\uE839");
            network.ToolTip=connected.Length==0?"연결된 네트워크 없음":"네트워크 연결됨 · 인터넷 접속 가능 여부는 별도로 검사하지 않습니다.";
        }
        catch{network.Content=Symbol("\uE774");network.ToolTip="네트워크 상태를 읽지 못했습니다.";}
        try
        {
            using var endpoint=AudioEndpoint.Open();if(endpoint==null){sound.Content=Symbol("\uE74F");sound.ToolTip="출력 장치 없음 또는 상태 확인 불가";}
            else{var state=endpoint.Read();sound.Content=Symbol(state.Muted?"\uE74F":"\uE767");sound.ToolTip=state.Muted?"음소거":$"음량 {Math.Round(state.Volume*100)}%";}
        }
        catch{sound.Content=Symbol("\uE74F");sound.ToolTip="음량 상태를 읽지 못했습니다.";}
        var power=Forms.SystemInformation.PowerStatus;
        var label=StatusText.Battery(power.BatteryLifePercent,(power.BatteryChargeStatus&Forms.BatteryChargeStatus.NoSystemBattery)!=0||power.BatteryChargeStatus==Forms.BatteryChargeStatus.Unknown);
        battery.Visibility=label==null||ActualWidth<600?Visibility.Collapsed:Visibility.Visible;
        battery.Content=label;
        battery.ToolTip=power.PowerLineStatus==Forms.PowerLineStatus.Online?"외부 전원 연결됨":"배터리 사용 중";
    }
    static bool IsNormalAppWindow(nint hwnd)
    {
        if(hwnd==0||!BarNative.IsWindow(hwnd)||!BarNative.IsWindowVisible(hwnd)||hwnd==BarNative.GetShellWindow()||hwnd==BarNative.GetDesktopWindow())return false;
        if((BarNative.GetWindowLong(hwnd,-20)&0x80)!=0)return false;
        var name=new StringBuilder(128);BarNative.GetClassName(hwnd,name,name.Capacity);
        return name.ToString() is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    }
    void ShowSystemMenu()
    {
        trayOverflow?.Close();
        if(start.ContextMenu?.IsOpen==true){start.ContextMenu.IsOpen=false;return;}
        if(openPopup!=null)openPopup.IsOpen=false;
        var menu=new ContextMenu {Style=(Style)FindResource("SystemMenuStyle"),PlacementTarget=start,Placement=PlacementMode.Bottom,VerticalOffset=4};
        start.ContextMenu=menu;
        menu.Resources.Add(typeof(MenuItem),(Style)FindResource("SystemMenuItemStyle"));
        Add(menu,"이 PC에 관하여",()=>Launch("ms-settings:about"));
        menu.Items.Add(new Separator());
        Add(menu,"시스템 설정…",()=>Launch("ms-settings:"));
        Add(menu,"Microsoft Store…",()=>Launch("ms-windows-store:"));
        menu.Items.Add(new Separator());
        var recent=new MenuItem {Header="최근 사용 항목"};
        try
        {
            foreach(var file in new DirectoryInfo(Environment.GetFolderPath(Environment.SpecialFolder.Recent)).EnumerateFiles("*.lnk").OrderByDescending(f=>f.LastWriteTimeUtc).Take(10))
            {
                var path=file.FullName;
                var item=new MenuItem {Header=file.Name[..^4].Replace("_","__"),MaxWidth=360};
                item.Click+=(_,_)=>Run(()=>Launch(path));recent.Items.Add(item);
            }
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException){BarSettings.Log(e);}
        if(recent.Items.Count==0)recent.Items.Add(new MenuItem {Header="최근 항목 없음",IsEnabled=false});
        recent.Items.Add(new Separator());
        var openRecent=new MenuItem {Header="최근 항목 폴더 열기"};openRecent.Click+=(_,_)=>Launch(Environment.GetFolderPath(Environment.SpecialFolder.Recent));recent.Items.Add(openRecent);
        menu.Items.Add(recent);
        menu.Items.Add(new Separator());
        Add(menu,"작업 관리자…",()=>Launch("taskmgr.exe"));
        menu.Items.Add(new Separator());
        Add(menu,"잠자기",()=>ConfirmSystemAction("잠자기","PC를 절전 모드로 전환할까요?",()=>{
            if(!Forms.Application.SetSuspendState(Forms.PowerState.Suspend,false,false))throw new InvalidOperationException("절전 모드로 전환하지 못했습니다.");
        }));
        Add(menu,"재시동…",()=>ConfirmSystemAction("재시동","저장하지 않은 작업을 저장한 후 진행하세요. PC를 다시 시작할까요?",()=>Shutdown("/r /t 0")));
        Add(menu,"시스템 종료…",()=>ConfirmSystemAction("시스템 종료","저장하지 않은 작업을 저장한 후 진행하세요. PC를 종료할까요?",()=>Shutdown("/s /t 0")));
        menu.Items.Add(new Separator());
        Add(menu,"화면 잠금",()=>{if(!BarNative.LockWorkStation())throw new InvalidOperationException("화면을 잠그지 못했습니다.");});
        Add(menu,Environment.UserName.Replace("_","__")+" 로그아웃…",()=>ConfirmSystemAction("로그아웃","저장하지 않은 작업을 저장한 후 진행하세요. 로그아웃할까요?",()=>Shutdown("/l")));
        menu.IsOpen=true;
    }
    void ConfirmSystemAction(string title,string message,Action action)
    {
        if(MessageBox.Show(this,message,title,MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes)action();
    }
    static void Shutdown(string arguments)=>Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"shutdown.exe"),arguments) {UseShellExecute=false,CreateNoWindow=true});
    void ShowAppsMenu()
    {
        trayOverflow?.Close();
        var menu=new ContextMenu();Add(menu,"파일 탐색기 열기",()=>Launch("explorer.exe"));Add(menu,"Windows 설정",()=>Launch("ms-settings:"));Add(menu,"바탕화면 보기",()=>BarNative.Shortcut(0x5B,0x44));menu.PlacementTarget=(UIElement)menus.Children[0];menu.Placement=PlacementMode.Bottom;menu.IsOpen=true;
    }
    void ShowWindowMenu()
    {
        trayOverflow?.Close();
        Tick();
        var target=currentWindow;var menu=new ContextMenu();bool usable=IsNormalAppWindow(target);
        Add(menu,"현재 창 최소화",()=>BarNative.ShowWindowAsync(target,6),usable);
        Add(menu,BarNative.IsZoomed(target)?"현재 창 이전 크기로":"현재 창 최대화",()=>BarNative.ShowWindowAsync(target,BarNative.IsZoomed(target)?9:3),usable);
        Add(menu,"현재 창 닫기 요청",()=>BarNative.PostMessage(target,0x10,0,0),usable);
        menu.PlacementTarget=(UIElement)menus.Children[1];menu.Placement=PlacementMode.Bottom;menu.IsOpen=true;
    }
    void Add(ContextMenu menu,string text,Action action,bool enabled=true){var i=new MenuItem {Header=text,IsEnabled=enabled};i.Click+=(_,_)=>Run(action);menu.Items.Add(i);}
    ContextMenu BuildContextMenu()
    {
        var menu=new ContextMenu();var monitors=new MenuItem {Header="상단 바 모니터"};
        monitors.SubmenuOpened+=(_,_)=>{monitors.Items.Clear();foreach(var s in Forms.Screen.AllScreens){var i=new MenuItem {Header=s.DeviceName+(s.Primary?" · 주 모니터":""),IsCheckable=true,IsChecked=s.DeviceName==SelectedScreen().DeviceName};i.Click+=(_,_)=>Run(()=>{settings.Monitor=s.DeviceName;settings.Save();if(openPopup!=null)openPopup.IsOpen=false;appBar?.Schedule();});monitors.Items.Add(i);}};menu.Items.Add(monitors);
        Add(menu,"상단 바 안내",ShowHelp);menu.Items.Add(new Separator());Add(menu,"상단 바 종료",Close);return menu;
    }
    void ShowHelp()=>MessageBox.Show(this,"GlassDock 상단 바 0.1\n\nWindows 버튼: PC 정보·설정·최근 항목·전원 메뉴\n현재 앱: 실제 전경 앱\n한국어/ENG: 키보드 언어 (한/영 입력 모드와 다름)\n소리: 실제 시스템 음량과 음소거\n네트워크: 어댑터 연결 상태 및 설정\n트레이 화살표: 상단에 숨겨진 트레이 아이콘 표시\n알림: Windows 기본 알림 배너를 바 오른쪽 아래에 표시\n시계: 현재 PC 날짜와 시간\n\n카카오톡 자체 알림은 카카오톡 설정의 표시 위치를 화면 우상단으로 선택하세요.\n앱마다 다른 파일·편집 메뉴는 아직 지원하지 않습니다.\nWindows 기본 작업 표시줄은 유지합니다.\n우클릭 메뉴에서 모니터 변경과 종료가 가능합니다.","상단 바 안내");
    Popup OpenPanel(Button anchor,FrameworkElement content,Action? onClose=null)
    {
        trayOverflow?.Close();
        if(openPopup!=null)openPopup.IsOpen=false;
        var border=new Border {Background=GlassTheme.CreateAcrylicTint(),CornerRadius=GlassTheme.CornerRadius,Padding=new Thickness(16),Child=content};
        GlassPopup.SetMaterial(border,true);
        var layers=new Grid {Margin=new Thickness(4)};layers.Children.Add(border);layers.Children.Add(new GlassEdgeLayer {IsHitTestVisible=false,Focusable=false});
        var popup=new Popup {Child=layers,PlacementTarget=anchor,Placement=PlacementMode.Bottom,VerticalOffset=5,StaysOpen=false,AllowsTransparency=true};
        popup.Closed+=(_,_)=>{onClose?.Invoke();if(openPopup==popup)openPopup=null;};openPopup=popup;popup.IsOpen=true;return popup;
    }
    TextBlock Heading(string text)=>new() {Text=text,Foreground=Brushes.White,FontSize=14,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,12)};
    TextBlock Note(string text)=>new() {Text=text,Foreground=new SolidColorBrush(Color.FromRgb(184,195,215)),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,8)};
    void ShowNetwork()
    {
        var panel=new StackPanel {Width=280};panel.Children.Add(Heading("네트워크"));
        try
        {
            var adapters=NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up&&n.NetworkInterfaceType!=NetworkInterfaceType.Loopback&&n.NetworkInterfaceType!=NetworkInterfaceType.Tunnel).ToArray();
            if(adapters.Length==0)panel.Children.Add(Note("연결된 네트워크 어댑터가 없습니다."));
            foreach(var adapter in adapters.Take(5))panel.Children.Add(Note(adapter.Name+" · "+(adapter.NetworkInterfaceType==NetworkInterfaceType.Wireless80211?"Wi-Fi":"연결됨")));
            panel.Children.Add(Note("어댑터 연결 상태입니다. 인터넷 접속 성공 여부를 뜻하지 않습니다."));
        }
        catch{panel.Children.Add(Note("네트워크 상태를 읽지 못했습니다."));}
        panel.Children.Add(MakeButton("네트워크 설정 열기",()=>Launch("ms-settings:network-status"),"Windows 네트워크 설정"));OpenPanel(network,panel);
    }
    void ShowSound()
    {
        var panel=new StackPanel {Width=260};panel.Children.Add(Heading("소리"));var endpoint=AudioEndpoint.Open();
        if(endpoint==null){panel.Children.Add(Note("출력 장치를 찾지 못했거나 접근할 수 없습니다."));panel.Children.Add(MakeButton("소리 설정 열기",()=>Launch("ms-settings:sound"),"Windows 소리 설정"));OpenPanel(sound,panel);return;}
        try
        {
            var state=endpoint.Read();var value=Note($"음량 {Math.Round(state.Volume*100)}%");panel.Children.Add(value);
            var slider=new Slider {Minimum=0,Maximum=100,Value=state.Volume*100,Margin=new Thickness(0,8,0,12)};
            slider.ValueChanged+=(_,_)=>Run(()=>{endpoint.SetVolume((float)(slider.Value/100));value.Text=$"음량 {Math.Round(slider.Value)}%";});panel.Children.Add(slider);
            var mute=new CheckBox {Content="음소거",Foreground=Brushes.White,IsChecked=state.Muted,Margin=new Thickness(0,4,0,12)};mute.Checked+=(_,_)=>Run(()=>endpoint.SetMute(true));mute.Unchecked+=(_,_)=>Run(()=>endpoint.SetMute(false));panel.Children.Add(mute);
            panel.Children.Add(MakeButton("소리 설정 열기",()=>Launch("ms-settings:sound"),"Windows 소리 설정"));OpenPanel(sound,panel,()=>{endpoint.Dispose();RefreshStatus();});
        }
        catch{endpoint.Dispose();throw;}
    }
    void ShowLanguages()
    {
        Tick();
        var target=currentWindow;var panel=new StackPanel {Width=270};panel.Children.Add(Heading("키보드 언어"));
        foreach(Forms.InputLanguage input in Forms.InputLanguage.InstalledInputLanguages)
        {
            var selected=input;
            panel.Children.Add(MakeButton(input.Culture.NativeName+" · "+input.LayoutName,()=>{
                if(!BarNative.IsWindow(target))throw new InvalidOperationException("전환할 앱 창을 먼저 선택해 주세요.");
                if(!BarNative.PostMessage(target,0x50,0,selected.Handle))throw new InvalidOperationException("이 창의 키보드 언어 변경 요청을 보내지 못했습니다.");
                if(openPopup!=null)openPopup.IsOpen=false;
            },"현재 앱 창에 입력 언어 변경 요청"));
        }
        panel.Children.Add(Note("한국어/ENG는 키보드 언어입니다. 한국어 입력기의 가/A 모드는 표시하지 않습니다. 기존 한/영 키는 그대로 사용하세요."));
        panel.Children.Add(MakeButton("언어 설정 열기",()=>Launch("ms-settings:regionlanguage"),"Windows 언어 설정"));OpenPanel(language,panel);
    }
    void ShowCalendar()
    {
        var panel=new StackPanel {Width=280};panel.Children.Add(Heading(DateTime.Now.ToString("yyyy년 M월 d일 dddd",CultureInfo.GetCultureInfo("ko-KR"))));
        panel.Children.Add(new System.Windows.Controls.Calendar {Foreground=Brushes.Black,Background=Brushes.White,SelectedDate=DateTime.Today,DisplayDate=DateTime.Today,Language=System.Windows.Markup.XmlLanguage.GetLanguage("ko-KR")});
        panel.Children.Add(MakeButton("날짜 및 시간 설정",()=>Launch("ms-settings:dateandtime"),"Windows 날짜 및 시간 설정"));OpenPanel(clock,panel);
    }
}
