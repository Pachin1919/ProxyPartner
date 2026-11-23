// Development-only protocol and child-lifecycle tests. No live network or user profile is read.
#include "desktop.cpp"
#include <iostream>

int failures=0;
void Check(bool condition,const char* name) {
    if(!condition){++failures;
        std::cerr<<name<<"\n";
    }
}
void InitializeHiddenApp() {
    app.instance=GetModuleHandleW(nullptr);
    INITCOMMONCONTROLSEX controls{sizeof controls,ICC_LISTVIEW_CLASSES};
    InitCommonControlsEx(&controls);
    WNDCLASSW cls{};
    cls.hInstance=app.instance;
    cls.lpfnWndProc=DefWindowProcW;
    cls.lpszClassName=L"NativeDesktop.Test.Hidden";
    RegisterClassW(&cls);
    app.window=CreateWindowExW(0,cls.lpszClassName,L"",WS_OVERLAPPEDWINDOW,0,0,1000,650,nullptr,nullptr,app.instance,nullptr);
    cls.lpfnWndProc=CardsProc;
    cls.lpszClassName=L"NativeDesktop.Test.Cards";
    RegisterClassW(&cls);
    app.table=Child(app.window,WC_LISTVIEWW,L"",List,LVS_REPORT|LVS_OWNERDATA);
    app.cards=Child(app.window,cls.lpszClassName,L"",Cards);
    app.status=Child(app.window,L"STATIC",L"",100);
    app.search=Child(app.window,L"EDIT",L"",Search);
    for(int id=View;id<=Details;++id)Child(app.window,L"BUTTON",L"",id);
    app.Theme();
    if(!app.directory.empty())for(int i=0;i<5;++i){
        auto path=app.directory/L"TargetIcons"/(std::wstring(SiteIds[i])+L".ico");
        app.icons[i]=static_cast<HICON>(LoadImageW(nullptr,path.c_str(),IMAGE_ICON,0,0,LR_LOADFROMFILE));
    }
}
int Acceptance(int argc,wchar_t** argv) {
    fs::path output;
    for(int i=2;i<argc;++i){
        if(i+1>=argc)return 2;
        std::wstring option=argv[i],value=argv[++i];
        if(option==L"--profile")app.prefs.profile=value;
        else if(option==L"--age-identity")app.prefs.identity=value;
        else if(option==L"--cli-dir")app.directory=value;
        else if(option==L"--output")output=value;
        else return 2;
    }
    if(!LocalFile(app.prefs.profile) || (!app.prefs.identity.empty() && !LocalFile(app.prefs.identity)) ||
        app.directory.empty() || output.empty() || !output.is_absolute() || fs::exists(output))return 2;
    HANDLE job=CreateJobObjectW(nullptr,nullptr);
    if(!job || !AssignProcessToJobObject(job,GetCurrentProcess())){
        if(job)CloseHandle(job);
        return 2;
    }
    InitializeHiddenApp();
    app.prefs.deadline=6000;
    ULONGLONG started=GetTickCount64();
    auto run=[&](bool inspect){
        app.Start(inspect);
        while(app.Running() && GetTickCount64()-started<600000){app.Drain();Sleep(10);}
        if(app.Running())app.Finish(true);
    };
    run(true);
    bool catalog=!app.nodes.empty();
    if(catalog)run(false);
    Json counts=Json::object();
    int scored=0,received=0;
    for(const auto& node:app.nodes){
        if(node.round)++received;
        if(node.score>=0)++scored;
        for(const auto& site:node.sites){
            std::string state=site.timeout?"timeout":!site.attempted?"unattempted":std::to_string(site.http);
            if(!counts.contains(state))counts[state]=0;
            counts[state]=counts[state].get<int>()+1;
        }
    }
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION memory{};
    JOBOBJECT_BASIC_ACCOUNTING_INFORMATION processes{};
    bool accounting=QueryInformationJobObject(job,JobObjectExtendedLimitInformation,&memory,sizeof memory,nullptr) &&
        QueryInformationJobObject(job,JobObjectBasicAccountingInformation,&processes,sizeof processes,nullptr);
    bool finished=catalog && app.sawSummary && !app.Running();
    Json result={{"status",finished && accounting?"COMPLETE":"INCOMPLETE"},{"nodes",app.nodes.size()},
        {"received_nodes",received},{"scored_nodes",scored},{"cli_completed",app.completed},{"attempts",app.attempts},
        {"http_counts",counts},{"wall_ms",GetTickCount64()-started},{"deadline_ms",6000},
        {"peak_job_commit_bytes",memory.PeakJobMemoryUsed},{"total_job_processes",processes.TotalProcesses},
        {"active_job_processes_at_report",processes.ActiveProcesses},{"accounting",accounting},
        {"measurement_scope","acceptance executable including actual App::Start, CLI and descendants through report"},
        {"gui_visual","NOT_RUN"}};
    std::ofstream file(output,std::ios::binary);
    file<<result.dump(2);
    file.close();
    DestroyWindow(app.window);
    CloseHandle(job);
    return finished && accounting && file.good()?0:1;
}
int wmain(int argc,wchar_t** argv) {
    if(argc>1 && wcscmp(argv[1],L"--acceptance")==0)return Acceptance(argc,argv);
    if(argc>1 && wcscmp(argv[1],L"--fixture-grandchild")==0){Sleep(60000);
        return 0;
    }
    if(argc>1 && (wcscmp(argv[1],L"inspect")==0 || wcscmp(argv[1],L"probe")==0)) {
        if(wcscmp(argv[1],L"probe")==0){
            wchar_t self[32768]{};
            GetModuleFileNameW(nullptr,self,32768);
            std::wstring command=Quote(self)+L" --fixture-grandchild";
            STARTUPINFOW startup{sizeof startup};
            PROCESS_INFORMATION child{};
            if(!CreateProcessW(self,command.data(),nullptr,nullptr,FALSE,CREATE_NO_WINDOW,nullptr,nullptr,&startup,&child))return 2;
            for(int i=2;i+1<argc;++i)if(wcscmp(argv[i],L"--profile")==0){
                std::ofstream pidFile(fs::path(argv[i+1]).parent_path()/L"grandchild.pid");
                pidFile<<child.dwProcessId;
                break;
            }
            CloseHandle(child.hThread);
            CloseHandle(child.hProcess);
            Sleep(60000);
            return 0;
        }
        std::wstring profile,identity;
        for(int i=2;i+1<argc;++i){
            if(wcscmp(argv[i],L"--profile")==0)profile=argv[i+1];
            if(wcscmp(argv[i],L"--age-identity")==0)identity=argv[i+1];
        }
        if(!profile.empty()){
            std::ofstream observed(fs::path(profile).parent_path()/L"observed-identity.txt");
            observed<<Utf8(identity);
        }
        std::cout<<"{\"event\":\"catalog\",\"nodes\":1}\n"
        <<"{\"event\":\"node\",\"node\":\"test-node\",\"protocol\":\"test\",\"state\":\"ready\"}\n";
        return 0;
    }
    if(argc!=2 && argc!=3)return 2;
    InitializeHiddenApp();
    Check(Wide(Utf8(L"香港节点 🟢"))==L"香港节点 🟢","UTF8 node roundtrip");
    std::wstring quoted=Quote(L"C:\\a path\\\"quoted\\");
    int count=0;
    LPWSTR* decoded=CommandLineToArgvW((L"test.exe "+quoted).c_str(),&count);
    Check(count==2 && std::wstring(decoded[1])==L"C:\\a path\\\"quoted\\","Command-line quote roundtrip");
    LocalFree(decoded);
    Check(!app.Event(Json::array()),"Reject invalid root");
    Check(!app.Event(Json{{"event","node"},{"node","invalid\nnode"}}),"Reject multiline name");
    Check(app.Event(Json{{"event","catalog"},{"nodes",2}}),"Catalog");
    Check(app.Event(Json{{"event","node"},{"node",Utf8(L"香港节点")},{"protocol","ss"},{"state","probing"}}),"Node probing");
    Check(app.nodes.size()==1 && app.nodes[0].probing && app.nodes[0].sites[4].active,"All five pending sites");
    Json sites=Json::array();
    for(int i=0;i<5;++i)sites.push_back({{"target",Utf8(SiteIds[i])},{"attempted",true},{"httpStatus",i==2?401:i==3?403:200},{"latencyMs",120+i},{"timedOut",false}});
    Check(app.Event(Json{{"event","round"},{"node",Utf8(L"香港节点")},{"score",74},{"round",1},{"stableTargets",0},{"sites",sites}}),"Round accepted");
    auto& node=app.nodes[0];
    Check(node.score==74 && !node.probing && !node.sites[4].active,"Score and completed activity");
    Check(SiteText(node.sites[2],false,true).find(L"401")!=std::wstring::npos,"401 retained in detail");
    Check(SiteText(node.sites[3],false,true).find(L"403")!=std::wstring::npos,"403 retained in detail");
    Check(node.sites[0].http==200 && node.sites[1].http==200 && node.sites[4].latency==124,"All five target positions");
    SetWindowTextW(app.search,L"香港");
    app.Filter();
    Check(app.visible.size()==1,"Chinese filter");
    Check(ScoreColor(39)==RGB(242,157,162) && ScoreColor(60)==RGB(230,196,119) && ScoreColor(80)==RGB(121,199,157),"Dark score bands");
    app.nodes.clear();
    SetWindowTextW(app.search,L"");
    for(int i=0;i<MaxNodes;++i)Check(app.Event(Json{{"event","node"},{"node","fixture-"+std::to_string(i)},{"protocol","ss"},{"state","ready"}}),"1000 node catalog");
    app.Filter();
    Check(app.visible.size()==MaxNodes && ListView_GetItemCount(app.table)==MaxNodes,"Virtual list 1000 rows");
    Check(!app.Event(Json{{"event","node"},{"node","overflow-node"}}),"Reject node overflow");
    auto root=fs::path(argv[1]).parent_path()/L"fixture-cli";
    fs::create_directories(root);
    wchar_t executable[32768]{};
    GetModuleFileNameW(nullptr,executable,32768);
    fs::copy_file(executable,root/L"ProxyPartner.Cli.exe");
    app.directory=root;
    app.prefs.profile=(root/L"fixture-only.yaml").wstring();
    {std::ofstream fixture(app.prefs.profile);
        fixture<<"fixture-only, never passed to a live CLI";
    }
    app.standardProfile=app.prefs.profile;
    {std::ofstream key(root/L".runtime_key");key<<"fixture-only";}
    SetWindowTextW(app.search,L"");
    app.Start(true);
    ULONGLONG deadline=GetTickCount64()+10000;
    while(app.Running() && GetTickCount64()<deadline){app.Drain();
        Sleep(10);
    }
    Check(!app.Running() && app.nodes.size()==1 && app.nodes[0].name==L"test-node","Child inspect JSONL lifecycle");
    {
        std::ifstream observed(root/L"observed-identity.txt");
        std::string identity;
        std::getline(observed,identity);
        Check(Wide(identity)==(root/L".runtime_key").wstring() && app.prefs.identity.empty(),"Standard runtime key passed without changing preferences");
    }
    app.standardProfile=root/L"different-runtime.yaml";
    app.Start(true);
    deadline=GetTickCount64()+10000;
    while(app.Running() && GetTickCount64()<deadline){app.Drain();Sleep(10);}
    {
        std::ifstream observed(root/L"observed-identity.txt");
        std::string identity;
        std::getline(observed,identity);
        Check(!app.Running() && identity.empty(),"Imported profile does not borrow a runtime key");
    }
    app.Start(false);
    Check(app.Running(),"Child probe started");
    fs::path pidFile=root/L"grandchild.pid";
    deadline=GetTickCount64()+5000;
    while(!fs::exists(pidFile) && GetTickCount64()<deadline)Sleep(10);
    DWORD childPid=0;
    {std::ifstream input(pidFile);
        input>>childPid;
    }
    HANDLE grandchild=childPid?OpenProcess(SYNCHRONIZE,FALSE,childPid):nullptr;
    Check(grandchild!=nullptr,"Grandchild fixture started");
    HANDLE observed=nullptr;
    DuplicateHandle(GetCurrentProcess(),app.process,GetCurrentProcess(),&observed,SYNCHRONIZE,FALSE,0);
    app.Finish(true);
    Check(observed && WaitForSingleObject(observed,0)==WAIT_OBJECT_0,"Cancel reaps probe child");
    if(observed)CloseHandle(observed);
    Check(grandchild && WaitForSingleObject(grandchild,5000)==WAIT_OBJECT_0,"Cancel reaps probe grandchild");
    if(grandchild)CloseHandle(grandchild);
    Check(!app.process && !app.pipe && !app.job,"Cancel releases handles");
    app.preferencePath=root/L"preferences.json";
    Preferences prefs;
    prefs.profile=L"C:\\path with spaces\\节点.yaml";
    prefs.english=true;
    prefs.parallel=3;
    Check(app.SavePreferences(prefs),"Atomic local preference save");
    std::ifstream saved(app.preferencePath);
    Json value=Json::parse(saved,nullptr,false);
    Check(value.is_object() && value["profile"]==Utf8(prefs.profile) && value["parallel"]==3,"Persisted values");
    bool realCliInspect=false;
    if(argc==3){
        app.directory=fs::path(argv[2]);
        app.prefs.profile=(root/L"inspect-only.yaml").wstring();
        {
            std::ofstream profile(app.prefs.profile);
            profile<<"proxies:\n  - name: '"<<Utf8(L"contract-only · 香港")<<"'\n    type: anytls\n    server: 127.0.0.1\n    port: 9\n    password: fixture-only\n";
        }
        app.Start(true);
        deadline=GetTickCount64()+15000;
        while(app.Running() && GetTickCount64()<deadline){app.Drain();Sleep(10);}
        realCliInspect=!app.Running() && app.nodes.size()==1 && app.nodes[0].name==L"contract-only · 香港" && app.nodes[0].state==L"ready";
        Check(realCliInspect,"Real native CLI inspect integration");
        if(app.Running())app.Finish(true);
    }
    if(app.font)DeleteObject(app.font);
    if(app.bold)DeleteObject(app.bold);
    if(app.scoreFont)DeleteObject(app.scoreFont);
    if(app.siteFont)DeleteObject(app.siteFont);
    if(app.controlBrush)DeleteObject(app.controlBrush);
    if(app.backgroundBrush)DeleteObject(app.backgroundBrush);
    DestroyWindow(app.window);
    Json result={{"status",failures?"FAIL":"PASS"},{"failures",failures},{"network","NOT_RUN"},{"fixture_only",true},{"native_cli_inspect",realCliInspect},
        {"checks",{"UTF8","argv quoting","invalid JSON boundary","five target activity and round mapping","401 and 403 detail","Chinese filter","score colors","1000 virtual rows and overflow bound","inspect child","probe cancel and descendant cleanup","preferences"}}};
    std::ofstream output(argv[1]);
    output<<result.dump(2);
    return failures?1:0;
}
