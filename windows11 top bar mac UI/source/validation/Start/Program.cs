using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using GlassDock.TopBar;
using Forms=System.Windows.Forms;

// Live integration test for the glass Start screen and the Windows key hook.
// Run with the real top bar closed. Presses the Windows key, the unassigned
// F24 key, Ctrl+Esc and Esc; launches Calculator once and closes it again.
//
// Windows' own Start menu cannot be seen through window state on Windows 11
// (it is not in EnumWindows and its CoreWindow looks the same open or closed),
// so the detector compares screen pixels: an opaque magenta canvas sits in a
// band of the Start menu's area that the glass Start screen does not cover.
// The Start menu draws above topmost windows, so while it is open the band no
// longer shows the canvas colour.
class Program
{
    static int count;
    static void Check(bool condition,string text){if(!condition){DumpDiagnostics(text);throw new Exception("FAIL "+text);}count++;Console.WriteLine("PASS "+text);}

    // ---- Diagnostics (team member 3): event log printed when a check fails ----
    static readonly Stopwatch clock=Stopwatch.StartNew();
    static readonly System.Collections.Generic.List<string> diag=new();
    static Func<string>? stateDump;
    static BarNative.WinEventCallback? foregroundCallback;
    static nint foregroundHook;
    static void Diag(string text){lock(diag)diag.Add($"{clock.ElapsedMilliseconds,7}ms {text}");}
    static string Describe(nint h)
    {
        if(h==0)return "(none)";
        var title=new StringBuilder(256);BarNative.GetWindowText(h,title,title.Capacity);
        var cls=new StringBuilder(256);BarNative.GetClassName(h,cls,cls.Capacity);
        BarNative.GetWindowThreadProcessId(h,out uint pid);
        string proc;try{proc=Process.GetProcessById((int)pid).ProcessName;}catch{proc="?";}
        string self=pid==(uint)Environment.ProcessId?" [self]":"";
        return $"0x{h:X} {proc}({pid}){self} class='{cls}' title='{title}'";
    }
    static string Fg()=>Describe(BarNative.GetForegroundWindow());
    static void StartForegroundLog()
    {
        foregroundCallback=(_,ev,h,obj,child,_,_)=>{if(obj==0)Diag($"EVENT_SYSTEM_FOREGROUND -> {Describe(h)}");};
        foregroundHook=BarNative.SetWinEventHook(3,3,0,foregroundCallback,0,0,0); // EVENT_SYSTEM_FOREGROUND, WINEVENT_OUTOFCONTEXT
        Diag($"foreground log started (hook 0x{foregroundHook:X}), foreground now {Fg()}");
    }
    static void DumpDiagnostics(string failed)
    {
        Console.WriteLine("DIAG ===== failure: "+failed);
        try{Console.WriteLine("DIAG state: "+(stateDump?.Invoke()??"(no menu yet)"));}catch(Exception e){Console.WriteLine("DIAG state error "+e.Message);}
        Console.WriteLine("DIAG foreground now: "+Fg());
        lock(diag)foreach(var line in diag)Console.WriteLine("DIAG "+line);
        Console.WriteLine("DIAG ===== end");
    }

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(nint hwnd,uint key,byte alpha,uint flags);

    [STAThread] static void Main()
    {
        SetProcessDPIAware();
        // One keyboard and screen: key-pressing test runs must not overlap.
        using var inputLock=new System.Threading.Mutex(false,@"Global\GlassDock.InputTests");
        bool locked;
        try{locked=inputLock.WaitOne(TimeSpan.FromMinutes(2));}
        catch(System.Threading.AbandonedMutexException){locked=true;}
        if(!locked){Console.Error.WriteLine("FAIL 다른 입력 검사 실행 중: another input test is running (Global\\GlassDock.InputTests held for 2 minutes)");Environment.ExitCode=1;return;}
        try{RunTests();}
        finally{inputLock.ReleaseMutex();}
    }

    static void RunTests()
    {
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Startup+=async(_,_)=>
        {
            StartMenuWindow? menu=null;StartKeyHook? hook=null;Window? other=null;
            try
            {
                // 0. Control: prove the pixel detector sees the real Start menu
                //    open and closed before trusting any "stays closed" check.
                StartForegroundLog();
                canvas=new Canvas(Band());canvas.Show();await Task.Delay(600);
                int stray=StartDiff();
                Check(BarNative.IsWindowVisible(canvas.Handle)&&stray==0,$"detector canvas is drawn at {Fmt(canvas.Bounds)} ({Samples-stray}/{Samples} samples match #FF00FF)");
                Check(!WindowsStartOpen(),"Windows Start menu is closed before the test");
                Keys(Down(0x11),Down(0x1B),Up(0x1B),Up(0x11));
                Check(await WaitFor(true,3000),$"control: Ctrl+Esc opens the Windows Start menu and the detector sees it ({StartDiff()}/{Samples} samples differ)");
                var how=await CloseWindowsStart();
                Check(how!=null,"control: the Windows Start menu closes again and the detector sees it");
                Console.WriteLine($"INFO the Windows Start menu was closed by: {how}");

                menu=new StartMenuWindow();
                {
                    var m=menu;
                    stateDump=()=>$"IsOpen={m.IsOpen} IsVisible={m.IsVisible} IsActive={m.IsActive} OpenCategory={m.OpenCategory??"null"} SearchText='{m.SearchText}' Opacity={m.Opacity:0.00} hwnd=0x{new WindowInteropHelper(m).Handle:X}";
                    m.Activated+=(_,_)=>Diag($"menu Activated; foreground {Fg()}");
                    m.Deactivated+=(_,_)=>Diag($"menu Deactivated; foreground {Fg()}; state {stateDump()}");
                    m.IsVisibleChanged+=(_,e)=>Diag($"menu IsVisible -> {e.NewValue}; foreground {Fg()}");
                }
                await menu.Loading.WaitAsync(TimeSpan.FromSeconds(20));
                Check(menu.Apps.Count>20,$"Windows app list loaded ({menu.Apps.Count} apps)");

                // 1. Presentation
                await menu.PresentAsync();await Task.Delay(400);
                var hwnd=new WindowInteropHelper(menu).Handle;
                Check(menu.IsOpen&&Math.Abs(menu.Opacity-1)<.01,"glass Start screen is shown and fully faded in");
                BarNative.GetWindowRect(hwnd,out var r);
                var screen=Forms.Screen.FromPoint(Forms.Cursor.Position).Bounds;
                int cx=(r.Left+r.Right)/2,cy=(r.Top+r.Bottom)/2;
                Check(Math.Abs(cx-(screen.Left+screen.Width/2))<=2&&Math.Abs(cy-(screen.Top+screen.Height/2))<=2,$"centered on the monitor ({r.Left},{r.Top},{r.Right},{r.Bottom})");
                double dpiScale=System.Windows.Media.VisualTreeHelper.GetDpi(menu).DpiScaleX;
                int groupCount=StartCatalog.Group(menu.Apps).Count;
                double fitted=menu.HeightFor(groupCount);
                int expectH=(int)Math.Round(fitted*dpiScale),expectW=(int)Math.Round(StartMenuWindow.PanelWidth*dpiScale);
                Check(Math.Abs(menu.PanelHeight-fitted)<.01&&Math.Abs(r.Right-r.Left-expectW)<=1&&Math.Abs(r.Bottom-r.Top-expectH)<=1&&fitted<=StartMenuWindow.MaxPanelHeight,
                    $"panel is {r.Right-r.Left}x{r.Bottom-r.Top}px, fitted to {(int)Math.Ceiling(groupCount/4.0)} category rows (expected {expectW}x{expectH}px)");
                Check(fitted>=StartMenuWindow.MaxPanelHeight||menu.ScrollableHeight<1,$"all category rows fit without scrolling (scrollable {menu.ScrollableHeight:0.#} DIP)");
                var band=canvas.Bounds;
                Check(r.Bottom<=band.Top||r.Top>=band.Bottom||r.Right<=band.Left||r.Left>=band.Right,$"glass Start screen does not overlap the detector band {Fmt(band)}");
                Check(!WindowsStartOpen(),$"detector reads closed while the glass Start screen is open ({StartDiff()}/{Samples} samples differ)");
                var glass=MaterialWindow();
                Check(glass!=0&&BarNative.GetWindowRect(glass,out var g)&&g.Left<=r.Left&&g.Top<=r.Top&&g.Right>=r.Right&&g.Bottom>=r.Bottom,"Dock glass material sits behind the whole panel");
                int groups=StartCatalog.Group(menu.Apps).Count;
                Check(menu.ShownCount==groups&&groups>=4,$"category grid shows {groups} non-empty categories");
                await Task.Delay(1500);
                Check(IconsLoaded(menu)>=groups*2,$"Shell app icons are drawn in the tiles ({IconsLoaded(menu)} images)");

                // 2. Search, categories, keyboard
                menu.SearchText="메모";await Task.Delay(100);
                Check(menu.IsOpen&&menu.Results.Count>0&&menu.Results[0].Name=="메모장",$"search ranks 메모장 first ({string.Join(", ",menu.Results.Take(4).Select(a=>a.Name))})");
                menu.SearchText="zzzz-no-app";await Task.Delay(100);
                Check(menu.IsOpen&&menu.Results.Count==0&&menu.ShownCount==0,"a search without matches shows no apps");
                Press(menu,Key.Escape);
                Check(menu.IsOpen&&menu.SearchText.Length==0&&menu.ShownCount==groups,"Esc clears the search and returns to the categories");
                var productivity=StartCatalog.Group(menu.Apps).First();
                Diag("step: Open category");
                menu.Open(productivity.Category);await Task.Delay(100);
                Check(menu.IsOpen&&menu.OpenCategory==productivity.Category&&menu.ShownCount==productivity.Apps.Count,$"opening {productivity.Category} lists all {productivity.Apps.Count} apps");
                Diag($"step: Esc in category; before {stateDump!()}");
                Press(menu,Key.Escape);
                Diag($"step: after Esc in category; {stateDump!()}");
                Check(menu.OpenCategory==null&&menu.IsOpen,"Esc goes back from a category to the grid");
                Press(menu,Key.Escape);await Task.Delay(150);
                Check(!menu.IsOpen&&!BarNative.IsWindowVisible(glass),"Esc on the grid closes the screen and its glass");

                // 3. Clicking another window closes it
                await menu.PresentAsync();await Task.Delay(300);
                other=new Window {Title="GlassDock Start test · other window",Width=200,Height=80,Left=40,Top=200};
                other.Show();other.Activate();await Task.Delay(400);
                Check(!menu.IsOpen,"activating another window dismisses the Start screen");
                other.Close();other=null;

                // 3a. Another window taking focus while the screen is still opening
                //     (Deactivated is ignored then) must not leave it open unfocused.
                Diag("step: 3a focus stolen during PresentAsync");
                var opening=menu.PresentAsync();
                other=new Window {Title="GlassDock Start test · focus thief",Width=200,Height=80,Left=40,Top=200,ShowInTaskbar=false};
                other.Show();other.Activate();BarNative.SetForegroundWindow(new WindowInteropHelper(other).Handle);
                await opening;await Task.Delay(300);
                Check(!menu.IsOpen&&!BarNative.IsWindowVisible(hwnd),$"focus taken while opening closes the Start screen instead of leaving it unfocused on top (foreground {Fg()})");
                other.Close();other=null;await Task.Delay(150);

                // 3b. The text colour follows the glass while the screen is open.
                //     A plain full-screen backdrop window (not topmost: below the
                //     screen, its glass and the detector canvas) turns white/black.
                var cursorScreen=Forms.Screen.FromPoint(Forms.Cursor.Position).Bounds;
                backdrop=new Backdrop(new System.Drawing.Rectangle(cursorScreen.Left,cursorScreen.Top,cursorScreen.Width,cursorScreen.Height-1));
                backdrop.Show();backdrop.Raise();await Task.Delay(500);
                await menu.PresentAsync();await Task.Delay(500);
                Check(menu.IsOpen&&menu.BrightBackdrop&&IsDarkText(menu),$"over a white backdrop the Start screen opens with dark text (glass level {GlassLevel(menu):0})");
                backdrop.BackColor=System.Drawing.Color.Black;backdrop.Raise();
                bool darkened=await WaitUntil(()=>!menu.BrightBackdrop,1500);
                Check(menu.IsOpen&&darkened&&IsWhiteText(menu),$"the backdrop turning black switches the text to white within 1.5 s (glass level {GlassLevel(menu):0})");
                backdrop.BackColor=System.Drawing.Color.White;backdrop.Raise();
                bool brightened=await WaitUntil(()=>menu.BrightBackdrop,1500);
                Check(menu.IsOpen&&brightened&&IsDarkText(menu),$"the backdrop turning white again switches the text back to dark (glass level {GlassLevel(menu):0})");
                Check(!WindowsStartOpen(),"the backdrop window stays below the detector canvas");
                Diag("step: 3b menu.Dismiss()");
                menu.Dismiss();await Task.Delay(150);
                Diag("step: 3b backdrop.Close()");
                backdrop.Close();backdrop.Dispose();backdrop=null;await Task.Delay(200);

                // 4. Windows key
                int presses=0;
                var menuRef=menu;
                stateDump=()=>$"presses={presses} IsOpen={menuRef.IsOpen} IsVisible={menuRef.IsVisible} IsActive={menuRef.IsActive} OpenCategory={menuRef.OpenCategory??"null"} SearchText='{menuRef.SearchText}' Opacity={menuRef.Opacity:0.00} WinKeyDown={BarNative.GetAsyncKeyState(0x5B)<0}";
                hook=new StartKeyHook(menu.Dispatcher,()=>{presses++;Diag($"hook callback #{presses}; before Toggle IsOpen={menuRef.IsOpen} IsVisible={menuRef.IsVisible}; foreground {Fg()}");menu.Toggle();Diag($"hook callback #{presses}; after Toggle IsOpen={menuRef.IsOpen}");});
                Diag($"step: sending Win; foreground {Fg()}");
                Keys(Down(0x5B),Up(0x5B));await Task.Delay(900);
                Check(presses==1&&menu.IsOpen,"pressing Win alone opens the glass Start screen");
                Check(!WindowsStartOpen(),"the Windows Start menu stays closed");
                Check(BarNative.GetForegroundWindow()==hwnd&&menu.SearchFocused,"the screen takes focus with the cursor in the search box");
                Keys(Down(0x5B),Up(0x5B));await Task.Delay(500);
                Check(presses==2&&!menu.IsOpen&&!WindowsStartOpen(),"pressing Win again closes it; Windows Start still closed");
                Keys(Down(0x5B),Down(0x87),Up(0x87),Up(0x5B));await Task.Delay(600);
                Check(presses==2&&!menu.IsOpen&&!WindowsStartOpen(),"Win + another key is left to Windows and opens nothing");
                Check(BarNative.GetAsyncKeyState(0x5B)>=0,"no Windows key is left held down");
                Keys(Down(0x11),Down(0x1B),Up(0x1B),Up(0x11));
                bool hookStartOpen=await WaitFor(true,3000);
                Check(hookStartOpen&&presses==2&&!menu.IsOpen,$"Ctrl+Esc is not taken by the hook and opens the Windows Start menu (presses={presses}, {StartDiff()}/{Samples} samples differ)");
                var hookHow=await CloseWindowsStart();
                Check(hookHow!=null&&!WindowsStartOpen(),"the Windows Start menu closes again with the hook installed");
                Console.WriteLine($"INFO with the hook installed the Windows Start menu was closed by: {hookHow}");

                // 5. Launch
                var calculator=menu.Apps.FirstOrDefault(a=>a.Id.StartsWith("Microsoft.WindowsCalculator",StringComparison.OrdinalIgnoreCase));
                if(calculator!=null)
                {
                    var before=Process.GetProcessesByName("CalculatorApp").Select(p=>p.Id).ToHashSet();
                    StartApps.Launch(calculator);
                    Process? started=null;
                    for(int i=0;i<40&&started==null;i++){await Task.Delay(250);started=Process.GetProcessesByName("CalculatorApp").FirstOrDefault(p=>!before.Contains(p.Id));}
                    Check(started!=null,"launching an app from the catalog starts it");
                    if(started!=null){started.CloseMainWindow();if(!started.WaitForExit(3000))started.Kill();}
                }
                else Console.WriteLine("SKIP launch check: Calculator is not installed");

                Console.WriteLine($"PASS {count} glass Start checks");
                if(Environment.GetEnvironmentVariable("GLASSDOCK_START_DIAG")=="1")DumpDiagnostics("(none: passing run, GLASSDOCK_START_DIAG=1)");
            }
            catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;}
            finally
            {
                if(foregroundHook!=0){BarNative.UnhookWinEvent(foregroundHook);foregroundHook=0;}
                hook?.Dispose();other?.Close();menu?.Close();backdrop?.Close();backdrop?.Dispose();backdrop=null;
                try
                {
                    if(canvas!=null&&WindowsStartOpen())
                    {
                        var how=await CloseWindowsStart();
                        Console.WriteLine(how!=null?$"CLEANUP the Windows Start menu was closed by: {how}":"CLEANUP could not close the Windows Start menu; close it by hand");
                    }
                }
                catch(Exception e){Console.Error.WriteLine("CLEANUP "+e.Message);}
                canvas?.Close();canvas?.Dispose();
                app.Shutdown(Environment.ExitCode);
            }
        };
        app.Run();
    }

    static void Press(Window window,Key key)
    {
        var source=PresentationSource.FromVisual(window)!;
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,source,0,key) {RoutedEvent=Keyboard.PreviewKeyDownEvent});
    }

    static BarNative.Input Down(int vk)=>new() {Type=1,Data=new BarNative.InputUnion {Keyboard=new BarNative.KeyboardInput {Key=(ushort)vk,Flags=vk is 0x5B?1u:0u}}};
    static BarNative.Input Up(int vk)=>new() {Type=1,Data=new BarNative.InputUnion {Keyboard=new BarNative.KeyboardInput {Key=(ushort)vk,Flags=vk is 0x5B?3u:2u}}};
    static void Keys(params BarNative.Input[] inputs)=>BarNative.SendInput((uint)inputs.Length,inputs,Marshal.SizeOf<BarNative.Input>());

    static async Task<bool> WaitUntil(Func<bool> condition,int milliseconds)
    {
        var watch=Stopwatch.StartNew();
        while(!condition()){if(watch.ElapsedMilliseconds>=milliseconds)return false;await Task.Delay(50);}
        return true;
    }

    static System.Windows.Media.Color? TextColor(StartMenuWindow menu)=>(menu.Foreground as System.Windows.Media.SolidColorBrush)?.Color;
    static bool IsDarkText(StartMenuWindow menu)=>TextColor(menu) is {} c&&c.R+c.G+c.B<200;
    static bool IsWhiteText(StartMenuWindow menu)=>TextColor(menu) is {} c&&c.R==255&&c.G==255&&c.B==255;
    static double GlassLevel(StartMenuWindow menu)
    {
        BarNative.GetWindowRect(new WindowInteropHelper(menu).Handle,out var r);
        return GlassPopup.MedianLevel(StartMenuWindow.MarginSamples(r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top));
    }

    static int IconsLoaded(DependencyObject root)
    {
        int n=0;
        if(root is System.Windows.Controls.Image image&&image.Source is System.Windows.Media.Imaging.BitmapSource)n++;
        for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++)n+=IconsLoaded(System.Windows.Media.VisualTreeHelper.GetChild(root,i));
        return n;
    }

    static nint MaterialWindow()
    {
        nint found=0;uint self=(uint)Environment.ProcessId;
        BarNative.EnumWindows((h,_)=>{
            var name=new StringBuilder(64);BarNative.GetClassName(h,name,name.Capacity);
            BarNative.GetWindowThreadProcessId(h,out uint pid);
            if(pid==self&&name.ToString()=="GlassDock.TopBar.NativeGlass"&&BarNative.IsWindowVisible(h)){found=h;return false;}
            return true;},0);
        return found;
    }

    // ---- Pixel detector for Windows' own Start menu ----------------------------
    static Canvas? canvas;
    const int Columns=5,Rows=3,Samples=Columns*Rows,Tolerance=60;
    static readonly System.Drawing.Color CanvasColor=System.Drawing.Color.FromArgb(255,0,255);

    // Physical-pixel band inside the Start menu's area (531,158-1389,1048 on
    // 1920x1080) and below the 820x580 glass Start screen (550,250-1370,830):
    // x 560-1360, y 860-1040 on 1920x1080, scaled to the primary monitor.
    static System.Drawing.Rectangle Band()
    {
        var s=Forms.Screen.PrimaryScreen!.Bounds;
        int X(double f)=>s.Left+(int)Math.Round(s.Width*f);
        int Y(double f)=>s.Top+(int)Math.Round(s.Height*f);
        int x0=X(560/1920.0),x1=X(1360/1920.0),y0=Y(860/1080.0),y1=Y(1040/1080.0);
        return new System.Drawing.Rectangle(x0,y0,x1-x0,y1-y0);
    }

    static string Fmt(System.Drawing.Rectangle b)=>$"{b.Left},{b.Top}-{b.Right},{b.Bottom}";

    // Number of grid samples in the band whose colour is far from the canvas.
    static int StartDiff()
    {
        if(canvas==null)throw new InvalidOperationException("detector canvas is not shown");
        // keep the canvas above other topmost windows without activating it
        BarNative.SetWindowPos(canvas.Handle,-1,0,0,0,0,0x0001|0x0002|0x0010);
        var b=canvas.Bounds;
        using var bitmap=new System.Drawing.Bitmap(b.Width,b.Height,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using(var g=System.Drawing.Graphics.FromImage(bitmap))
            g.CopyFromScreen(b.Left,b.Top,0,0,b.Size,System.Drawing.CopyPixelOperation.SourceCopy);
        int differ=0;
        for(int row=0;row<Rows;row++)
        for(int col=0;col<Columns;col++)
        {
            var c=bitmap.GetPixel((int)(b.Width*(col+.5)/Columns),(int)(b.Height*(row+.5)/Rows));
            if(Math.Abs(c.R-CanvasColor.R)+Math.Abs(c.G-CanvasColor.G)+Math.Abs(c.B-CanvasColor.B)>Tolerance)differ++;
        }
        return differ;
    }

    static bool WindowsStartOpen()=>StartDiff()*2>=Samples;

    static async Task<bool> WaitFor(bool open,int milliseconds)
    {
        var watch=Stopwatch.StartNew();
        while(true)
        {
            if(WindowsStartOpen()==open)return true;
            if(watch.ElapsedMilliseconds>=milliseconds)return false;
            await Task.Delay(100);
        }
    }

    // Closes Windows' own Start menu; returns the method that worked or null.
    static async Task<string?> CloseWindowsStart()
    {
        if(!WindowsStartOpen())return "already closed";
        Keys(Down(0x1B),Up(0x1B));
        if(await WaitFor(false,2000))return "Esc";
        Keys(Down(0x1B),Up(0x1B));
        if(await WaitFor(false,1500))return "Esc (second press)";
        Keys(Down(0x11),Down(0x1B),Up(0x1B),Up(0x11));
        if(await WaitFor(false,2000))return "Ctrl+Esc toggle";
        var focus=new Window {Title="GlassDock Start test · focus window",Width=200,Height=80,Left=40,Top=320,ShowInTaskbar=false};
        try
        {
            focus.Show();focus.Activate();
            BarNative.SetForegroundWindow(new WindowInteropHelper(focus).Handle);
            if(await WaitFor(false,2000))return "activating a test window";
        }
        finally{focus.Close();}
        return null;
    }

    static Backdrop? backdrop;
    // Plain white (later black) screen-sized window, not topmost, never activated.
    sealed class Backdrop:Forms.Form
    {
        public Backdrop(System.Drawing.Rectangle bounds)
        {
            FormBorderStyle=Forms.FormBorderStyle.None;ShowInTaskbar=false;StartPosition=Forms.FormStartPosition.Manual;
            Bounds=bounds;BackColor=System.Drawing.Color.White;Text="GlassDock Start test · backdrop";
        }
        // Top of the non-topmost windows, without activating it.
        public void Raise()=>BarNative.SetWindowPos(Handle,0,0,0,0,0,0x0001|0x0002|0x0010);
        protected override bool ShowWithoutActivation=>true;
        protected override Forms.CreateParams CreateParams
        {
            get
            {
                var p=base.CreateParams;
                p.ExStyle|=0x20|0x80|0x08000000; // WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                return p;
            }
        }
        protected override void WndProc(ref Forms.Message m)
        {
            if(m.Msg==0x21){m.Result=3;return;} // WM_MOUSEACTIVATE -> MA_NOACTIVATE
            base.WndProc(ref m);
        }
    }

    // Opaque magenta, topmost, click-through, never activated, not in the taskbar.
    sealed class Canvas:Forms.Form
    {
        public Canvas(System.Drawing.Rectangle bounds)
        {
            FormBorderStyle=Forms.FormBorderStyle.None;ShowInTaskbar=false;StartPosition=Forms.FormStartPosition.Manual;
            Bounds=bounds;BackColor=CanvasColor;Text="GlassDock Start test · detector canvas";
        }
        protected override bool ShowWithoutActivation=>true;
        protected override Forms.CreateParams CreateParams
        {
            get
            {
                var p=base.CreateParams;
                // WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE
                p.ExStyle|=0x8|0x20|0x80|0x80000|0x08000000;
                return p;
            }
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetLayeredWindowAttributes(Handle,0,255,2); // LWA_ALPHA 255: fully opaque
        }
        protected override void WndProc(ref Forms.Message m)
        {
            if(m.Msg==0x21){m.Result=3;return;} // WM_MOUSEACTIVATE -> MA_NOACTIVATE
            base.WndProc(ref m);
        }
    }
}
