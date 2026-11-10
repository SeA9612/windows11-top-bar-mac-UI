using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
namespace GlassDock.TopBar;

// Opens the glass Start screen when the Windows key is pressed and released on
// its own. Shortcuts such as Win+E, Win+D or Win+Shift+S still reach Windows
// unchanged, and Ctrl+Esc keeps opening the original Start menu.
internal sealed class StartKeyHook : IDisposable
{
    // Marks our own injected keys so the hook ignores them. Other injected
    // input (on-screen keyboard, automation) is treated like a real key.
    internal const nuint Marker=0x47445354; // "GDST"
    const int LeftWin=0x5B,RightWin=0x5C,MaskKey=0xE8; // 0xE8 is unassigned
    readonly BarNative.InputHookCallback callback;
    readonly Dispatcher dispatcher;
    readonly Action pressed;
    nint hook;
    bool winDown,combined;

    internal StartKeyHook(Dispatcher dispatcher,Action pressed)
    {
        this.dispatcher=dispatcher;this.pressed=pressed;callback=OnKey;
        hook=BarNative.SetWindowsHookEx(13,callback,BarNative.GetModuleHandle(null),0); // WH_KEYBOARD_LL
        if(hook==0)throw new Win32Exception(Marshal.GetLastWin32Error(),"Windows 키 감시를 시작하지 못했습니다.");
    }

    [StructLayout(LayoutKind.Sequential)] struct KeyboardHook {public int Key,Scan;public uint Flags,Time;public nuint Extra;}

    nint OnKey(int code,nint message,nint data)
    {
        if(code<0)return BarNative.CallNextHookEx(hook,code,message,data);
        var key=Marshal.PtrToStructure<KeyboardHook>(data);
        if(key.Extra==Marker)return BarNative.CallNextHookEx(hook,code,message,data);
        long kind=message.ToInt64();
        bool down=kind is 0x100 or 0x104,up=kind is 0x101 or 0x105;
        bool win=key.Key is LeftWin or RightWin;
        if(win&&down)
        {
            // Auto-repeat sends more key-downs; only the first starts a press.
            if(!winDown){winDown=true;combined=ModifierHeld();}
        }
        else if(win&&up&&winDown)
        {
            winDown=false;
            if(!combined)
            {
                // Windows opens Start when Win is released with nothing in between.
                // Put an unassigned key between down and up, then release Win
                // ourselves, so Start stays closed and the key state stays clean.
                ReleaseMasked(key.Key);
                dispatcher.BeginInvoke(DispatcherPriority.Send,pressed);
                return 1;
            }
        }
        else if(down&&winDown)combined=true;
        return BarNative.CallNextHookEx(hook,code,message,data);
    }

    static bool ModifierHeld()
    {
        foreach(int vk in new[]{0x10,0x11,0x12})if(BarNative.GetAsyncKeyState(vk)<0)return true; // Shift, Ctrl, Alt
        return false;
    }

    static void ReleaseMasked(int win)
    {
        BarNative.Input Key(int vk,uint flags)=>new() {Type=1,Data=new BarNative.InputUnion {Keyboard=new BarNative.KeyboardInput {Key=(ushort)vk,Flags=flags,Extra=Marker}}};
        var inputs=new[]{Key(MaskKey,0),Key(MaskKey,2),Key(win,2|1)}; // KEYUP=2, EXTENDEDKEY=1
        BarNative.SendInput((uint)inputs.Length,inputs,Marshal.SizeOf<BarNative.Input>());
    }

    public void Dispose()
    {
        if(hook!=0){BarNative.UnhookWindowsHookEx(hook);hook=0;}
    }
}
