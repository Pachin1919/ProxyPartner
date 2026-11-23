#pragma once

Json ExitFixtureObservation(int samples=1) {
    return {{"event","exit_observation"},{"node","test-node"},{"round",1},{"family",4},{"address","8.8.8.8"},
        {"observedUtc","2026-11-23T01:00:00Z"},{"observers",2},{"corroboratingAddress","8.8.8.8"},
        {"agreement","confirmed"},{"changes",0},{"lastVerifiedAddress","8.8.8.8"},
        {"consistentSince","2026-11-23T01:00:00Z"},{"confirmedSamples",samples}};
}
Json ExitFixtureState(int samples=1) {
    return {{"event","exit_state"},{"version",1},{"node","test-node"},{"protocol","test"},{"profileRevision","fixture-revision"},
        {"families",Json::array({{{"family",4},{"address","8.8.8.8"},{"lastVerifiedAddress","8.8.8.8"},
            {"changes",0},{"confirmedSamples",samples},{"observedUnixMs",1795395600000LL},{"consistentSinceUnixMs",1795395600000LL}}})}};
}
int ExitFixture(int argc,wchar_t** argv) {
    std::wstring profile,statePath,nodeName;
    for(int i=2;i+1<argc;++i){
        if(wcscmp(argv[i],L"--profile")==0)profile=argv[i+1];
        if(wcscmp(argv[i],L"--exit-state")==0)statePath=argv[i+1];
        if(wcscmp(argv[i],L"--node")==0)nodeName=argv[i+1];
    }
    if(nodeName!=L"test-node")return 2;
    if(fs::path(profile).filename()==L"exit-error.yaml"){
        std::cout<<Json{{"event","error"},{"message","fixture failure"}}.dump()<<'\n';return 1;
    }
    int samples=1;
    if(!statePath.empty()){
        std::ifstream input(statePath);
        Json incoming=Json::parse(input,nullptr,false),normalized;
        if(!NormalizeExitState(incoming,L"test-node",L"test",normalized))return 2;
        samples=normalized["families"][0]["confirmedSamples"].get<int>()+1;
        std::cout<<Json{{"event","exit_state_input"},{"state","accepted"}}.dump()<<'\n';
    }
    std::cout<<Json{{"event","catalog"},{"nodes",1}}.dump()<<'\n'
        <<Json{{"event","exit_begin"},{"node","test-node"},{"protocol","test"},{"rounds",1},{"sources",2},{"independent",true}}.dump()<<'\n';
    for(int i=0;i<2;++i)std::cout<<Json{{"event","exit_source"},{"node","test-node"},{"round",1},{"source",i+1},
        {"host",i?"api.ipify.org":"ipv4.icanhazip.com"},{"observedUtc","2026-11-23T01:00:00Z"},
        {"httpStatus",200},{"elapsedMs",20+i},{"timedOut",false},{"address","8.8.8.8"},{"family",4},{"state","public"}}.dump()<<'\n';
    std::cout<<ExitFixtureObservation(samples).dump()<<'\n'<<ExitFixtureState(samples).dump()<<'\n'<<std::flush;
    if(fs::path(profile).filename()==L"exit-slow.yaml")Sleep(60000);
    Sleep(200);
    std::cout<<Json{{"event","exit_summary"},{"node","test-node"},{"rounds",1},{"publicObservations",1},
        {"confirmedFamilies",1},{"confirmedObservations",1},{"cancelled",false}}.dump()<<'\n';return 0;
}
void TestExit(const fs::path& root) {
    std::wstring original=app.prefs.profile;
    app.prefs.english=true;app.tableMode=false;app.Theme();
    Check(Field(app.window,View)==L"Table" && Field(app.window,TestSelected)==L"Test node","Compact English toolbar labels");
    app.tableMode=true;app.Layout();Check(Field(app.window,View)==L"Cards","View action label follows table mode");
    app.prefs.english=false;app.tableMode=false;app.Theme();
    app.selected=0;
    app.ShowDetails(false);
    Check(app.details && GetDlgItem(app.details,RetestExit) && GetDlgItem(app.details,1)==app.detailText && !IsWindowVisible(app.details),"Hidden real detail footer IDs");
    int completed=app.completed,scored=app.scored,total=app.total,attempts=app.attempts;
    auto wait=[](){auto until=GetTickCount64()+5000;while(app.Running() && GetTickCount64()<until){app.Drain();Sleep(10);}Check(!app.Running(),"Exit child terminates");};
    SendMessageW(app.details,WM_COMMAND,RetestExit,0);
    HANDLE running=app.process;
    app.Start(false);
    Check(running && app.process==running && app.exitMode && !IsWindowEnabled(GetDlgItem(app.details,RetestExit)),"Detail action starts exclusive exit child");
    wait();
    auto facts=app.nodes[0].exit;
    Check(facts && facts->observations[0].valid && !facts->observations[1].valid && facts->sources[1].response.latency==21 && app.sawExitSummary,"Exit JSONL sources and confirmed IPv4 without fabricated IPv6");
    Check(app.nodes[0].score==74 && app.nodes[0].sites[2].http==401 && app.nodes[0].sites[2].latency==122 &&
        app.completed==completed && app.scored==scored && app.total==total && app.attempts==attempts,"Exit leaves independent probe samples and counts unchanged");
    Check(!IsWindowVisible(app.details) && IsWindowEnabled(GetDlgItem(app.details,RetestExit)),"Updates preserve hidden details and reenable action");
    app.Start(false,L"test-node",false,true);
    fs::path transfer=app.exitStatePath;
    Check(!transfer.empty() && fs::exists(transfer),"Repeated request creates bounded temporary state transfer");
    HANDLE denied=CreateFileW(transfer.c_str(),GENERIC_WRITE,FILE_SHARE_READ|FILE_SHARE_WRITE,nullptr,OPEN_EXISTING,0,nullptr);
    Check(denied==INVALID_HANDLE_VALUE,"Temporary state excludes competing writers");
    if(denied!=INVALID_HANDLE_VALUE)CloseHandle(denied);
    wait();
    Check(facts->observations[0].confirmedSamples==2 && !fs::exists(transfer) && app.exitStatePath.empty() && !app.exitStateFile,"Child reads previous state and finish deletes transfer");
    Check(app.ExitReport(app.nodes[0]).find(L"8.8.8.8")!=std::wstring::npos,"Detail text contains observed address");
    app.prefs.profile=(root/L"exit-error.yaml").wstring();
    {std::ofstream file(app.prefs.profile);file<<"fixture-only";}
    app.Start(false,L"test-node",false,true);wait();
    Check(facts->observations[0].confirmedSamples==2 && !facts->notice.empty() && !app.sawExitSummary,"Failure preserves last valid observations");
    app.prefs.profile=(root/L"exit-slow.yaml").wstring();
    {std::ofstream file(app.prefs.profile);file<<"fixture-only";}
    app.Start(false,L"test-node",false,true);
    transfer=app.exitStatePath;
    auto until=GetTickCount64()+5000;
    while(app.Running() && facts->observations[0].confirmedSamples<3 && GetTickCount64()<until){app.Drain();Sleep(10);}
    HANDLE child=nullptr;DuplicateHandle(GetCurrentProcess(),app.process,GetCurrentProcess(),&child,SYNCHRONIZE,FALSE,0);
    app.Finish(true);
    Check(child && WaitForSingleObject(child,0)==WAIT_OBJECT_0 && !app.process && !app.job && !app.pipe && !fs::exists(transfer) &&
        facts->observations[0].confirmedSamples==3,"Cancel reaps exit child, deletes state and retains partial observation");
    if(child)CloseHandle(child);
    app.activeExitFacts=facts;
    Json observation=ExitFixtureObservation();
    observation["agreement"]="conflict";observation["corroboratingAddress"]="1.1.1.1";observation["confirmedSamples"]=0;
    Check(app.Event(observation) && facts->observations[0].agreement==L"conflict" && facts->observations[0].lastVerified==L"8.8.8.8","Conflict retains last confirmed address");
    observation["family"]=6;observation["address"]="2606:4700:4700::1111";observation["corroboratingAddress"]=nullptr;
    observation["lastVerifiedAddress"]=nullptr;observation["agreement"]="single_source";observation["observers"]=1;
    Check(app.Event(observation) && facts->observations[1].valid && facts->observations[1].agreement==L"single_source","IPv6 single-source observation remains separate");
    observation["node"]="wrong-node";
    Check(!app.Event(observation),"Exit rejects mismatching node");
    Check(app.Event(Json{{"event","exit_source"},{"node","test-node"},{"round",1},{"source",1},{"host","ipv4.icanhazip.com"},
        {"state","invalid_address"},{"address","127.0.0.1"},{"family",4},{"httpStatus",200},{"elapsedMs",12}}) &&
        facts->sources[0].address.empty() && facts->observations[0].valid,"Rejected source body does not become a public observation");
    Json state=ExitFixtureState(),normalized;
    state["extra"]="unneeded snapshot";
    Check(NormalizeExitState(state,L"test-node",L"test",normalized) && !normalized.contains("extra"),"Exit state projects known fields only");
    state["families"].push_back(state["families"][0]);
    Check(!NormalizeExitState(state,L"test-node",L"test",normalized),"Exit state rejects duplicate families");
    Check(!ExitLiteral(L"8.8.8.8 trailing",4) && !ExitLiteral(L"1.1.1.1",6),"Exit literal boundary rejects malformed address and family");
    app.activeExitFacts.reset();app.exitMode=false;
    app.prefs.profile=original;
    DestroyWindow(app.details);
}
