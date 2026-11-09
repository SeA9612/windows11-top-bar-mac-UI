using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Interop;
using Forms=System.Windows.Forms;
namespace GlassDock.TopBar;

// Borrow the Explorer-owned flyout for one open session. Never reparent its XAML
// island, alter notification-area settings, or replace any application's icons.
internal sealed class TrayOverflowHost : IDisposable
{
    readonly Button anchor;
    readonly Action<bool> stateChanged;
    readonly DispatcherTimer timer=new(DispatcherPriority.Send) {Interval=TimeSpan.FromMilliseconds(30)};
    readonly BarNative.WinEventCallback eventCallback;
    readonly BarNative.WinEventCallback foregroundCallback;
    readonly BarNative.InputHookCallback mouseCallback,keyboardCallback;
    nint hook,popup,previousForeground,foregroundHook,mouseHook,keyboardHook;
    BarNative.Rect originalBounds,anchorBounds;
    bool closingPending;
    bool originalTopmost,hasOriginal,opening,disposed,positioning,seenVisible;
    int openGeneration;
    TrayGlassWindow? glass;
    NativeTrayOpacity? taskbarRendering;
    readonly Dictionary<nint,(BarNative.Rect Bounds,bool Topmost)> beforeOpen=new();
    public bool IsOpen=>popup!=0&&BarNative.IsWindow(popup)&&BarNative.IsWindowVisible(popup);
    public bool IsInteracting=>opening||IsOpen;
    internal System.Windows.Media.Imaging.BitmapSource? RenderedArtwork=>glass?.Artwork;
    internal int CaptureFrameCount=>glass?.FrameCount??0;
    internal int RenderedIconCount {get;private set;}

    public TrayOverflowHost(Button anchor,Action<bool> stateChanged)
    {
        this.anchor=anchor;this.stateChanged=stateChanged;
        eventCallback=OnShellEvent;
        foregroundCallback=OnForegroundChanged;
        mouseCallback=OnMouseInput;keyboardCallback=OnKeyboardInput;
        timer.Tick+=(_,_)=>{try{Track();}catch(Exception e){BarSettings.Log(e);Close();}};
    }

    public async Task ToggleAsync()
    {
        if(disposed||opening)return;
        if(IsOpen){Close();return;}
        Restore();
        foreach(string name in new[]{"TopLevelWindowForOverflowXamlIsland","NotifyIconOverflowWindow"})
        {
            var existing=BarNative.FindWindow(name,null);
            if(existing!=0&&BarNative.GetWindowRect(existing,out var saved))beforeOpen[existing]=(saved,(BarNative.GetWindowLong(existing,-20)&8)!=0);
        }
        var taskbar=BarNative.FindWindow("Shell_TrayWnd",null);
        if(taskbar==0)throw new InvalidOperationException("Windows 트레이를 찾지 못했습니다. 탐색기가 실행 중인지 확인해 주세요.");
        BarNative.GetWindowThreadProcessId(taskbar,out uint processId);
        previousForeground=BarNative.GetForegroundWindow();
        opening=true;seenVisible=false;
        int generation=++openGeneration;
        hook=BarNative.SetWinEventHook(0x8002,0x800B,0,eventCallback,processId,0,0);
        if(hook==0){opening=false;throw new Win32Exception(Marshal.GetLastWin32Error(),"트레이 위치 감시를 시작하지 못했습니다.");}
        timer.Start();
        try
        {
            // Showing a retained HWND does not expand Explorer's XAML state.
            // Use the real chevron every time so the icon visual tree is live.
            foreach(string name in new[]{"TopLevelWindowForOverflowXamlIsland","NotifyIconOverflowWindow"})
            {
                var old=BarNative.FindWindow(name,null);if(old!=0)BarNative.ShowWindow(old,0);
            }
            // UIA must run away from WPF's dispatcher, which receives shell events.
            // https://learn.microsoft.com/dotnet/framework/ui-automation/ui-automation-threading-issues
            var rendering=await Task.Run(()=>InvokeChevron(taskbar));
            if(disposed||generation!=openGeneration)
            {
                rendering?.Dispose();
                foreach(string name in new[]{"TopLevelWindowForOverflowXamlIsland","NotifyIconOverflowWindow"})
                {
                    var candidate=BarNative.FindWindow(name,null);BarNative.GetWindowThreadProcessId(candidate,out uint owner);
                    if(candidate!=0&&owner==processId)BarNative.ShowWindow(candidate,0);
                }
                return;
            }
            taskbarRendering=rendering;
            for(int attempt=0;attempt<60&&!disposed;attempt++)
            {
                Track();
                if(IsOpen){await ReadyAsync(generation);return;}
                await Task.Delay(25);
            }
            if(!disposed)throw new InvalidOperationException("Windows 트레이가 열리지 않았습니다. 숨겨진 트레이 아이콘이 있는지 확인해 주세요.");
        }
        catch
        {
            Close();throw;
        }
        finally
        {
            opening=false;
            if(disposed||!IsOpen)Restore();
        }
    }

    static NativeTrayOpacity? InvokeChevron(nint taskbar)
    {
        // Keep Explorer's XAML visuals live for the whole borrowed session.
        // A layered input-only taskbar prevents exposing the original taskbar.
        NativeTrayOpacity? rendering=null;
        try
        {
            if(TryInvokeChevron(taskbar))return null;
            rendering=new NativeTrayOpacity(taskbar);rendering.RevealTaskbarContent();
            for(int attempt=0;attempt<10;attempt++)
            {
                if(TryInvokeChevron(taskbar))return rendering;
                System.Threading.Thread.Sleep(80);
            }
            throw new InvalidOperationException("Windows의 숨겨진 아이콘 버튼을 찾지 못했습니다. 작업 표시줄의 트레이 메뉴가 사용 가능한지 확인해 주세요.");
        }
        catch{rendering?.Dispose();throw;}
    }

    static bool TryInvokeChevron(nint taskbar)
    {
        var root=AutomationElement.FromHandle(taskbar);
        var condition=new OrCondition(
            new PropertyCondition(AutomationElement.ClassNameProperty,"SystemTray.ChevronIconView"),
            new PropertyCondition(AutomationElement.AutomationIdProperty,"SystemTray.OverflowButton"),
            new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button),
                new OrCondition(new PropertyCondition(AutomationElement.NameProperty,"숨겨진 아이콘 표시"),
                    new PropertyCondition(AutomationElement.NameProperty,"Show hidden icons"))));
        var chevron=root.FindFirst(TreeScope.Descendants,condition);
        if(chevron==null)
        {
            var buttons=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ClassNameProperty,"SystemTray.AccentButton"));
            foreach(AutomationElement button in buttons)
            {
                string name=button.Current.Name;
                if(name.StartsWith("숨겨진 아이콘",StringComparison.Ordinal)||name.Contains("hidden icons",StringComparison.OrdinalIgnoreCase)){chevron=button;break;}
            }
        }
        if(chevron==null)return false;
        if(chevron.TryGetCurrentPattern(ExpandCollapsePattern.Pattern,out var expand))((ExpandCollapsePattern)expand).Expand();
        else if(chevron.TryGetCurrentPattern(InvokePattern.Pattern,out var invoke))((InvokePattern)invoke).Invoke();
        else if(chevron.TryGetCurrentPattern(TogglePattern.Pattern,out var toggle))((TogglePattern)toggle).Toggle();
        else throw new InvalidOperationException("이 Windows 버전에서는 트레이 버튼을 열 수 없습니다.");
        return true;
    }

    static bool IsTrayPopup(nint hwnd)
    {
        var name=new StringBuilder(128);BarNative.GetClassName(hwnd,name,name.Capacity);
        return name.ToString() is "TopLevelWindowForOverflowXamlIsland" or "NotifyIconOverflowWindow";
    }

    async Task ReadyAsync(int generation)
    {
        var hwnd=popup;
        var cells=await Task.Run(()=>ReadIconCells(hwnd));
        System.Windows.Media.Imaging.BitmapSource? artwork=null;
        for(int attempt=0;attempt<40;attempt++)
        {
            if(disposed||generation!=openGeneration||!IsOpen)return;
            artwork=NativeTrayFrame.Capture(popup);
            if(artwork!=null&&attempt>0)cells=await Task.Run(()=>ReadIconCells(hwnd));
            if(artwork!=null&&cells.Count>0&&NativeTrayFrame.CountVisibleIcons(artwork,cells)==cells.Count)break;
            artwork=null;
            if(cells.Count==0)cells=await Task.Run(()=>ReadIconCells(hwnd));
            await Task.Delay(50);
        }
        if(artwork==null)throw new InvalidOperationException("트레이 아이콘 화면이 준비되지 않았습니다.");
        RenderedIconCount=NativeTrayFrame.CountVisibleIcons(artwork,cells);
        glass??=new TrayGlassWindow(popup,artwork);
        foregroundHook=BarNative.SetWinEventHook(3,3,0,foregroundCallback,0,0,0);
        var module=BarNative.GetModuleHandle(null);
        mouseHook=BarNative.SetWindowsHookEx(14,mouseCallback,module,0);
        keyboardHook=BarNative.SetWindowsHookEx(13,keyboardCallback,module,0);
        if(foregroundHook==0||mouseHook==0||keyboardHook==0)
            throw new Win32Exception(Marshal.GetLastWin32Error(),"트레이 닫기 처리를 시작하지 못했습니다.");
        stateChanged(true);
    }
    static List<Int32Rect> ReadIconCells(nint hwnd)
    {
        var cells=new List<Int32Rect>();
        if(!BarNative.GetWindowRect(hwnd,out var bounds))return cells;
        var buttons=AutomationElement.FromHandle(hwnd).FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button));
        foreach(AutomationElement button in buttons)
        {
            var r=button.Current.BoundingRectangle;
            if(!r.IsEmpty&&r.Width>0&&r.Height>0)cells.Add(new Int32Rect((int)Math.Round(r.Left-bounds.Left),(int)Math.Round(r.Top-bounds.Top),(int)Math.Round(r.Width),(int)Math.Round(r.Height)));
        }
        return cells;
    }

    static bool IsContextMenu(nint hwnd)
    {
        var info=new BarNative.GuiThreadInfo {Size=(uint)Marshal.SizeOf<BarNative.GuiThreadInfo>()};
        if(BarNative.GetGUIThreadInfo(0,ref info)&&(info.Flags&0x1C)!=0)return true;
        var name=new StringBuilder(128);BarNative.GetClassName(BarNative.GetAncestor(hwnd,2),name,name.Capacity);
        return name.ToString() is "#32768" or "Xaml_WindowedPopupClass" or "Microsoft.UI.Content.PopupWindowSiteBridge";
    }

    void OnForegroundChanged(nint eventHook,uint eventId,nint hwnd,int objectId,int childId,uint thread,uint time)
    {
        var parent=Window.GetWindow(anchor);
        if(parent!=null&&hwnd==new WindowInteropHelper(parent).Handle)return;
        if(glass!=null&&hwnd==new WindowInteropHelper(glass).Handle)return;
        if(!opening&&IsOpen&&hwnd!=popup&&BarNative.GetAncestor(hwnd,2)!=popup&&!IsContextMenu(hwnd))DismissFromOutside();
    }

    static bool Contains(BarNative.Rect bounds,BarNative.Point point)=>point.X>=bounds.Left&&point.X<bounds.Right&&point.Y>=bounds.Top&&point.Y<bounds.Bottom;

    nint OnMouseInput(int code,nint message,nint data)
    {
        if(code>=0&&IsOpen&&message.ToInt64() is 0x201 or 0x204 or 0x207 or 0x20B)
        {
            // MSLLHOOKSTRUCT starts with a physical screen POINT. Never retain input.
            var point=Marshal.PtrToStructure<BarNative.Point>(data);
            if(BarNative.GetWindowRect(popup,out var bounds)&&!Contains(bounds,point)&&!Contains(anchorBounds,point)&&!IsContextMenu(BarNative.WindowFromPoint(point)))DismissFromOutside();
        }
        return BarNative.CallNextHookEx(mouseHook,code,message,data);
    }

    nint OnKeyboardInput(int code,nint message,nint data)
    {
        // Observe only Escape while the flyout is open; forward every other key.
        if(code>=0&&IsOpen&&message.ToInt64() is 0x100 or 0x104&&Marshal.ReadInt32(data)==0x1B)
        {
            DismissFromOutside();return 1;
        }
        return BarNative.CallNextHookEx(keyboardHook,code,message,data);
    }

    void DismissFromOutside()
    {
        if(closingPending||disposed)return;
        closingPending=true;
        anchor.Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(()=>{closingPending=false;if(!disposed)Close(false);}));
    }

    void OnShellEvent(nint eventHook,uint eventId,nint hwnd,int objectId,int childId,uint thread,uint time)
    {
        if(disposed||positioning||objectId!=0||childId!=0||hwnd==0||!IsTrayPopup(hwnd))return;
        try
        {
            if(BarNative.IsWindowVisible(hwnd))AttachAndPlace(hwnd);
            else if(hwnd==popup&&!opening)Restore();
        }
        catch(Exception e){BarSettings.Log(e);Close();}
    }

    void Track()
    {
        if(disposed)return;
        if(popup!=0)
        {
            if(BarNative.IsWindow(popup)&&BarNative.IsWindowVisible(popup))AttachAndPlace(popup);
            else if(seenVisible&&!opening)Restore();
            return;
        }
        foreach(string name in new[]{"TopLevelWindowForOverflowXamlIsland","NotifyIconOverflowWindow"})
        {
            var hwnd=BarNative.FindWindow(name,null);
            if(hwnd!=0&&BarNative.IsWindowVisible(hwnd)){AttachAndPlace(hwnd);break;}
        }
    }

    void AttachAndPlace(nint hwnd)
    {
        if(positioning||disposed||!BarNative.GetWindowRect(hwnd,out var bounds))return;
        int width=bounds.Right-bounds.Left,height=bounds.Bottom-bounds.Top;
        if(width<=0||height<=0)return;
        if(popup==0)
        {
            popup=hwnd;originalBounds=bounds;hasOriginal=true;
            originalTopmost=(BarNative.GetWindowLong(hwnd,-20)&8)!=0;
            if(beforeOpen.TryGetValue(hwnd,out var saved)){originalBounds=saved.Bounds;originalTopmost=saved.Topmost;}
        }
        if(hwnd!=popup)return;
        var top=anchor.PointToScreen(new Point(0,0));
        var bottom=anchor.PointToScreen(new Point(anchor.ActualWidth,anchor.ActualHeight));
        anchorBounds=new BarNative.Rect {Left=(int)top.X,Top=(int)top.Y,Right=(int)bottom.X,Bottom=(int)bottom.Y};
        var screen=Forms.Screen.FromPoint(new System.Drawing.Point((int)bottom.X,(int)bottom.Y)).WorkingArea;
        var position=TrayPopupPlacement.Below(
            anchorBounds,
            new BarNative.Rect {Left=screen.Left,Top=screen.Top,Right=screen.Right,Bottom=screen.Bottom},
            width,height,(int)Math.Round(4*VisualTreeHelper.GetDpi(anchor).DpiScaleY));
        positioning=true;
        try
        {
            if(bounds.Left!=position.X||bounds.Top!=position.Y)
                if(!BarNative.SetWindowPos(hwnd,(nint)(-1),position.X,position.Y,width,height,0x30))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),"트레이 창을 상단 바로 이동하지 못했습니다.");
            seenVisible=true;
            glass?.Sync(new BarNative.Rect {Left=position.X,Top=position.Y,Right=position.X+width,Bottom=position.Y+height});
        }
        finally{positioning=false;}
    }

    public void Close(bool restoreFocus=true)
    {
        openGeneration++;
        if(IsOpen)
        {
            // Let Explorer dismiss its own flyout on loss of focus first.
            if(restoreFocus&&previousForeground!=0&&BarNative.IsWindow(previousForeground))BarNative.SetForegroundWindow(previousForeground);
            if(IsOpen)BarNative.ShowWindow(popup,0);
        }
        Restore();
    }

    void Restore()
    {
        timer.Stop();
        if(hook!=0){BarNative.UnhookWinEvent(hook);hook=0;}
        if(foregroundHook!=0){BarNative.UnhookWinEvent(foregroundHook);foregroundHook=0;}
        if(mouseHook!=0){BarNative.UnhookWindowsHookEx(mouseHook);mouseHook=0;}
        if(keyboardHook!=0){BarNative.UnhookWindowsHookEx(keyboardHook);keyboardHook=0;}
        glass?.Dispose();glass=null;
        taskbarRendering?.Dispose();taskbarRendering=null;
        var hwnd=popup;popup=0;
        if(hasOriginal&&hwnd!=0&&BarNative.IsWindow(hwnd))
            BarNative.SetWindowPos(hwnd,originalTopmost?(nint)(-1):(nint)(-2),originalBounds.Left,originalBounds.Top,originalBounds.Right-originalBounds.Left,originalBounds.Bottom-originalBounds.Top,0x10);
        hasOriginal=false;seenVisible=false;
        RenderedIconCount=0;
        beforeOpen.Clear();
        stateChanged(false);
    }

    public void Dispose(){if(disposed)return;disposed=true;Close();}
}
