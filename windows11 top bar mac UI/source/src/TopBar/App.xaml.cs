using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
namespace GlassDock.TopBar;
public partial class App : Application
{
    Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if(NativeTrayOpacity.RunGuard(e.Args)||TaskbarHider.RunGuard(e.Args)){Shutdown();return;}
        instance=new Mutex(true,"Local\\GlassDock.TopBar",out bool first);
        if(!first){MessageBox.Show("상단 바가 이미 실행 중입니다.");Shutdown();return;}
        DispatcherUnhandledException+=(_,args)=>{BarSettings.Log(args.Exception);args.Handled=true;MessageBox.Show("상단 바 오류: "+args.Exception.Message+"\n상단 바를 종료합니다.");Shutdown(1);};
        NativeTrayOpacity.RecoverOrphans();
        TaskbarHider.RecoverOrphan();
        MainWindow=new TopBarWindow();MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e){instance?.Dispose();base.OnExit(e);}
}
public sealed class BarSettings
{
    public string Monitor {get;set;}="";
    public static string Folder=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GlassDock");
    public static BarSettings Load(){try{return JsonSerializer.Deserialize<BarSettings>(File.ReadAllText(Path.Combine(Folder,"topbar.json")))??new();}catch(Exception e) when(e is IOException or JsonException or UnauthorizedAccessException){return new();}}
    public void Save(){Directory.CreateDirectory(Folder);var path=Path.Combine(Folder,"topbar.json");File.WriteAllText(path+".tmp",JsonSerializer.Serialize(this));File.Move(path+".tmp",path,true);}
    public static void Log(Exception e){try{Directory.CreateDirectory(Folder);File.AppendAllText(Path.Combine(Folder,"topbar-errors.log"),DateTimeOffset.Now+" "+e+Environment.NewLine);}catch{}}
}
