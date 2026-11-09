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
    TrayGlassWindow? glass;
    public bool IsOpen=>popup!=0&&BarNative.IsWindow(popup)&&BarNative.IsWindowVisible(popup);
    public bool IsInteracting=>opening||IsOpen;

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
        var taskbar=BarNative.FindWindow("Shell_TrayWnd",null);
        if(taskbar==0)throw new InvalidOperationException("Windows 트레이를 찾지 못했습니다. 탐색기가 실행 중인지 확인해 주세요.");
        BarNative.GetWindowThreadProcessId(taskbar,out uint processId);
        previousForeground=BarNative.GetForegroundWindow();
        opening=true;seenVisible=false;
        hook=BarNative.SetWinEventHook(0x8002,0x800B,0,eventCallback,processId,0,0);
        if(hook==0){opening=false;throw new Win32Exception(Marshal.GetLastWin32Error(),"트레이 위치 감시를 시작하지 못했습니다.");}
        timer.Start();
        try
        {
            Track();
            if(IsOpen){Ready();return;}
            // Reuse an already-created native flyout directly. This also works
            // when a dock has hidden the original taskbar and its UIA children.
            foreach(string name in new[]{"TopLevelWindowForOverflowXamlIsland","NotifyIconOverflowWindow"})
            {
                var hwnd=BarNative.FindWindow(name,null);
                if(hwnd==0||!BarNative.GetWindowRect(hwnd,out var bounds)||bounds.Right<=bounds.Left||bounds.Bottom<=bounds.Top)continue;
                AttachAndPlace(hwnd);
                BarNative.ShowWindow(hwnd,4);
                BarNative.SetForegroundWindow(hwnd);
                Track();
                if(IsOpen){Ready();return;}
            }
            // UIA must run away from WPF's dispatcher, which receives shell events.
            // https://learn.microsoft.com/dotnet/framework/ui-automation/ui-automation-threading-issues
            await Task.Run(()=>InvokeChevron(taskbar));
            for(int attempt=0;attempt<60&&!disposed;attempt++)
            {
                Track();
                if(IsOpen){Ready();return;}
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
            if(disposed)Close();
        }
    }

    static void InvokeChevron(nint taskbar)
    {
        // Explorer may not expose its UIA children until a taskbar hidden by a
        // dock has been shown once after sign-in. Initialize without activation,
        // then restore its visibility; the flyout is still placed by our hook.
        bool wasVisible=BarNative.IsWindowVisible(taskbar);
        var hiddenContent=new List<nint>();
        try
        {
            if(TryInvokeChevron(taskbar))return;
            BarNative.EnumChildWindows(taskbar,(hwnd,_)=>{
                var name=new StringBuilder(128);BarNative.GetClassName(hwnd,name,name.Capacity);
                if((BarNative.GetWindowLong(hwnd,-16)&0x10000000)==0&&name.ToString() is "Windows.UI.Composition.DesktopWindowContentBridge" or "Windows.UI.Core.CoreWindow" or "Windows.UI.Input.InputSite.WindowClass")
                    hiddenContent.Add(hwnd);
                return true;
            },0);
            if(!wasVisible)BarNative.ShowWindow(taskbar,4);
            foreach(var hwnd in hiddenContent)BarNative.ShowWindow(hwnd,4);
            for(int attempt=0;attempt<10;attempt++)
            {
                if(TryInvokeChevron(taskbar))return;
                System.Threading.Thread.Sleep(80);
            }
            throw new InvalidOperationException("Windows의 숨겨진 아이콘 버튼을 찾지 못했습니다. 작업 표시줄의 트레이 메뉴가 사용 가능한지 확인해 주세요.");
        }
        finally
        {
            for(int i=hiddenContent.Count-1;i>=0;i--)BarNative.ShowWindow(hiddenContent[i],0);
            if(!wasVisible)BarNative.ShowWindow(taskbar,0);
        }
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
        if(chevron==null)return false;
        if(chevron.TryGetCurrentPattern(InvokePattern.Pattern,out var invoke))((InvokePattern)invoke).Invoke();
        else if(chevron.TryGetCurrentPattern(TogglePattern.Pattern,out var toggle))((TogglePattern)toggle).Toggle();
        else throw new InvalidOperationException("이 Windows 버전에서는 트레이 버튼을 열 수 없습니다.");
        return true;
    }

    static bool IsTrayPopup(nint hwnd)
    {
        var name=new StringBuilder(128);BarNative.GetClassName(hwnd,name,name.Capacity);
        return name.ToString() is "TopLevelWindowForOverflowXamlIsland" or "NotifyIconOverflowWindow";
    }

    void Ready()
    {
        glass??=new TrayGlassWindow(popup);
        foregroundHook=BarNative.SetWinEventHook(3,3,0,foregroundCallback,0,0,0);
        var module=BarNative.GetModuleHandle(null);
        mouseHook=BarNative.SetWindowsHookEx(14,mouseCallback,module,0);
        keyboardHook=BarNative.SetWindowsHookEx(13,keyboardCallback,module,0);
        if(foregroundHook==0||mouseHook==0||keyboardHook==0)
            throw new Win32Exception(Marshal.GetLastWin32Error(),"트레이 닫기 처리를 시작하지 못했습니다.");
        stateChanged(true);
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
                if(!BarNative.SetWindowPos(hwnd,(nint)(-1),position.X,position.Y,0,0,0x11))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),"트레이 창을 상단 바로 이동하지 못했습니다.");
            seenVisible=true;
            glass?.Sync(new BarNative.Rect {Left=position.X,Top=position.Y,Right=position.X+width,Bottom=position.Y+height});
        }
        finally{positioning=false;}
    }

    public void Close(bool restoreFocus=true)
    {
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
        var hwnd=popup;popup=0;
        if(hasOriginal&&hwnd!=0&&BarNative.IsWindow(hwnd))
            BarNative.SetWindowPos(hwnd,originalTopmost?(nint)(-1):(nint)(-2),originalBounds.Left,originalBounds.Top,originalBounds.Right-originalBounds.Left,originalBounds.Bottom-originalBounds.Top,0x10);
        hasOriginal=false;seenVisible=false;
        stateChanged(false);
    }

    public void Dispose(){if(disposed)return;disposed=true;Close();}
}
