using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelFormat=System.Drawing.Imaging.PixelFormat;
namespace GlassDock.TopBar;

// Reads only the Explorer tray flyout's icon artwork, never notification cards
// or other application windows. Pixels remain in memory for this open session.
internal static class NativeTrayFrame
{
    internal static BitmapSource? Capture(nint hwnd,bool allowScreenFallback)
    {
        if(!BarNative.GetWindowRect(hwnd,out var r))return null;
        int width=r.Right-r.Left,height=r.Bottom-r.Top;
        if(width<8||height<8||width>2048||height>2048)return null;
        int inset=Math.Min((int)Math.Round(20*GetDpiForWindow(hwnd)/96.0),Math.Min(width,height)/3);
        using var bitmap=new Bitmap(width,height,PixelFormat.Format32bppArgb);
        bool printed;
        using(var graphics=Graphics.FromImage(bitmap))
        {
            var dc=graphics.GetHdc();try{printed=PrintWindow(hwnd,dc,2);}finally{graphics.ReleaseHdc(dc);}
        }
        var pixels=Read(bitmap);
        if(!printed||Contrast(pixels)<24)
        {
            if(!allowScreenFallback)return null;
            using(var graphics=Graphics.FromImage(bitmap))graphics.CopyFromScreen(r.Left,r.Top,0,0,new Size(width,height),CopyPixelOperation.SourceCopy);
            pixels=Read(bitmap);
            if(Contrast(pixels)<24)return null;
        }
        var counts=new System.Collections.Generic.Dictionary<int,int>();
        for(int y=8;y<height-8;y++)for(int x=8;x<width-8;x++)
        {
            int i=(y*width+x)*4;
            int color=((pixels[i]>>2)<<12)|((pixels[i+1]>>2)<<6)|(pixels[i+2]>>2);
            counts.TryGetValue(color,out int count);counts[color]=count+1;
        }
        int dominant=0,best=0;foreach(var pair in counts)if(pair.Value>best){dominant=pair.Key;best=pair.Value;}
        int blue=((dominant>>12)&63)*4+2,green=((dominant>>6)&63)*4+2,red=(dominant&63)*4+2;
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {
            int i=(y*width+x)*4;
            int difference=Math.Max(Math.Abs(pixels[i]-blue),Math.Max(Math.Abs(pixels[i+1]-green),Math.Abs(pixels[i+2]-red)));
            int alpha=x<inset||y<inset||x>=width-inset||y>=height-inset?0:Math.Clamp((difference-6)*255/18,0,255);
            pixels[i]=(byte)(pixels[i]*alpha/255);pixels[i+1]=(byte)(pixels[i+1]*alpha/255);pixels[i+2]=(byte)(pixels[i+2]*alpha/255);pixels[i+3]=(byte)alpha;
        }
        var source=BitmapSource.Create(width,height,96,96,PixelFormats.Pbgra32,null,pixels,width*4);source.Freeze();return source;
    }
    static byte[] Read(Bitmap bitmap)
    {
        var data=bitmap.LockBits(new Rectangle(0,0,bitmap.Width,bitmap.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try{var bytes=new byte[bitmap.Width*bitmap.Height*4];for(int y=0;y<bitmap.Height;y++)Marshal.Copy(data.Scan0+y*data.Stride,bytes,y*bitmap.Width*4,bitmap.Width*4);return bytes;}
        finally{bitmap.UnlockBits(data);}
    }
    static int Contrast(byte[] pixels)
    {
        int min=255,max=0;
        for(int i=0;i<pixels.Length;i+=4){int level=(pixels[i]+pixels[i+1]+pixels[i+2])/3;min=Math.Min(min,level);max=Math.Max(max,level);}
        return max-min;
    }
    [DllImport("user32.dll")] static extern bool PrintWindow(nint hwnd,nint dc,uint flags);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint hwnd);
}
