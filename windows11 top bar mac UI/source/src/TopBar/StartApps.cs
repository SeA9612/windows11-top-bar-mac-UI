using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace GlassDock.TopBar;

// Reads Windows' own app list (shell:AppsFolder, the same source as Start's
// "All apps") and Shell application icons on one STA worker thread. Icons are
// requested as ICONONLY like the Dock: no thumbnails, documents or screenshots.
internal sealed class StartApps : IDisposable
{
    internal const string AppsFolder="shell:AppsFolder\\";
    readonly BlockingCollection<Action> work=new();
    readonly ConcurrentDictionary<string,Task<ImageSource?>> icons=new(StringComparer.OrdinalIgnoreCase);
    readonly Thread worker;
    bool disposed;

    internal StartApps()
    {
        worker=new Thread(()=>{foreach(var action in work.GetConsumingEnumerable())try{action();}catch(Exception e){BarSettings.Log(e);}})
            {IsBackground=true,Name="GlassDock Start apps"};
        worker.SetApartmentState(ApartmentState.STA);worker.Start();
    }

    Task<T> Run<T>(Func<T> function)
    {
        var result=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try{work.Add(()=>{try{result.SetResult(function());}catch(Exception e){result.SetException(e);}});}
        catch(InvalidOperationException){result.TrySetException(new ObjectDisposedException(nameof(StartApps)));}
        return result.Task;
    }

    internal Task<List<StartApp>> LoadAsync()=>Run(()=>
    {
        var list=new List<StartApp>();
        var type=Type.GetTypeFromProgID("Shell.Application")??throw new InvalidOperationException("Windows Shell을 사용할 수 없습니다.");
        dynamic shell=Activator.CreateInstance(type)!;
        try
        {
            dynamic folder=shell.NameSpace("shell:AppsFolder");
            foreach(dynamic item in folder.Items())
            {
                string name=(string)item.Name,id=(string)item.Path;
                if(StartCatalog.IsLaunchable(name,id))list.Add(StartCatalog.Create(name,id));
            }
        }
        finally{Marshal.FinalReleaseComObject(shell);}
        return list;
    });

    internal Task<ImageSource?> IconAsync(string id,int pixels)
    {
        pixels=Math.Clamp(pixels,16,256);
        return icons.GetOrAdd(id+"|"+pixels,_=>Run<ImageSource?>(()=>LoadIcon(id,pixels)));
    }

    static ImageSource? LoadIcon(string id,int pixels)
    {
        object? item=null;nint bitmap=0;
        try
        {
            Guid iid=typeof(IShellItemImageFactory).GUID;
            if(SHCreateItemFromParsingName(AppsFolder+id,0,ref iid,out item)!=0||item==null)return null;
            if(((IShellItemImageFactory)item).GetImage(new Size(pixels,pixels),0x4|0x1,out bitmap)!=0||bitmap==0)return null;
            return ReadBitmap(bitmap);
        }
        catch(Exception e) when(e is COMException or InvalidCastException or InvalidOperationException){return null;}
        finally{if(bitmap!=0)DeleteObject(bitmap);if(item!=null&&Marshal.IsComObject(item))Marshal.FinalReleaseComObject(item);}
    }

    // Opens the app the same way Start does; Explorer starts it, so it is not
    // a child of the top bar and keeps running when the top bar exits.
    internal static void Launch(StartApp app)=>Process.Start(new ProcessStartInfo("explorer.exe",AppsFolder+app.Id) {UseShellExecute=true});

    static BitmapSource ReadBitmap(nint handle)
    {
        if(GetObject(handle,Marshal.SizeOf<Bitmap>(),out var bitmap)==0)throw new InvalidOperationException("Icon bitmap metadata unavailable");
        int width=bitmap.Width,height=Math.Abs(bitmap.Height),stride=checked(width*4);
        var info=new BitmapInfo {Header=new BitmapHeader {Size=40,Width=width,Height=-height,Planes=1,BitCount=32,ImageSize=(uint)checked(stride*height)}};
        var bytes=new byte[checked(stride*height)];nint dc=GetDC(0);
        try{if(GetDIBits(dc,handle,0,(uint)height,bytes,ref info,0)==0)throw new InvalidOperationException("Icon pixels unavailable");}
        finally{ReleaseDC(0,dc);}
        // Legacy Shell bitmaps leave every alpha byte at zero: treat those as opaque.
        bool anyAlpha=false;for(int i=3;i<bytes.Length;i+=4)if(bytes[i]!=0){anyAlpha=true;break;}
        if(!anyAlpha)for(int i=3;i<bytes.Length;i+=4)bytes[i]=255;
        var image=BitmapSource.Create(width,height,96,96,PixelFormats.Pbgra32,null,bytes,stride);image.Freeze();return image;
    }

    public void Dispose(){if(disposed)return;disposed=true;work.CompleteAdding();}

    [ComImport,Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory {[PreserveSig] int GetImage(Size size,uint flags,out nint bitmap);}
    [StructLayout(LayoutKind.Sequential)] readonly struct Size {readonly int Width,Height;internal Size(int width,int height){Width=width;Height=height;}}
    [StructLayout(LayoutKind.Sequential)] struct Bitmap {internal int Type,Width,Height,WidthBytes;internal ushort Planes,BitsPixel;internal nint Bits;}
    [StructLayout(LayoutKind.Sequential)] struct BitmapHeader {internal uint Size;internal int Width,Height;internal ushort Planes,BitCount;internal uint Compression,ImageSize;internal int XPels,YPels;internal uint Used,Important;}
    [StructLayout(LayoutKind.Sequential)] struct BitmapInfo {internal BitmapHeader Header;internal uint Colors;}
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] static extern int SHCreateItemFromParsingName(string name,nint context,ref Guid iid,[MarshalAs(UnmanagedType.Interface)] out object item);
    [DllImport("gdi32.dll",EntryPoint="GetObjectW")] static extern int GetObject(nint handle,int size,out Bitmap bitmap);
    [DllImport("gdi32.dll")] static extern int GetDIBits(nint dc,nint bitmap,uint start,uint lines,[Out] byte[] pixels,ref BitmapInfo info,uint usage);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint handle);
    [DllImport("user32.dll")] static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint hwnd,nint dc);
}
