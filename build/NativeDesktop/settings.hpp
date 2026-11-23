#pragma once

LRESULT CALLBACK PreferencesProc(HWND window,UINT message,WPARAM w,LPARAM l);

bool App::ReadDemoWeights(Preferences& value) {
    for(int i=0;i<6;++i){
        auto input=Field(preferencesWindow,WeightFirst+i);
        wchar_t* end=nullptr;
        long weight=wcstol(input.c_str(),&end,10);
        if(input.empty() || *end || weight<0 || weight>100)return false;
        (i<3?value.components:value.targets)[i%3]=static_cast<int>(weight);
    }
    return ValidWeights(value.components) && ValidWeights(value.targets);
}
void App::UpdateDemo() {
    Preferences value=prefs;
    std::wstring text;
    if(!ReadDemoWeights(value))text=L(L"两组权重须各合计 100%，每项为 0–100 的整数。",L"Each weight group must total 100%, using whole numbers from 0 to 100.");
    else {
        auto apply=[](const std::array<int,3>& weights,double a,double b,double c){return (weights[0]*a+weights[1]*b+weights[2]*c)/100;};
        double openai=apply(value.components,90,75,80),gpt=apply(value.components,70,90,55),github=apply(value.components,100,95,90);
        wchar_t result[192]{};
        swprintf_s(result,L"OPENAI %.1f / GPT %.1f / GitHub %.1f → %.1f",openai,gpt,github,apply(value.targets,openai,gpt,github));
        text=std::wstring(L(L"固定样本算例 · ",L"Fixed sample example · "))+result+L"\r\n"+
            L(L"权重用于本页算例。真实节点分数由测速 CLI 返回。",L"Weights drive this example. Live node scores are supplied by the probe CLI.");
    }
    SetDlgItemTextW(preferencesWindow,DemoExample,text.c_str());
}
void App::RefreshRegistered(int selection) {
    HWND list=GetDlgItem(preferencesWindow,RegisteredList);
    SendMessageW(list,LB_RESETCONTENT,0,0);
    for(const auto& target:draftTargets){
        std::wstring line=target.symbol+L" "+target.shortName+L" · "+L(L"待测",L"Untested")+L" · "+target.url;
        SendMessageW(list,LB_ADDSTRING,0,reinterpret_cast<LPARAM>(line.c_str()));
    }
    SendMessageW(list,LB_SETCURSEL,selection,0);
    const RegisteredTarget* target=selection>=0 && selection<static_cast<int>(draftTargets.size())?&draftTargets[selection]:nullptr;
    SetDlgItemTextW(preferencesWindow,TargetUrl,target?target->url.c_str():L"");
    SetDlgItemTextW(preferencesWindow,TargetShort,target?target->shortName.c_str():L"");
    SetDlgItemTextW(preferencesWindow,TargetSymbol,target?target->symbol.c_str():L"");
}
void App::ChangeRegistered(int action) {
    int index=static_cast<int>(SendDlgItemMessageW(preferencesWindow,RegisteredList,LB_GETCURSEL,0,0));
    bool hasSelection=index>=0 && index<static_cast<int>(draftTargets.size());
    auto setStatus=[&](const wchar_t* zh,const wchar_t* en){SetDlgItemTextW(preferencesWindow,TargetStatus,L(zh,en));};
    if(action!=AddTarget && !hasSelection){setStatus(L"请先选择一个目标。",L"Select a target first.");return;}
    if(action==RemoveTarget){draftTargets.erase(draftTargets.begin()+index);RefreshRegistered(-1);}
    else {
        RegisteredTarget target{Field(preferencesWindow,TargetUrl),Field(preferencesWindow,TargetShort),Field(preferencesWindow,TargetSymbol)};
        if(!ValidTarget(target)){setStatus(L"需要无凭证的绝对 HTTP(S) URL、简写和符号。",L"Enter an absolute credential-free HTTP(S) URL, short name and symbol.");return;}
        if(action==AddTarget){
            if(draftTargets.size()==16){setStatus(L"最多登记 16 个目标。",L"Up to 16 custom targets are allowed.");return;}
            draftTargets.push_back(target);
            index=static_cast<int>(draftTargets.size())-1;
        } else draftTargets[index]=target;
        RefreshRegistered(index);
    }
    setStatus(L"草稿已更新，按保存生效。登记目标保持待测。",L"Draft updated. Save to apply. Registered targets remain untested.");
}
void App::SizePreferences(int position) {
    int windowDpi=static_cast<int>(GetDpiForWindow(preferencesWindow));
    auto px=[&](int value){return MulDiv(value,windowDpi,96);};
    RECT client{};GetClientRect(preferencesWindow,&client);
    int extent=px(644);
    preferenceScroll=std::clamp(position,0,std::max(0,extent-static_cast<int>(client.bottom)));
    SCROLLINFO bar{sizeof bar,SIF_RANGE|SIF_PAGE|SIF_POS,0,extent-1,static_cast<UINT>(client.bottom),preferenceScroll,0};
    SetScrollInfo(preferencesWindow,SB_VERT,&bar,TRUE);
    GetClientRect(preferencesWindow,&client);
    int width=std::min(px(724),static_cast<int>(client.right));
    if(!preferencesFont)preferencesFont=CreateFontW(-px(15),0,0,0,400,0,0,0,DEFAULT_CHARSET,0,0,CLEARTYPE_QUALITY,0,L"Segoe UI");
    for(const auto& item:preferenceControls){
        SendMessageW(item.handle,WM_SETFONT,reinterpret_cast<WPARAM>(preferencesFont),TRUE);
        MoveWindow(item.handle,item.x*width/724,px(item.y)-preferenceScroll,item.width*width/724,px(item.height),TRUE);
    }
    InvalidateRect(preferencesWindow,nullptr,TRUE);
}
void App::ShowPreferences(bool show) {
    if(preferencesWindow){if(show)SetForegroundWindow(preferencesWindow);return;}
    WNDCLASSW cls{};cls.hInstance=instance;cls.lpfnWndProc=PreferencesProc;
    cls.lpszClassName=L"ProxyPartner.NativePreferences";cls.hCursor=LoadCursorW(nullptr,IDC_ARROW);
    RegisterClassW(&cls);
    RECT origin{};GetWindowRect(window,&origin);
    MONITORINFO monitor{sizeof monitor};GetMonitorInfoW(MonitorFromWindow(window,MONITOR_DEFAULTTONEAREST),&monitor);
    int width=std::min(Px(740),static_cast<int>(monitor.rcWork.right-monitor.rcWork.left));
    int height=std::min(Px(690),static_cast<int>(monitor.rcWork.bottom-monitor.rcWork.top));
    int x=std::clamp(static_cast<int>(origin.left)+Px(20),static_cast<int>(monitor.rcWork.left),static_cast<int>(monitor.rcWork.right)-width);
    int y=std::clamp(static_cast<int>(origin.top)+Px(20),static_cast<int>(monitor.rcWork.top),static_cast<int>(monitor.rcWork.bottom)-height);
    preferencesWindow=CreateWindowExW(WS_EX_CONTROLPARENT,cls.lpszClassName,L(L"评分算例与登记目标",L"Score demo / registered targets"),
        WS_CAPTION|WS_SYSMENU|WS_VSCROLL|WS_CLIPCHILDREN,x,y,width,height,window,nullptr,instance,nullptr);
    if(!preferencesWindow)return;
    draftTargets=prefs.customTargets;
    auto field=[&](const wchar_t* type,const std::wstring& text,int id,int left,int top,int w,int h,DWORD style=0){
        if(wcscmp(type,L"BUTTON")==0)style|=BS_OWNERDRAW|BS_NOTIFY;
        HWND handle=Child(preferencesWindow,type,text.c_str(),id,style);
        preferenceControls.push_back({handle,left,top,w,h});
        return handle;
    };
    field(L"STATIC",L(L"评分分项（%）· 仅算例",L"Score components (%) · example only"),0,20,18,684,22);
    field(L"STATIC",L(L"AI 工作目标（%）· 仅算例",L"AI work targets (%) · example only"),0,20,91,684,22);
    const wchar_t* labels[]={L(L"成功率",L"Success"),L(L"延迟",L"Latency"),L(L"稳定性",L"Stability"),L"OPENAI",L"GPT",L"GitHub"};
    for(int i=0;i<6;++i){
        int left=20+(i%3)*225,top=48+(i/3)*73;
        field(L"STATIC",labels[i],0,left,top,110,24);
        HWND edit=field(L"EDIT",std::to_wstring((i<3?prefs.components:prefs.targets)[i%3]),WeightFirst+i,left+116,top,65,26,WS_BORDER|WS_TABSTOP|ES_NUMBER);
        SendMessageW(edit,EM_SETLIMITTEXT,3,0);
    }
    field(L"STATIC",L"",DemoExample,20,172,684,75);
    field(L"STATIC",L(L"自定义目标 · 仅登记，待测",L"Custom targets · registration only, untested"),0,20,263,684,22);
    HWND list=field(L"LISTBOX",L"",RegisteredList,20,292,684,100,WS_BORDER|WS_VSCROLL|WS_HSCROLL|WS_TABSTOP|LBS_NOTIFY);
    SendMessageW(list,LB_SETHORIZONTALEXTENT,Px(1500),0);
    field(L"STATIC",L"HTTP(S) URL",0,20,407,460,22);
    field(L"STATIC",L(L"简写",L"Short name"),0,502,407,108,22);
    field(L"STATIC",L(L"符号",L"Symbol"),0,620,407,84,22);
    HWND url=field(L"EDIT",L"",TargetUrl,20,435,468,27,WS_BORDER|WS_TABSTOP|ES_AUTOHSCROLL);
    HWND shortName=field(L"EDIT",L"",TargetShort,502,435,104,27,WS_BORDER|WS_TABSTOP|ES_AUTOHSCROLL);
    HWND symbol=field(L"EDIT",L"",TargetSymbol,620,435,84,27,WS_BORDER|WS_TABSTOP|ES_AUTOHSCROLL);
    SendMessageW(url,EM_SETLIMITTEXT,2048,0);SendMessageW(shortName,EM_SETLIMITTEXT,12,0);SendMessageW(symbol,EM_SETLIMITTEXT,8,0);
    field(L"BUTTON",L(L"添加",L"Add"),AddTarget,20,480,90,29,WS_TABSTOP);
    field(L"BUTTON",L(L"修改所选",L"Update"),EditTarget,122,480,110,29,WS_TABSTOP);
    field(L"BUTTON",L(L"移除所选",L"Remove"),RemoveTarget,244,480,110,29,WS_TABSTOP);
    field(L"STATIC",L(L"添加、修改或移除后按保存。两组权重须各合计 100%。",L"Save after adding, updating or removing. Each weight group must total 100%."),TargetStatus,20,530,684,48);
    field(L"BUTTON",L(L"保存",L"Save"),SaveTargets,490,595,98,30,WS_TABSTOP);
    field(L"BUTTON",L(L"取消",L"Cancel"),CancelTargets,602,595,102,30,WS_TABSTOP);
    RefreshRegistered(-1);UpdateDemo();SizePreferences(0);
    BOOL dark=!prefs.light;DwmSetWindowAttribute(preferencesWindow,20,&dark,sizeof dark);
    if(show)ShowWindow(preferencesWindow,SW_SHOW);
}
LRESULT CALLBACK PreferencesProc(HWND window,UINT message,WPARAM w,LPARAM l) {
    if(message==WM_CLOSE){DestroyWindow(window);return 0;}
    if(message==WM_DESTROY){
        app.preferencesWindow=nullptr;
        if(app.preferencesFont)DeleteObject(app.preferencesFont);
        app.preferencesFont=nullptr;app.preferenceControls.clear();app.draftTargets.clear();app.preferenceScroll=0;
        return 0;
    }
    if(message==WM_SIZE && app.preferencesWindow && !app.preferenceControls.empty()){app.SizePreferences(app.preferenceScroll);return 0;}
    if(message==WM_DPICHANGED){
        if(app.preferencesFont)DeleteObject(app.preferencesFont);
        app.preferencesFont=nullptr;
        auto rect=reinterpret_cast<RECT*>(l);
        SetWindowPos(window,nullptr,rect->left,rect->top,rect->right-rect->left,rect->bottom-rect->top,SWP_NOZORDER|SWP_NOACTIVATE);
        app.SizePreferences(app.preferenceScroll);return 0;
    }
    if(message==WM_VSCROLL || message==WM_MOUSEWHEEL){
        RECT rect{};GetClientRect(window,&rect);
        SCROLLINFO info{sizeof info,SIF_TRACKPOS};GetScrollInfo(window,SB_VERT,&info);
        int position=app.preferenceScroll;
        if(message==WM_MOUSEWHEEL)position-=GET_WHEEL_DELTA_WPARAM(w)*60/WHEEL_DELTA;
        else switch(LOWORD(w)){
            case SB_LINEUP:position-=36;break;case SB_LINEDOWN:position+=36;break;
            case SB_PAGEUP:position-=rect.bottom;break;case SB_PAGEDOWN:position+=rect.bottom;break;
            case SB_THUMBTRACK:position=info.nTrackPos;break;case SB_TOP:position=0;break;case SB_BOTTOM:position=INT_MAX;break;
        }
        app.SizePreferences(position);return 0;
    }
    if(message==WM_COMMAND){
        int id=LOWORD(w),notification=HIWORD(w);
        if(id>=WeightFirst && id<WeightFirst+6 && notification==EN_CHANGE)app.UpdateDemo();
        if(id==RegisteredList && notification==LBN_SELCHANGE){
            int index=static_cast<int>(SendDlgItemMessageW(window,RegisteredList,LB_GETCURSEL,0,0));
            app.RefreshRegistered(index);
        }
        if(notification==BN_CLICKED){
            if(id==AddTarget || id==EditTarget || id==RemoveTarget)app.ChangeRegistered(id);
            if(id==CancelTargets || id==IDCANCEL){DestroyWindow(window);return 0;}
            if(id==SaveTargets){
                Preferences value=app.prefs;
                if(!app.ReadDemoWeights(value)){SetDlgItemTextW(window,TargetStatus,app.L(L"两组权重须各合计 100%，已有偏好未改动。",L"Each weight group must total 100%. Saved preferences were kept."));return 0;}
                value.customTargets=app.draftTargets;
                if(!app.SavePreferences(value)){SetDlgItemTextW(window,TargetStatus,app.L(L"无法保存本机偏好。",L"Could not save local preferences."));return 0;}
                app.prefs=std::move(value);DestroyWindow(window);
                if(app.details)app.ShowDetails();
                return 0;
            }
        } else if(l && (notification==EN_SETFOCUS || notification==BN_SETFOCUS || id==RegisteredList && notification==LBN_SETFOCUS)){
            RECT client{};GetClientRect(window,&client);
            int dpi=static_cast<int>(GetDpiForWindow(window));
            for(const auto& item:app.preferenceControls)if(item.handle==reinterpret_cast<HWND>(l)){
                int top=MulDiv(item.y-24,dpi,96),bottom=MulDiv(item.y+item.height+8,dpi,96);
                if(top<app.preferenceScroll)app.SizePreferences(top);
                else if(bottom>app.preferenceScroll+client.bottom)app.SizePreferences(bottom-client.bottom);
                break;
            }
        }
        return 0;
    }
    if(message==WM_DRAWITEM){DrawButton(*reinterpret_cast<DRAWITEMSTRUCT*>(l));return TRUE;}
    if(message==WM_CTLCOLORSTATIC || message==WM_CTLCOLOREDIT || message==WM_CTLCOLORLISTBOX){
        SetTextColor(reinterpret_cast<HDC>(w),app.Text());SetBkColor(reinterpret_cast<HDC>(w),app.Surface());
        return reinterpret_cast<LRESULT>(app.controlBrush);
    }
    if(message==WM_ERASEBKGND){RECT rect{};GetClientRect(window,&rect);Fill(reinterpret_cast<HDC>(w),rect,app.Surface());return 1;}
    return DefWindowProcW(window,message,w,l);
}
