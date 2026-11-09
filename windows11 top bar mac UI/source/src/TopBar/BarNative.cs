using System;
using System.Runtime.InteropServices;
using System.Text;
namespace GlassDock.TopBar;
internal static class BarNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] internal struct AppBarData {public uint Size;public nint Hwnd;public uint Callback;public uint Edge;public Rect Bounds;public nint Param;}
    [StructLayout(LayoutKind.Sequential)] internal struct Point {public int X,Y;}
    [StructLayout(LayoutKind.Sequential)] internal struct Margins {public int Left,Right,Top,Bottom;}
    [DllImport("shell32.dll",CallingConvention=CallingConvention.StdCall)] internal static extern nuint SHAppBarMessage(uint message,ref AppBarData data);
    [DllImport("user32.dll",SetLastError=true)] internal static extern bool SetWindowPos(nint hwnd,nint after,int x,int y,int w,int h,uint flags);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern nint FindWindow(string className,string? title);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hwnd);
    internal delegate void WinEventCallback(nint hook,uint eventId,nint hwnd,int objectId,int childId,uint thread,uint time);
    [DllImport("user32.dll",SetLastError=true)] internal static extern nint SetWinEventHook(uint eventMin,uint eventMax,nint module,WinEventCallback callback,uint processId,uint threadId,uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(nint hook);
    internal delegate nint InputHookCallback(int code,nint message,nint data);
    [DllImport("user32.dll",SetLastError=true)] internal static extern nint SetWindowsHookEx(int hook,InputHookCallback callback,nint module,uint threadId);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook,int code,nint message,nint data);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd,uint flags);
    [StructLayout(LayoutKind.Sequential)] internal struct GuiThreadInfo {public uint Size,Flags;public nint Active,Focus,Capture,MenuOwner,MoveSize,Caret;public Rect CaretRect;}
    [DllImport("user32.dll")] internal static extern bool GetGUIThreadInfo(uint threadId,ref GuiThreadInfo info);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetShellWindow();
    [DllImport("user32.dll")] internal static extern nint GetDesktopWindow();
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")] internal static extern int GetWindowLong(nint hwnd,int index);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern int GetClassName(nint hwnd,StringBuilder text,int max);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern int GetWindowText(nint hwnd,StringBuilder text,int max);
    [DllImport("user32.dll")] internal static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll",SetLastError=true)] internal static extern bool LockWorkStation();
    internal delegate bool EnumWindowCallback(nint hwnd,nint param);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowCallback callback,nint param);
    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(nint parent,EnumWindowCallback callback,nint param);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd,out Rect bounds);
    [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint hwnd,uint flags);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint hwnd,int attribute,out int value,int size);
    [DllImport("user32.dll")] internal static extern bool ShowWindowAsync(nint hwnd,int command);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint hwnd,int command);
    [DllImport("user32.dll")] internal static extern bool PostMessage(nint hwnd,uint message,nint w,nint l);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll",SetLastError=true)] internal static extern uint SendInput(uint count,Input[] inputs,int size);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] internal static extern nint MonitorFromPoint(Point point,uint flags);
    [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(nint monitor,int type,out uint x,out uint y);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(nint hwnd,int attribute,ref int value,int size);
    [DllImport("dwmapi.dll")] internal static extern int DwmExtendFrameIntoClientArea(nint hwnd,ref Margins margins);
    [StructLayout(LayoutKind.Sequential)] internal struct Input {public uint Type;public InputUnion Data;}
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion {[FieldOffset(0)]public KeyboardInput Keyboard;[FieldOffset(0)]public MouseInput Mouse;}
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput {public ushort Key,Scan;public uint Flags,Time;public nuint Extra;}
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput {public int X,Y;public uint MouseData,Flags,Time;public nuint Extra;}
    internal static void Shortcut(ushort modifier,ushort key)
    {
        foreach(int k in new[]{0x10,0x11,0x12,0x5B,0x5C})if(GetAsyncKeyState(k)<0)throw new InvalidOperationException("누르고 있는 Shift/Ctrl/Alt/Windows 키를 놓고 다시 눌러주세요.");
        Input Key(ushort k,bool up)=>new(){Type=1,Data=new InputUnion {Keyboard=new KeyboardInput {Key=k,Flags=up?2u:0u}}};
        var input=new[]{Key(modifier,false),Key(key,false),Key(key,true),Key(modifier,true)};
        if(SendInput((uint)input.Length,input,Marshal.SizeOf<Input>())!=(uint)input.Length)
        {
            // Release only keys this action tried to synthesize; do not leave Ctrl/Win held.
            var release=new[]{Key(key,true),Key(modifier,true)};SendInput(2,release,Marshal.SizeOf<Input>());
            throw new InvalidOperationException("Windows가 입력 요청을 허용하지 않았습니다. 기본 시작 메뉴나 단축키를 사용해 주세요.");
        }
    }
}
