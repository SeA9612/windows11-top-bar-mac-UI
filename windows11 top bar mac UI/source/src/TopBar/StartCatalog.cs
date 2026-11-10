using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
namespace GlassDock.TopBar;

// One entry of Windows' own app list (shell:AppsFolder). Id is the AppsFolder
// parsing name: an AUMID, a known-folder path or an absolute path.
internal sealed record StartApp(string Name,string Id,string Category);

// Pure rules for the glass Start screen: which apps the category grid shows,
// which category each app belongs to, and how search results are ranked.
// Windows does not publish its own category assignment, so these rules follow
// the categories and grouping the Windows Start menu shows for common apps.
internal static class StartCatalog
{
    internal static readonly string[] Categories={"생산성","유틸리티 및 도구","개발자 도구","기타","창의성","게임","엔터테인먼트"};
    const string Other="기타";

    static readonly string[] DocumentExtensions={".html",".htm",".chm",".txt",".pdf",".url",".md",".twdx",".rtf",".doc",".docx"};
    static readonly string[] SystemFolders=
    {
        "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\", // System32
        "{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}\\", // SysWOW64
        "{F38BF404-1D43-42F2-9305-67DE0B28FC23}\\", // Windows
    };
    // Windows 11 keeps these inside the "Windows Tools" folder instead of listing them.
    static readonly string[] SystemTools=
    {
        "로컬 보안 정책","local security policy","리소스 모니터","resource monitor","성능 모니터","performance monitor",
        "이벤트 뷰어","event viewer","작업 스케줄러","task scheduler","컴퓨터 관리","computer management","작업 관리자","task manager",
    };
    static readonly string[] SystemIds=
    {
        "microsoft.windows.controlpanel","microsoft.windows.shell.rundialog","microsoft.windows.remotedesktop",
        "microsoft.windows.mediaplayer32","microsoftwindows.client.cbs_cw5n1h2txyewy!webexperiencehost",
    };

    // Help pages, web links, manuals and uninstallers are not apps.
    internal static bool IsLaunchable(string name,string id)
    {
        if(string.IsNullOrWhiteSpace(name)||string.IsNullOrWhiteSpace(id))return false;
        if(id.Contains("://",StringComparison.Ordinal))return false;
        string extension=SafeExtension(id);
        if(DocumentExtensions.Contains(extension,StringComparer.OrdinalIgnoreCase))return false;
        string lower=name.ToLowerInvariant();
        foreach(string word in new[]{"uninstall","제거","release notes","faq","manual","documentation","사용가이드","(administrator)"})
            if(lower.Contains(word,StringComparison.Ordinal))return false;
        return true;
    }

    // Shown in the category grid. Search still finds every launchable app.
    internal static bool IsListed(string name,string id)
    {
        if(!IsLaunchable(name,id))return false;
        foreach(string folder in SystemFolders)if(id.StartsWith(folder,StringComparison.OrdinalIgnoreCase))return false;
        if(SystemIds.Contains(id.ToLowerInvariant(),StringComparer.Ordinal))return false;
        string lower=name.ToLowerInvariant();
        return !SystemTools.Any(tool=>lower==tool);
    }

    static readonly (string Category,string[] Keys)[] Rules=
    {
        ("개발자 도구",new[]{"visual studio","visualstudiocode","vscode","git ","git\\","\\git\\","python","idle (","pydoc","pycharm","intellij","android studio","dev home","devhome","터미널","windowsterminal","xshell","xftp","xagent","putty","vctl","antigravity","deepseek","openai.codex","chatgpt","docker","postman","github","node.js","cursor"}),
        ("유틸리티 및 도구",new[]{"캡처 도구","screensketch","snipping","vmware","virtual network editor","스티커 메모","stickynotes","계산기","calculator","시계","windowsalarms","immersivecontrolpanel","windows 보안","sechealthui","windows 백업","windowsbackup","빠른 지원","quickassist","반디집","bandizip","treesize","windows tools","administrativetools","수행하려면 클릭","clicktodo","nvidia","intelgraphics","인텔","realtek","smartmicrophone","휴대폰과 연결","yourphone","피드백 허브","feedbackhub","gethelp","power automate","powerautomate","windowsstore","onedrive","skydrive"}),
        ("생산성",new[]{"chrome","msedge","microsoft edge","firefox","whale","파일 탐색기","microsoft.windows.explorer","메모장","notepad","outlook","teams","microsoft to do","todos","winword","excel","powerpoint","onenote","notion","thinkwise","hwp","한글"}),
        ("창의성",new[]{"그림판","paint","windows.photos","사진","카메라","windowscamera","clipchamp","녹음기","soundrecorder","photoshop","illustrator","figma"}),
        ("게임",new[]{"xbox","gamingapp","solitaire","game bar","xboxgamingoverlay","steam","epic games","minecraft"}),
        ("엔터테인먼트",new[]{"미디어 플레이어","zunemusic","media player","netflix","spotify","youtube","disney"}),
    };

    internal static string CategoryOf(string name,string id)
    {
        string text=(name+" | "+id).ToLowerInvariant();
        // "설정" is a whole-name match so apps such as "인쇄 설정 도우미" are not pulled in.
        if(name=="설정"||name.Equals("Settings",StringComparison.OrdinalIgnoreCase))return "유틸리티 및 도구";
        foreach(var (category,keys) in Rules)
            foreach(string key in keys)
                if(text.Contains(key,StringComparison.Ordinal))return category;
        return Other;
    }

    internal static StartApp Create(string name,string id)=>new(name,id,CategoryOf(name,id));

    // The Korean Start menu lists Latin names (A–Z) before Hangul (ㄱ–ㅎ).
    // ICU's ko-KR collation does the opposite, so compare the script first.
    static readonly Comparer<string> NameOrder=Comparer<string>.Create((a,b)=>
    {
        int script=IsHangul(a).CompareTo(IsHangul(b));
        return script!=0?script:string.Compare(a,b,CultureInfo.GetCultureInfo("ko-KR"),CompareOptions.IgnoreCase);
    });
    static bool IsHangul(string name)=>name.Length>0&&name[0] is >='가' and <='힣' or >='ㄱ' and <='ㆎ' or >='ᄀ' and <='ᇿ';

    // Non-empty categories in display order, each with apps sorted by name.
    internal static List<(string Category,List<StartApp> Apps)> Group(IEnumerable<StartApp> apps)
    {
        var listed=apps.Where(a=>IsListed(a.Name,a.Id)).ToList();
        var groups=new List<(string,List<StartApp>)>();
        foreach(string category in Categories)
        {
            var members=listed.Where(a=>a.Category==category).OrderBy(a=>a.Name,NameOrder).ToList();
            if(members.Count>0)groups.Add((category,members));
        }
        return groups;
    }

    // Name prefix, then word prefix, then anywhere in the name, then the app id.
    internal static List<StartApp> Search(IEnumerable<StartApp> apps,string query,int limit=24)
    {
        query=query.Trim();
        if(query.Length==0)return new();
        var ranked=new List<(int Rank,StartApp App)>();
        foreach(var app in apps)
        {
            if(!IsLaunchable(app.Name,app.Id))continue;
            int rank=Rank(app,query);
            if(rank>=0)ranked.Add((rank,app));
        }
        return ranked.OrderBy(r=>r.Rank).ThenBy(r=>r.App.Name.Length).ThenBy(r=>r.App.Name,NameOrder)
            .GroupBy(r=>r.App.Name,StringComparer.OrdinalIgnoreCase).Select(g=>g.First().App).Take(limit).ToList();
    }

    static int Rank(StartApp app,string query)
    {
        var comparison=StringComparison.CurrentCultureIgnoreCase;
        if(app.Name.StartsWith(query,comparison))return 0;
        foreach(string word in app.Name.Split(new[]{' ','-','_','(',')','.'},StringSplitOptions.RemoveEmptyEntries))
            if(word.StartsWith(query,comparison))return 1;
        if(app.Name.Contains(query,comparison))return 2;
        if(app.Id.Contains(query,StringComparison.OrdinalIgnoreCase))return 3;
        return -1;
    }

    static string SafeExtension(string id)
    {
        try{return Path.GetExtension(id);}catch(ArgumentException){return "";}
    }
}
