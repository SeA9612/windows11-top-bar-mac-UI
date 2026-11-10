using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Collections.Generic;
using System.Windows;
using PixelFormat=System.Drawing.Imaging.PixelFormat;
namespace GlassDock.TopBar;

// Reads only the Explorer tray flyout's icon artwork, never notification cards
// or other application windows. Pixels remain in memory for this open session.
internal static class NativeTrayFrame
{
    internal static BitmapSource? Capture(nint hwnd)
    {
        if(!BarNative.GetWindowRect(hwnd,out var r))return null;
        int width=r.Right-r.Left,height=r.Bottom-r.Top;
        if(width<8||height<8||width>2048||height>2048)return null;
        using var bitmap=new Bitmap(width,height,PixelFormat.Format32bppArgb);
        bool printed;
        using(var graphics=Graphics.FromImage(bitmap))
        {
            var dc=graphics.GetHdc();try{printed=PrintWindow(hwnd,dc,2);}finally{graphics.ReleaseHdc(dc);}
        }
        var pixels=Read(bitmap);
        if(!printed||Contrast(pixels)<24)
        {
            return null;
        }
        return FromPixels(pixels,width,height,width*4,GetDpiForWindow(hwnd));
    }
    // True when two captures show the same settled picture (at most 0.5% of
    // pixels differ noticeably, which tolerates blinking icon details).
    internal static bool Matches(BitmapSource a,BitmapSource b)
    {
        if(a.PixelWidth!=b.PixelWidth||a.PixelHeight!=b.PixelHeight)return false;
        int stride=a.PixelWidth*4;
        var first=new byte[stride*a.PixelHeight];var second=new byte[first.Length];
        a.CopyPixels(first,stride,0);b.CopyPixels(second,stride,0);
        int different=0,limit=Math.Max(4,a.PixelWidth*a.PixelHeight/200);
        for(int i=0;i<first.Length;i+=4)
            if(Math.Abs(first[i+3]-second[i+3])>48&&++different>limit)return false;
        return true;
    }
    internal static int CountVisibleIcons(BitmapSource artwork,IReadOnlyList<Int32Rect> cells)
    {
        var pixels=new byte[artwork.PixelWidth*artwork.PixelHeight*4];artwork.CopyPixels(pixels,artwork.PixelWidth*4,0);
        int count=0;
        foreach(var cell in cells)
        {
            int margin=Math.Min(6,Math.Min(cell.Width,cell.Height)/4),ink=0;
            for(int y=Math.Max(0,cell.Y+margin);y<Math.Min(artwork.PixelHeight,cell.Y+cell.Height-margin);y++)
                for(int x=Math.Max(0,cell.X+margin);x<Math.Min(artwork.PixelWidth,cell.X+cell.Width-margin);x++)
                    if(pixels[(y*artwork.PixelWidth+x)*4+3]>128)ink++;
            if(ink>=8)count++;
        }
        return count;
    }
    internal static BitmapSource? FromPixels(byte[] input,int width,int height,int stride,uint dpi)
    {
        var pixels=new byte[width*height*4];
        for(int y=0;y<height;y++)Array.Copy(input,y*stride,pixels,y*width*4,width*4);
        if(Contrast(pixels)<24)return null;
        int inset=Math.Min((int)Math.Round(20*dpi/96.0),Math.Min(width,height)/3);
        var counts=new System.Collections.Generic.Dictionary<int,int>();
        for(int y=8;y<height-8;y++)for(int x=8;x<width-8;x++)
        {
            int i=(y*width+x)*4;
            int color=((pixels[i]>>2)<<12)|((pixels[i+1]>>2)<<6)|(pixels[i+2]>>2);
            counts.TryGetValue(color,out int count);counts[color]=count+1;
        }
        int dominant=0,best=0;foreach(var pair in counts)if(pair.Value>best){dominant=pair.Key;best=pair.Value;}
        int blue=((dominant>>12)&63)*4+2,green=((dominant>>6)&63)*4+2,red=(dominant&63)*4+2;
        int ink=0;
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {
            int i=(y*width+x)*4;
            int difference=Math.Max(Math.Abs(pixels[i]-blue),Math.Max(Math.Abs(pixels[i+1]-green),Math.Abs(pixels[i+2]-red)));
            int alpha=x<inset||y<inset||x>=width-inset||y>=height-inset?0:Math.Clamp((difference-6)*255/18,0,255);
            pixels[i]=(byte)(pixels[i]*alpha/255);pixels[i+1]=(byte)(pixels[i+1]*alpha/255);pixels[i+2]=(byte)(pixels[i+2]*alpha/255);pixels[i+3]=(byte)alpha;
            if(alpha>128)ink++;
        }
        if(ink<40)return null;
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
