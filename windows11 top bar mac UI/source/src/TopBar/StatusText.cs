using System;
using System.Globalization;
namespace GlassDock.TopBar;
internal static class StatusText
{
    internal static string Keyboard(long layout)
    {
        int id=(int)(layout&0xFFFF);
        if(id==0)return "언어";
        if(id==0x0412)return "한국어";
        if(id==0x0409)return "ENG";
        try{return CultureInfo.GetCultureInfo(id).TwoLetterISOLanguageName.ToUpperInvariant();}catch(CultureNotFoundException){return "언어";}
    }
    internal static string? Battery(float fraction,bool unavailable)
    {
        if(unavailable||!float.IsFinite(fraction)||fraction<0||fraction>1)return null;
        return $"▰ {Math.Round(fraction*100)}%";
    }
    internal static string Clock(DateTime date,bool compact)=>date.ToString(compact?"tt h:mm":"M월 d일 (ddd) tt h:mm",CultureInfo.GetCultureInfo("ko-KR"));
    internal static int HeightForDpi(uint dpi)=>(int)Math.Max(1,Math.Round(28*(dpi==0?96:dpi)/96d));
}
