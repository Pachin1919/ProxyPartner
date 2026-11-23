#define UNICODE
#define _UNICODE
#define NOMINMAX
#include <windows.h>
#include <windowsx.h>
#include <commctrl.h>
#include <commdlg.h>
#include <shellapi.h>
#include <shlobj.h>
#include <dwmapi.h>
#include <uxtheme.h>
#include <winhttp.h>
#include <algorithm>
#include <array>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>
#include <nlohmann/json.hpp>

using Json = nlohmann::json;
namespace fs = std::filesystem;
constexpr UINT Poll = 1, TrayMessage = WM_APP + 1;
constexpr int MaxNodes = 1000;
enum Control { Search = 101, View, Refresh, TestAll, Cancel, Settings, Details, List, Cards,
    TestSelected = 150,
    Profile = 201, BrowseProfile, Identity, BrowseIdentity, Rounds, Parallel, Deadline, English, Light, Save, CloseSettings,
    MinParallel = 230, MaxParallel, Kernel, PreferencesButton,
    WeightFirst = 400, RegisteredList = 410, TargetUrl, TargetShort, TargetSymbol, AddTarget, EditTarget, RemoveTarget,
    SaveTargets, CancelTargets, DemoExample, TargetStatus };
const wchar_t* SiteIds[] = { L"google", L"youtube", L"openai_api", L"chatgpt_web", L"anthropic_api" };
const wchar_t* SiteNames[] = { L"Google", L"YouTube", L"OpenAI API", L"ChatGPT", L"Anthropic API" };
const wchar_t* SiteShort[] = { L"GO", L"YT", L"OPENAI", L"GPT", L"ANT" };
std::wstring Wide(const std::string& value) {
    int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0);
    if (!count) return {};
    std::wstring result(count, 0);
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), result.data(), count);
    return result;
}
std::string Utf8(const std::wstring& value) {
    int count = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    std::string result(count, 0);
    WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), result.data(), count, nullptr, nullptr);
    return result;
}
std::wstring Quote(const std::wstring& value) {
    std::wstring result = L"\"";
    size_t slashes = 0;
    for (wchar_t c : value) {
        if (c == L'\\') { ++slashes;
            continue;
        }
        result.append(slashes * (c == L'"' ? 2 : 1), L'\\');
        slashes = 0;
        if (c == L'"') result += L'\\';
        result += c;
    }
    result.append(slashes * 2, L'\\');
    return result + L'"';
}
std::wstring Field(HWND parent, int id) {
    HWND control = GetDlgItem(parent, id);
    int count = GetWindowTextLengthW(control);
    std::wstring value(static_cast<size_t>(count) + 1, 0);
    GetWindowTextW(control, value.data(), count + 1);
    value.resize(count);
    return value;
}
bool LocalFile(const std::wstring& path) {
    if(path.size()<4 || path[1]!=L':' || path[2]!=L'\\' ||
    std::any_of(path.begin(),path.end(),[](wchar_t c){return c<L' ';}))return false;
    DWORD attributes=GetFileAttributesW(path.c_str());
    return attributes!=INVALID_FILE_ATTRIBUTES && !(attributes&FILE_ATTRIBUTE_DIRECTORY);
}
struct Site {
    int http = -1, latency = -1;
    bool attempted = false, timeout = false, active = false;
};
struct Node {
    std::wstring name, protocol, state;
    std::array<Site, 5> sites;
    int score = -1, stable = 0, round = 0, elapsed = -1, kernel = -1;
    bool probing = false;
    std::wstring observed, kernelObserved;
};
struct RegisteredTarget {
    std::wstring url, shortName, symbol;
};
struct Preferences {
    std::wstring profile, identity;
    int rounds = 1, parallel = 0, deadline = 0, minimum = 4, maximum = 8;
    bool english = false, light = false, kernel = true;
    std::array<int,3> components{50,35,15}, targets{60,30,10};
    std::vector<RegisteredTarget> customTargets;
};
#include "preferences.hpp"
struct App {
    HINSTANCE instance{};
    HWND window{}, status{}, search{}, table{}, cards{}, settings{}, details{}, detailText{}, tooltip{};
    HFONT font{}, bold{}, scoreFont{}, siteFont{};
    HBRUSH controlBrush{}, backgroundBrush{};
    HICON appIcon{}, icons[5]{};
    Preferences prefs;
    std::vector<Node> nodes;
    std::vector<int> visible;
    int selected = -1, sort = -1, scroll = 0, columns = 1, cardWidth = 0, cardHeight = 0;
    UINT dpi = 96;
    bool tableMode = false, descending = false, inspect = false, sawSummary = false, autoTest = false, filtering = false;
    int completed = 0, total = 0, scored = 0, attempts = 0;
    HANDLE process{}, pipe{}, job{};
    ULONGLONG started = 0, lastOutput = 0;
    std::string pending;
    std::wstring message, tipText;
    fs::path directory, preferencePath, standardProfile;
    HWND preferencesWindow{};
    HFONT preferencesFont{};
    struct PreferenceControl { HWND handle; int x,y,width,height; };
    std::vector<PreferenceControl> preferenceControls;
    std::vector<RegisteredTarget> draftTargets;
    int preferenceScroll=0;
    const wchar_t* L(const wchar_t* zh, const wchar_t* en) const { return prefs.english ? en : zh;
    }
    int Px(int value) const { return MulDiv(value, static_cast<int>(dpi), 96);
    }
    COLORREF Background() const { return prefs.light ? RGB(230,235,241) : RGB(34,41,51);
    }
    COLORREF Surface() const { return prefs.light ? RGB(248,250,252) : RGB(50,59,71);
    }
    COLORREF Text() const { return prefs.light ? RGB(32,43,59) : RGB(242,244,247);
    }
    COLORREF Muted() const { return prefs.light ? RGB(71,90,112) : RGB(195,206,219);
    }
    bool Running() const { return process != nullptr;
    }
    void Theme();
    void Layout();
    void Filter();
    void Update();
    void Finish(bool stop);
    void Start(bool catalog, const std::wstring& node = {});
    bool Event(const Json& event);
    void Drain();
    void PaintCards(HDC dc);
    void ShowSettings();
    void ShowDetails();
    void Load(fs::path native = {},fs::path legacy = {});
    bool SavePreferences(const Preferences& value);
    void ShowPreferences(bool visible = true);
    void SizePreferences(int position);
    void UpdateDemo();
    void RefreshRegistered(int selection);
    void ChangeRegistered(int action);
    bool ReadDemoWeights(Preferences& value);
} app;
std::wstring SiteText(const Site& site, bool english, bool full = false) {
    if (site.active) return english ? L"Testing" : L"测试中";
    if (!site.attempted) return english ? L"Untested" : L"待测";
    std::wstring timing = site.latency >= 0 ? std::to_wstring(site.latency) + L" ms" : L"—";
    if (site.http == 401) return full ? L"401 · " + std::wstring(english ? L"Auth response · " : L"鉴权响应 · ") + timing :
        site.latency>=0?timing:english?L"Auth":L"鉴权";
    if (site.http == 403) return full ? L"403 · " + std::wstring(english ? L"Restricted · " : L"服务受限 · ") + timing :
        site.latency>=0?timing:english?L"Blocked":L"受限";
    if (site.http >= 100) return full ? L"HTTP " + std::to_wstring(site.http) + L" · " + timing : timing;
    if (site.timeout) return english ? L"Timeout" : L"超时";
    return english ? L"Failed" : L"失败";
}
std::wstring CoreText(const Node& node,bool full = false) {
    std::wstring value=node.kernel>=0?std::to_wstring(node.kernel)+L" ms":app.L(L"无数据",L"No data");
    if(full && node.kernel>=0)value+=std::wstring(L" · ")+app.L(L"Mihomo 历史样本 · ",L"Mihomo history sample · ")+
        (node.kernelObserved.empty()?app.L(L"时间未知",L"Time unknown"):node.kernelObserved);
    return value;
}
COLORREF ScoreColor(int score) {
    if (score < 0) return app.Text();
    if (score < 60) return app.prefs.light ? RGB(159,37,62) : RGB(242,157,162);
    if (score < 80) return app.prefs.light ? RGB(120,82,13) : RGB(230,196,119);
    return app.prefs.light ? RGB(23,100,70) : RGB(121,199,157);
}
void Label(HDC dc, const std::wstring& value, RECT rect, HFONT face, COLORREF color, UINT flags = DT_LEFT | DT_END_ELLIPSIS | DT_SINGLELINE | DT_VCENTER) {
    HGDIOBJ previous = SelectObject(dc, face);
    SetTextColor(dc, color);
    SetBkMode(dc, TRANSPARENT);
    DrawTextW(dc, value.c_str(), static_cast<int>(value.size()), &rect, flags | DT_NOPREFIX);
    SelectObject(dc, previous);
}
void Fill(HDC dc, RECT rect, COLORREF color) {
    SetDCBrushColor(dc, color);
    FillRect(dc, &rect, static_cast<HBRUSH>(GetStockObject(DC_BRUSH)));
}
void DrawButton(const DRAWITEMSTRUCT& item) {
    wchar_t text[80]{};
    GetWindowTextW(item.hwndItem,text,80);
    bool primary=item.CtlID==TestAll || item.CtlID==Save || item.CtlID==SaveTargets;
    COLORREF ink=item.itemState&ODS_DISABLED?app.Muted():primary?RGB(247,249,255):app.Text();
    Fill(item.hDC,item.rcItem,item.itemState&ODS_FOCUS?RGB(121,164,239):RGB(100,118,142));
    RECT inside=item.rcItem;
    InflateRect(&inside,-1,-1);
    Fill(item.hDC,inside,primary?RGB(49,90,201):app.Surface());
    auto font=reinterpret_cast<HFONT>(SendMessageW(item.hwndItem,WM_GETFONT,0,0));
    Label(item.hDC,text,item.rcItem,font?font:app.font,ink,DT_CENTER|DT_VCENTER|DT_SINGLELINE);
}
void App::Theme() {
    if (font) DeleteObject(font);
    if (bold) DeleteObject(bold);
    if (scoreFont) DeleteObject(scoreFont);
    if (siteFont) DeleteObject(siteFont);
    font = CreateFontW(-Px(15),0,0,0,400,0,0,0,DEFAULT_CHARSET,0,0,CLEARTYPE_QUALITY,0,L"Segoe UI");
    bold = CreateFontW(-Px(16),0,0,0,600,0,0,0,DEFAULT_CHARSET,0,0,CLEARTYPE_QUALITY,0,L"Segoe UI");
    scoreFont = CreateFontW(-Px(29),0,0,0,600,0,0,0,DEFAULT_CHARSET,0,0,CLEARTYPE_QUALITY,0,L"Segoe UI");
    siteFont = CreateFontW(-Px(12),0,0,0,400,0,0,0,DEFAULT_CHARSET,0,0,CLEARTYPE_QUALITY,0,L"Segoe UI");
    if (controlBrush) DeleteObject(controlBrush);
    if (backgroundBrush) DeleteObject(backgroundBrush);
    controlBrush = CreateSolidBrush(Surface());
    backgroundBrush = CreateSolidBrush(Background());
    for (HWND child = GetWindow(window, GW_CHILD); child; child = GetWindow(child, GW_HWNDNEXT))
    SendMessageW(child, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
    ListView_SetBkColor(table, Background());
    ListView_SetTextBkColor(table, Background());
    ListView_SetTextColor(table, Text());
    BOOL dark = !prefs.light;
    DwmSetWindowAttribute(window, 20, &dark, sizeof dark);
    SetWindowTextW(window, L(L"ProxyPartner · 节点质量面板", L"ProxyPartner · Node quality"));
    const wchar_t* labels[] = {L(L"表格 / 卡片",L"Table / Cards"),L(L"刷新",L"Refresh"),L(L"全部测速",L"Test all"),
        L(L"取消",L"Cancel"),L(L"设置",L"Settings"),L(L"详情",L"Details")};
    for (int i = 0; i < 6; ++i) SetWindowTextW(GetDlgItem(window, View + i), labels[i]);
    SetWindowTextW(GetDlgItem(window,TestSelected),L(L"测所选",L"Test selected"));
    const wchar_t* headings[] = {L(L"节点",L"Node"),L(L"协议",L"Protocol"),L(L"评分",L"Score"),L"GO",L"YT",L"OPENAI",L"GPT",L"ANT",L(L"状态",L"State"),L(L"轮次",L"Round"),L(L"内核历史延迟",L"Core history latency")};
    for (int i = 0; i < 11; ++i) { LVCOLUMNW col{};
        col.mask = LVCF_TEXT;
        col.pszText = const_cast<wchar_t*>(headings[i]);
        ListView_SetColumn(table,i,&col);
    }
    InvalidateRect(window,nullptr,TRUE);
    InvalidateRect(cards,nullptr,TRUE);
}
void App::Layout() {
    RECT client{};
    GetClientRect(window,&client);
    int pad = Px(14), width = client.right - 2 * pad;
    bool compact=width<Px(1000);
    int top=compact?Px(161):Px(122);
    MoveWindow(status,pad,Px(43),width,Px(24),TRUE);
    MoveWindow(search,pad,Px(78),compact?width:std::max(Px(100),width-Px(770)),Px(29),TRUE);
    int x=compact?pad:std::max(pad+Px(110),static_cast<int>(client.right)-pad-Px(756));
    int slot=compact?std::max(1,(width-Px(36))/7):Px(102);
    int buttons[]={View,Refresh,TestAll,TestSelected,Cancel,Settings,Details};
    for(int id:buttons){MoveWindow(GetDlgItem(window,id),x,compact?Px(117):Px(78),slot,Px(29),TRUE);
        x+=slot+Px(6);
    }
    MoveWindow(table,pad,top,width,std::max(0,static_cast<int>(client.bottom)-top-pad),TRUE);
    MoveWindow(cards,pad,top,width,std::max(0,static_cast<int>(client.bottom)-top-pad),TRUE);
    ShowWindow(table,tableMode ? SW_SHOW : SW_HIDE);
    ShowWindow(cards,tableMode ? SW_HIDE : SW_SHOW);
    RECT area{};
    GetClientRect(cards,&area);
    columns = std::max(1,static_cast<int>(area.right)/Px(254));
    cardWidth = std::max(1,(static_cast<int>(area.right)-Px(20)*(columns-1))/columns);
    cardHeight = cardWidth*96/100;
    int extent = static_cast<int>((visible.size()+columns-1)/columns)*(cardHeight+Px(20));
    scroll = std::clamp(scroll,0,std::max(0,extent-static_cast<int>(area.bottom)));
    SCROLLINFO bar{sizeof bar,SIF_RANGE|SIF_PAGE|SIF_POS,0,std::max(0,extent-1),static_cast<UINT>(area.bottom),scroll,0};
    SetScrollInfo(cards,SB_VERT,&bar,TRUE);
    InvalidateRect(cards,nullptr,TRUE);
}
void App::Filter() {
    std::wstring query = Field(window,Search);
    visible.clear();
    for (int i = 0; i < static_cast<int>(nodes.size()); ++i)
    if (query.empty() || FindNLSStringEx(LOCALE_NAME_INVARIANT,FIND_FROMSTART|NORM_IGNORECASE,
    nodes[i].name.c_str(),-1,query.c_str(),-1,nullptr,nullptr,nullptr,0) >= 0) visible.push_back(i);
    if (sort >= 0) std::stable_sort(visible.begin(),visible.end(),[&](int a,int b) {
        int comparison=0;
        if(sort==2)comparison=nodes[a].score-nodes[b].score;
        else if(sort==1)comparison=nodes[a].protocol.compare(nodes[b].protocol);
        else if(sort>=3 && sort<=7)comparison=nodes[a].sites[sort-3].latency-nodes[b].sites[sort-3].latency;
        else if(sort==8)comparison=nodes[a].state.compare(nodes[b].state);
        else if(sort==9)comparison=nodes[a].round-nodes[b].round;
        else if(sort==10)comparison=nodes[a].kernel-nodes[b].kernel;
        else comparison=nodes[a].name.compare(nodes[b].name);
        return descending ? comparison > 0 : comparison < 0;
    });
    filtering=true;
    ListView_SetItemCountEx(table,static_cast<int>(visible.size()),LVSICF_NOSCROLL);
    ListView_SetItemState(table,-1,0,LVIS_SELECTED|LVIS_FOCUSED);
    auto selection=std::find(visible.begin(),visible.end(),selected);
    if(selection!=visible.end())ListView_SetItemState(table,static_cast<int>(selection-visible.begin()),LVIS_SELECTED|LVIS_FOCUSED,LVIS_SELECTED|LVIS_FOCUSED);
    filtering=false;
    Layout();
}
void App::Update() {
    std::wstring line = Running() ? inspect ? L(L"读取节点中",L"Reading nodes") : L(L"测速中",L"Testing") : message;
    line += L"  |  " + std::to_wstring(visible.size()) + L(L" 个节点",L" nodes");
    if (!inspect && total) line += L"  |  " + std::to_wstring(completed) + L"/" + std::to_wstring(total) +
    L"  |  " + L(L"已评分 ",L"Scored ") + std::to_wstring(scored);
    SetWindowTextW(status,line.c_str());
    for (int id : {Refresh,TestAll,TestSelected,Settings}) EnableWindow(GetDlgItem(window,id),!Running());
    EnableWindow(GetDlgItem(window,Cancel),Running());
    InvalidateRect(cards,nullptr,FALSE);
    InvalidateRect(table,nullptr,FALSE);
}
void App::Load(fs::path native,fs::path legacy) {
    PWSTR local = nullptr;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData,0,nullptr,&local))) {
        preferencePath = fs::path(local)/L"ProxyPartner"/L"NativeDesktop"/L"preferences.json";
        CoTaskMemFree(local);
    }
    if(!native.empty())preferencePath=native;
    if(legacy.empty())legacy=preferencePath.parent_path().parent_path()/L"Desktop"/L"settings.json";
    std::error_code existsError;
    bool hasNative=fs::exists(preferencePath,existsError);
    if(!LoadPreferenceFile(preferencePath,prefs,false) && !hasNative && !existsError)
        LoadPreferenceFile(legacy,prefs,true);
    wchar_t data[32768]{};
    if (GetEnvironmentVariableW(L"APPDATA",data,32768)) {
        standardProfile=fs::path(data)/L"io.github.clash-verge-rev.clash-verge-rev"/L"clash-verge.yaml";
        if(prefs.profile.empty() && LocalFile(standardProfile.wstring()))prefs.profile=standardProfile.wstring();
    }
}
bool App::SavePreferences(const Preferences& value) {
    std::error_code error;
    fs::create_directories(preferencePath.parent_path(),error);
    if (error) return false;
    std::string data=EncodePreferences(value).dump();
    if(data.size()>65536)return false;
    fs::path temporary=preferencePath;
    temporary+=L".tmp";
    std::ofstream output(temporary,std::ios::binary|std::ios::trunc);
    output << data;
    output.close();
    return output.good() && MoveFileExW(temporary.c_str(),preferencePath.c_str(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH);
}

// Accept only the CLI's public event fields. Unknown/error payloads never reach the UI verbatim.
bool App::Event(const Json& value) {
    if (!value.is_object() || !value.contains("event") || !value["event"].is_string()) return false;
    auto string = [&](const Json& object,const char* key) -> std::wstring {
        auto field=object.find(key);
        return field != object.end() && field->is_string() && field->get_ref<const std::string&>().size()<=16384 ? Wide(field->get<std::string>()) : std::wstring{};
    };
    auto integer = [](const Json& object,const char* key,int fallback,int maximum) {
        auto field=object.find(key);
        return field != object.end() && field->is_number_integer() && *field>=0 && *field<=maximum ? field->get<int>() : fallback;
    };
    auto boolean = [](const Json& object,const char* key) { auto field=object.find(key);
        return field!=object.end() && field->is_boolean() && field->get<bool>();
    };
    std::wstring kind=string(value,"event");
    if (kind==L"catalog") { total=integer(value,"nodes",0,MaxNodes);
        return total>0;
    }
    if (kind==L"summary") {
        completed=integer(value,"completed",0,MaxNodes);
        scored=integer(value,"scored",0,MaxNodes);
        attempts=integer(value,"attempts",0,MaxNodes*30);
        sawSummary=true;
        return true;
    }
    if (kind==L"error") { message=L(L"CLI 无法完成操作，请检查配置与身份文件",L"CLI could not complete. Check profile and identity files");
        return true;
    }
    if (kind==L"kernel") return true;
    if (kind!=L"node" && kind!=L"round") return false;
    std::wstring name=string(value,"node");
    if (name.empty() || name.size()>4096 || name.find_first_of(L"\r\n\t")!=std::wstring::npos) return false;
    auto found=std::find_if(nodes.begin(),nodes.end(),[&](const Node& node){return node.name==name;});
    if (found==nodes.end()) {
        if (nodes.size()==MaxNodes) return false;
        Node node;
        node.name=name;
        nodes.push_back(node);
        found=nodes.end()-1;
    }
    auto& node=*found;
    std::wstring protocol=string(value,"protocol");
    if (!protocol.empty()) node.protocol=protocol;
    if (kind==L"node") {
        node.state=string(value,"state");
        node.probing=node.state==L"probing";
        for (auto& site:node.sites) site.active=node.probing;
    } else if (kind==L"round") {
        if (!value.contains("sites") || !value["sites"].is_array() || value["sites"].size()>5) return false;
        node.score=integer(value,"score",-1,100);
        node.stable=integer(value,"stableTargets",0,5);
        node.round=integer(value,"round",0,6);
        node.elapsed=integer(value,"nodeElapsedMs",-1,3600000);
        node.kernel=integer(value,"kernelLatencyMs",-1,3600000);
        node.kernelObserved=string(value,"kernelObservedUtc");
        for (auto& site:node.sites) site.active=false;
        for (const auto& sample:value["sites"]) {
            if (!sample.is_object()) return false;
            std::wstring target=string(sample,"target");
            for (int i=0;i<5;++i) if (target==SiteIds[i]) {
                node.sites[i]={integer(sample,"httpStatus",-1,599),integer(sample,"latencyMs",-1,3600000),
                    boolean(sample,"attempted"),boolean(sample,"timedOut"),false};
            }
        }
        node.probing=false;
        node.state=L"complete";
        SYSTEMTIME now{};
        GetSystemTime(&now);
        wchar_t stamp[64]{};
        swprintf_s(stamp,L"%02u-%02u %02u:%02u:%02u UTC",now.wMonth,now.wDay,now.wHour,now.wMinute,now.wSecond);
        node.observed=stamp;
        completed=static_cast<int>(std::count_if(nodes.begin(),nodes.end(),[&](const Node& n){return n.round>=prefs.rounds;}));
        scored=static_cast<int>(std::count_if(nodes.begin(),nodes.end(),[](const Node& n){return n.score>=0;}));
    }
    return true;
}
void App::Finish(bool stop) {
    KillTimer(window,Poll);
    if (job && stop) TerminateJobObject(job,130);
    if (process) { WaitForSingleObject(process,5000);
        CloseHandle(process);
    }
    if (job) CloseHandle(job);
    if (pipe) CloseHandle(pipe);
    process=pipe=job=nullptr;
    pending.clear();
    for (auto& node:nodes) { node.probing=false;
        for(auto& site:node.sites) site.active=false;
    }
    Update();
}
void App::Start(bool catalog,const std::wstring& node) {
    if (Running()) return;
    if (!LocalFile(prefs.profile) || !prefs.identity.empty() && !LocalFile(prefs.identity)) { ShowSettings();
        return;
    }
    fs::path executable=directory/L"ProxyPartner.Cli.exe";
    if (GetFileAttributesW(executable.c_str())==INVALID_FILE_ATTRIBUTES) {
        message=L(L"缺少同目录 ProxyPartner.Cli.exe",L"ProxyPartner.Cli.exe is missing beside the desktop");
        Update();
        return;
    }
    std::wstring args=Quote(executable.wstring())+(catalog?L" inspect --profile ":L" probe --profile ")+Quote(prefs.profile);
    std::wstring identity=prefs.identity;
    if(identity.empty() && !standardProfile.empty()){
        auto profile=fs::path(prefs.profile).lexically_normal().make_preferred();
        auto standard=standardProfile.lexically_normal().make_preferred();
        auto key=standard.parent_path()/L".runtime_key";
        if(_wcsicmp(profile.c_str(),standard.c_str())==0 && LocalFile(key.wstring()))identity=key.wstring();
    }
    if (!identity.empty()) args+=L" --age-identity "+Quote(identity);
    if (!catalog) {
        args+=(node.empty()?L" --all":L" --node "+Quote(node))+L" --rounds "+std::to_wstring(prefs.rounds);
        args+=prefs.parallel?L" --parallel "+std::to_wstring(prefs.parallel):
        L" --min-parallel "+std::to_wstring(prefs.minimum)+L" --max-parallel "+std::to_wstring(prefs.maximum);
        if(prefs.deadline)args+=L" --node-timeout-ms "+std::to_wstring(prefs.deadline);
        if(prefs.kernel)args+=L" --kernel-latency";
    }
    SECURITY_ATTRIBUTES security{sizeof security,nullptr,TRUE};
    HANDLE write=nullptr;
    if (!CreatePipe(&pipe,&write,&security,0)) { message=L(L"无法创建结果管道",L"Cannot create result pipe");
        Update();
        return;
    }
    SetHandleInformation(pipe,HANDLE_FLAG_INHERIT,0);
    HANDLE nullFile=CreateFileW(L"NUL",GENERIC_READ|GENERIC_WRITE,FILE_SHARE_READ|FILE_SHARE_WRITE,&security,OPEN_EXISTING,0,nullptr);
    job=CreateJobObjectW(nullptr,nullptr);
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limit{};
    limit.BasicLimitInformation.LimitFlags=JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    bool jobReady=job && SetInformationJobObject(job,JobObjectExtendedLimitInformation,&limit,sizeof limit);
    STARTUPINFOEXW startup{};
    startup.StartupInfo.cb=sizeof startup;
    startup.StartupInfo.dwFlags=STARTF_USESTDHANDLES;
    startup.StartupInfo.hStdOutput=write;
    startup.StartupInfo.hStdError=nullFile;
    startup.StartupInfo.hStdInput=nullFile;
    SIZE_T bytes=0;
    InitializeProcThreadAttributeList(nullptr,1,0,&bytes);
    std::vector<unsigned char> attributes(bytes);
    startup.lpAttributeList=reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attributes.data());
    bool initialized=InitializeProcThreadAttributeList(startup.lpAttributeList,1,0,&bytes)!=FALSE;
    HANDLE inherited[]={write,nullFile};
    bool inherit=initialized && UpdateProcThreadAttribute(startup.lpAttributeList,0,PROC_THREAD_ATTRIBUTE_HANDLE_LIST,inherited,sizeof inherited,nullptr,nullptr);
    PROCESS_INFORMATION child{};
    bool created=jobReady && inherit && nullFile!=INVALID_HANDLE_VALUE && CreateProcessW(executable.c_str(),args.data(),nullptr,nullptr,TRUE,
    CREATE_NO_WINDOW|CREATE_SUSPENDED|EXTENDED_STARTUPINFO_PRESENT,nullptr,directory.c_str(),&startup.StartupInfo,&child);
    if (initialized) DeleteProcThreadAttributeList(startup.lpAttributeList);
    CloseHandle(write);
    if(nullFile!=INVALID_HANDLE_VALUE) CloseHandle(nullFile);
    if (!created) { message=L(L"无法启动测速程序",L"Cannot start CLI");
        Finish(true);
        return;
    }
    process=child.hProcess;
    if (!AssignProcessToJobObject(job,process)) {
        TerminateProcess(process,130);
        CloseHandle(child.hThread);
        message=L(L"无法隔离测速进程",L"Cannot isolate CLI process");
        Finish(true);
        return;
    }
    inspect=catalog;
    sawSummary=false;
    completed=scored=attempts=0;
    pending.clear();
    message.clear();
    if (catalog) { nodes.clear();
        visible.clear();
        selected=-1;
        total=0;
    }
    else for(auto& entry:nodes) if(node.empty() || node==entry.name) entry.state=L"queued";
    started=lastOutput=GetTickCount64();
    ResumeThread(child.hThread);
    CloseHandle(child.hThread);
    SetTimer(window,Poll,100,nullptr);
    Filter();
    Update();
}
void App::Drain() {
    if (!Running()) return;
    char buffer[8192];
    DWORD available=0,count=0;
    bool changed=false;
    for (int batch=0;batch<128 && PeekNamedPipe(pipe,nullptr,0,nullptr,&available,nullptr) && available;++batch) {
        if (!ReadFile(pipe,buffer,std::min<DWORD>(available,sizeof buffer),&count,nullptr) || !count) break;
        pending.append(buffer,count);
        lastOutput=GetTickCount64();
        size_t end=0;
        while ((end=pending.find('\n'))!=std::string::npos) {
            Json value=Json::parse(pending.begin(),pending.begin()+end,nullptr,false);
            bool valid=!value.is_discarded() && Event(value);
            pending.erase(0,end+1);
            if (!valid) { message=L(L"测速结果协议无效",L"Invalid CLI event");
                Finish(true);
                return;
            }
            changed=true;
        }
        if (pending.size()>1024*1024) { message=L(L"测速结果超出上限",L"CLI output exceeded limit");
            Finish(true);
            return;
        }
    }
    if (changed) { Filter();
        Update();
        if(details) ShowDetails();
    }
    if (WaitForSingleObject(process,0)==WAIT_OBJECT_0) {
        // Drain on the next tick if bytes are still buffered after process exit.
        if (PeekNamedPipe(pipe,nullptr,0,nullptr,&available,nullptr) && available) return;
        DWORD code=0;
        GetExitCodeProcess(process,&code);
        if (!pending.empty()) message=L(L"测速结果不完整",L"Incomplete CLI output");
        else if (inspect && code==0) message=L(L"节点已读取",L"Nodes loaded");
        else if (!inspect && sawSummary) message=code==0?L(L"测速完成",L"Test completed"):L(L"测速结束，含失败或不可用节点",L"Test ended with failed or unavailable nodes");
        else if (message.empty()) message=L(L"测速程序异常结束",L"CLI ended without a summary");
        bool startSweep=inspect && code==0 && autoTest && !nodes.empty();
        Finish(false);
        if(startSweep)Start(false);
    } else if (GetTickCount64()-lastOutput>120000) {
        message=L(L"测速无进度，已取消",L"No progress. Test cancelled");
        Finish(true);
    }
}

void App::PaintCards(HDC dc) {
    RECT area{};
    GetClientRect(cards,&area);
    Fill(dc,area,Background());
    int pitch=cardHeight+Px(20);
    if(pitch<=0) return;
    for(int i=std::max(0,scroll/pitch*columns);i<static_cast<int>(visible.size());++i) {
        int x=i%columns*(cardWidth+Px(20)),y=i/columns*pitch-scroll;
        if(y>=area.bottom) break;
        auto& node=nodes[visible[i]];
        RECT bounds{x,y,x+cardWidth,y+cardHeight};
        SelectObject(dc,GetStockObject(DC_BRUSH));
        SelectObject(dc,GetStockObject(DC_PEN));
        SetDCBrushColor(dc,Surface());
        SetDCPenColor(dc,visible[i]==selected?RGB(70,123,223):prefs.light?RGB(205,215,226):RGB(73,85,100));
        RoundRect(dc,bounds.left,bounds.top,bounds.right,bounds.bottom,Px(14),Px(14));
        int left=x+Px(17),right=x+cardWidth-Px(17);
        auto at=[&](int percent){return y+cardHeight*percent/100;
        };
        Label(dc,node.name,{left,at(8),right,at(8)+Px(25)},bold,Text());
        std::wstring headline=node.score>=0?L"S="+std::to_wstring(node.score):L"—";
        Label(dc,headline,{left,at(23),right,at(23)+Px(39)},scoreFont,ScoreColor(node.score));
        std::wstring state=node.probing?L(L"测速中",L"Testing"):node.state==L"queued"?L(L"排队中",L"Queued"):
        node.round?L(L"即时评分 · 稳定性样本 ",L"Initial score · stability ")+std::to_wstring(node.stable)+L"/5":
        node.state==L"ready"?L(L"待测速",L"Untested"):L(L"协议或配置不可用",L"Protocol / configuration unavailable");
        Label(dc,state,{left,at(40),right,at(40)+Px(19)},font,Muted());
        for(int site=0;site<5;++site) {
            int first=left+(right-left)*site/5,last=left+(right-left)*(site+1)/5;
            int size=Px(19),ix=(first+last-size)/2;
            if(icons[site]) DrawIconEx(dc,ix,at(54),icons[site],size,size,0,nullptr,DI_NORMAL);
            Label(dc,SiteShort[site],{first,at(65),last,at(65)+Px(17)},siteFont,Muted(),DT_CENTER|DT_SINGLELINE|DT_END_ELLIPSIS|DT_VCENTER);
            auto& result=node.sites[site];
            COLORREF ink=result.http==403?RGB(210,165,90):result.timeout || result.attempted && result.http<100?ScoreColor(0):Text();
            Label(dc,SiteText(result,prefs.english),{first,at(75),last,at(75)+Px(19)},siteFont,ink,DT_CENTER|DT_SINGLELINE|DT_END_ELLIPSIS|DT_VCENTER);
        }
        std::wstring core=std::wstring(L(L"内核 ",L"Core "))+CoreText(node);
        Label(dc,core,{left,at(86),right,at(86)+Px(17)},siteFont,Muted());
        Label(dc,node.protocol,{left,at(93),right,at(93)+Px(14)},siteFont,Muted());
    }
}

HWND Child(HWND parent,const wchar_t* type,const wchar_t* text,int id,DWORD style=0) {
    HWND result=CreateWindowExW(0,type,text,WS_CHILD|WS_VISIBLE|style,0,0,0,0,parent,reinterpret_cast<HMENU>(static_cast<INT_PTR>(id)),app.instance,nullptr);
    SendMessageW(result,WM_SETFONT,reinterpret_cast<WPARAM>(app.font),TRUE);
    return result;
}
#include "settings.hpp"
void App::ShowDetails() {
    if(selected<0 || selected>=static_cast<int>(nodes.size())) return;
    if(!details) {
        details=CreateWindowExW(0,L"ProxyPartner.NativeAux",L(L"节点详情",L"Node details"),WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT,CW_USEDEFAULT,Px(740),Px(560),window,nullptr,instance,nullptr);
        detailText=Child(details,L"EDIT",L"",1,ES_MULTILINE|ES_READONLY|ES_AUTOVSCROLL|WS_VSCROLL);
        RECT rect{};
        GetClientRect(details,&rect);
        MoveWindow(detailText,10,10,rect.right-20,rect.bottom-20,TRUE);
    }
    auto& node=nodes[selected];
    std::wstring report=node.name+L"\r\n"+node.protocol+L"\r\n"+L(L"来源：独立节点 CLI 测速",L"Source: independent node CLI probe")+
    L"\r\n"+L(L"评分 ",L"Score ")+(node.score>=0?std::to_wstring(node.score):L"—")+L"  |  "+L(L"轮次 ",L"Round ")+std::to_wstring(node.round)+L"\r\n";
    for(int i=0;i<5;++i) report+=std::wstring(SiteNames[i])+L" · "+SiteText(node.sites[i],prefs.english,true)+L"\r\n";
    for(const auto& target:prefs.customTargets)report+=target.symbol+L" "+target.shortName+L" · "+L(L"登记目标，待测",L"Registered target, untested")+L" · "+target.url+L"\r\n";
    report+=std::wstring(L(L"内核 ",L"Core "))+CoreText(node,true)+L"\r\n";
    report+=std::wstring(L"\r\n")+L(L"结果接收时间 ",L"Result received ")+node.observed+L"\r\n"+L(L"401 表示已到达鉴权，403 表示服务受限。耗时与评分不能证明登录或模型业务可用。\r\n首轮即时评分尚无跨轮稳定性依据。",L"401 reached authentication. 403 indicates restriction. Timing and score do not verify login or model access.\r\nThe first round has no cross-round stability evidence.");
    SetWindowTextW(detailText,report.c_str());
    ShowWindow(details,SW_SHOW);
}
LRESULT CALLBACK AuxProc(HWND hwnd,UINT msg,WPARAM w,LPARAM l) {
    if(msg==WM_SIZE && hwnd==app.details && app.detailText) { RECT r{};
        GetClientRect(hwnd,&r);
        MoveWindow(app.detailText,10,10,r.right-20,r.bottom-20,TRUE);
        return 0;
    }
    if(msg==WM_CLOSE){DestroyWindow(hwnd);
        return 0;
    }
    if(msg==WM_DESTROY){if(hwnd==app.settings)app.settings=nullptr;
        else app.details=app.detailText=nullptr;
        return 0;
    }
    if(msg==WM_COMMAND && hwnd==app.settings) {
        int id=LOWORD(w);
        if(id==BrowseProfile || id==BrowseIdentity) {
            wchar_t file[32768]{};
            OPENFILENAMEW dialog{sizeof dialog};
            dialog.hwndOwner=hwnd;
            dialog.lpstrFile=file;
            dialog.nMaxFile=32768;
            dialog.Flags=OFN_FILEMUSTEXIST|OFN_PATHMUSTEXIST|OFN_NOCHANGEDIR;
            dialog.lpstrFilter=id==BrowseProfile?L"Profiles\0*.yaml;*.yml;*.age\0All files\0*.*\0":L"Identity files\0*.*\0";
            if(GetOpenFileNameW(&dialog))SetDlgItemTextW(hwnd,id==BrowseProfile?Profile:Identity,file);
        }
        if(id==CloseSettings){DestroyWindow(hwnd);
            return 0;
        }
        if(id==PreferencesButton){app.ShowPreferences();return 0;}
        if(id==Save) {
            Preferences value=app.prefs;
            value.profile=Field(hwnd,Profile);
            value.identity=Field(hwnd,Identity);
            auto number=[&](int control,int maximum,int minimum=1) { auto text=Field(hwnd,control);
                wchar_t* end=nullptr;
                long n=wcstol(text.c_str(),&end,10);
                return !text.empty() && *end==0 && n>=minimum && n<=maximum?static_cast<int>(n):-1;
            };
            value.rounds=number(Rounds,6);
            value.parallel=number(Parallel,12,0);
            value.deadline=number(Deadline,6000,0);
            value.minimum=number(MinParallel,12);
            value.maximum=number(MaxParallel,12);
            value.kernel=IsDlgButtonChecked(hwnd,Kernel)==BST_CHECKED;
            value.english=IsDlgButtonChecked(hwnd,English)==BST_CHECKED;
            value.light=IsDlgButtonChecked(hwnd,Light)==BST_CHECKED;
            if(value.rounds<1 || value.parallel<0 || value.deadline<0 || value.minimum<1 || value.maximum<value.minimum ||
            !LocalFile(value.profile) || !value.identity.empty() && !LocalFile(value.identity)) {
                MessageBoxW(hwnd,app.L(L"请选择存在的本机文件。轮次 1–6，并发上下限 1–12，固定并发与超时填 0 使用自适应。",L"Choose local files. Rounds 1–6, parallel limits 1–12. Set fixed parallel and timeout to 0 for adaptive mode."),L"ProxyPartner",MB_ICONWARNING);
                return 0;
            }
            if(!app.SavePreferences(value)){MessageBoxW(hwnd,app.L(L"无法保存偏好",L"Cannot save preferences"),L"ProxyPartner",MB_ICONERROR);
                return 0;
            }
            bool changed=value.profile!=app.prefs.profile || value.identity!=app.prefs.identity;
            app.prefs=value;
            DestroyWindow(hwnd);
            app.Theme();
            app.Filter();
            app.Update();
            if(changed)app.Start(true);
        }
    }
    if(msg==WM_DRAWITEM){DrawButton(*reinterpret_cast<DRAWITEMSTRUCT*>(l));
        return TRUE;
    }
    if(msg==WM_CTLCOLORSTATIC || msg==WM_CTLCOLOREDIT || msg==WM_CTLCOLORBTN){SetTextColor(reinterpret_cast<HDC>(w),app.Text());
        SetBkColor(reinterpret_cast<HDC>(w),app.Surface());
        return reinterpret_cast<LRESULT>(app.controlBrush);
    }
    if(msg==WM_ERASEBKGND){RECT rect{};
        GetClientRect(hwnd,&rect);
        Fill(reinterpret_cast<HDC>(w),rect,app.Surface());
        return 1;
    }
    return DefWindowProcW(hwnd,msg,w,l);
}
void App::ShowSettings() {
    if(settings){SetForegroundWindow(settings);
        return;
    }
    settings=CreateWindowExW(WS_EX_CONTROLPARENT,L"ProxyPartner.NativeAux",L(L"设置",L"Settings"),WS_CAPTION|WS_SYSMENU,
    CW_USEDEFAULT,CW_USEDEFAULT,Px(690),Px(462),window,nullptr,instance,nullptr);
    auto field=[&](const wchar_t* type,const std::wstring& text,int id,int x,int y,int width,int height,DWORD style=0){
        if(wcscmp(type,L"BUTTON")==0 && (style&0xF)==BS_PUSHBUTTON)style|=BS_OWNERDRAW;
        HWND h=Child(settings,type,text.c_str(),id,style);
        MoveWindow(h,Px(x),Px(y),Px(width),Px(height),TRUE);
        return h;
    };
    field(L"STATIC",L(L"本机节点配置（只读）",L"Local node profile (read only)"),0,18,15,620,22);
    field(L"EDIT",prefs.profile,Profile,18,42,542,27,WS_BORDER|WS_TABSTOP|ES_AUTOHSCROLL);
    field(L"BUTTON",L"…",BrowseProfile,570,42,70,27,WS_TABSTOP);
    field(L"STATIC",L(L"AGE 身份文件（按需）",L"AGE identity file (optional)"),0,18,83,620,22);
    field(L"EDIT",prefs.identity,Identity,18,110,542,27,WS_BORDER|WS_TABSTOP|ES_AUTOHSCROLL);
    field(L"BUTTON",L"…",BrowseIdentity,570,110,70,27,WS_TABSTOP);
    const wchar_t* labels[]={L(L"轮次 1–6",L"Rounds 1–6"),L(L"固定并发（0 自动）",L"Fixed parallel (0 auto)"),L(L"超时 ms（0 自动）",L"Timeout ms (0 auto)")};
    int values[]={prefs.rounds,prefs.parallel,prefs.deadline};
    for(int i=0;i<3;++i){field(L"STATIC",labels[i],0,18+i*215,158,200,22);
        field(L"EDIT",std::to_wstring(values[i]),Rounds+i,18+i*215,186,185,27,WS_BORDER|WS_TABSTOP|ES_NUMBER);
    }
    field(L"STATIC",L(L"自动并发下限 / 上限",L"Adaptive parallel min / max"),0,18,230,245,22);
    field(L"EDIT",std::to_wstring(prefs.minimum),MinParallel,275,230,60,25,WS_BORDER|WS_TABSTOP|ES_NUMBER);
    field(L"EDIT",std::to_wstring(prefs.maximum),MaxParallel,345,230,60,25,WS_BORDER|WS_TABSTOP|ES_NUMBER);
    field(L"BUTTON",L(L"读内核样本",L"Read core samples"),Kernel,430,230,210,27,BS_AUTOCHECKBOX|WS_TABSTOP);
    field(L"BUTTON",L"English",English,18,278,180,27,BS_AUTOCHECKBOX|WS_TABSTOP);
    field(L"BUTTON",L(L"浅色主题",L"Light theme"),Light,235,278,180,27,BS_AUTOCHECKBOX|WS_TABSTOP);
    CheckDlgButton(settings,Kernel,prefs.kernel?BST_CHECKED:BST_UNCHECKED);
    CheckDlgButton(settings,English,prefs.english?BST_CHECKED:BST_UNCHECKED);
    CheckDlgButton(settings,Light,prefs.light?BST_CHECKED:BST_UNCHECKED);
    if(!prefs.light)for(int id:{English,Light,Kernel})SetWindowTheme(GetDlgItem(settings,id),L"",L"");
    field(L"STATIC",L(L"路径只保存在本机偏好中；测速由独立 CLI 完成。",L"Paths stay in local preferences. An independent CLI runs probes."),0,18,325,620,22);
    field(L"BUTTON",L(L"保存",L"Save"),Save,430,370,100,29,WS_TABSTOP);
    field(L"BUTTON",L(L"取消",L"Cancel"),CloseSettings,540,370,100,29,WS_TABSTOP);
    field(L"BUTTON",L(L"评分算例与登记目标",L"Score demo / registered targets"),PreferencesButton,18,370,350,29,WS_TABSTOP);
    ShowWindow(settings,SW_SHOW);
    SetFocus(GetDlgItem(settings,Profile));
}

LRESULT CALLBACK CardsProc(HWND hwnd,UINT msg,WPARAM w,LPARAM l) {
    if(msg==WM_GETDLGCODE)return DLGC_WANTARROWS|DLGC_WANTCHARS|(w==VK_RETURN?DLGC_WANTMESSAGE:0);
    if(msg==WM_PAINT){PAINTSTRUCT paint{};
        HDC dc=BeginPaint(hwnd,&paint);
        app.PaintCards(dc);
        EndPaint(hwnd,&paint);
        return 0;
    }
    if(msg==WM_ERASEBKGND)return 1;
    if(msg==WM_VSCROLL || msg==WM_MOUSEWHEEL) {
        RECT area{};
        GetClientRect(hwnd,&area);
        SCROLLINFO info{sizeof info,SIF_TRACKPOS};
        GetScrollInfo(hwnd,SB_VERT,&info);
        if(msg==WM_MOUSEWHEEL)app.scroll-=GET_WHEEL_DELTA_WPARAM(w)*app.Px(65)/WHEEL_DELTA;
        else switch(LOWORD(w)) {
            case SB_LINEUP:app.scroll-=app.Px(40);
            break;
            case SB_LINEDOWN:app.scroll+=app.Px(40);
            break;
            case SB_PAGEUP:app.scroll-=area.bottom;
            break;
            case SB_PAGEDOWN:app.scroll+=area.bottom;
            break;
            case SB_THUMBTRACK:app.scroll=info.nTrackPos;
            break;
            case SB_TOP:app.scroll=0;
            break;
            case SB_BOTTOM:app.scroll=INT_MAX;
            break;
        }
        app.Layout();
        return 0;
    }
    if(msg==WM_LBUTTONDOWN || msg==WM_LBUTTONDBLCLK) {
        int index=(GET_Y_LPARAM(l)+app.scroll)/(app.cardHeight+app.Px(20))*app.columns+GET_X_LPARAM(l)/(app.cardWidth+app.Px(20));
        if(index>=0 && index<static_cast<int>(app.visible.size())){app.selected=app.visible[index];
            InvalidateRect(hwnd,nullptr,FALSE);
            if(msg==WM_LBUTTONDBLCLK)app.ShowDetails();
        }
        SetFocus(hwnd);
        return 0;
    }
    if(msg==WM_KEYDOWN){
        auto found=std::find(app.visible.begin(),app.visible.end(),app.selected);
        int i=static_cast<int>(found-app.visible.begin());
        if(w==VK_HOME)i=0;
        else if(w==VK_END)i=static_cast<int>(app.visible.size())-1;
        else if(w==VK_RIGHT)++i;
        else if(w==VK_LEFT)--i;
        else if(w==VK_DOWN)i+=app.columns;
        else if(w==VK_UP)i-=app.columns;
        else if(w==VK_RETURN){app.ShowDetails();
            return 0;
        }else return DefWindowProcW(hwnd,msg,w,l);
        if(!app.visible.empty()){i=std::clamp(i,0,static_cast<int>(app.visible.size())-1);
            app.selected=app.visible[i];
            app.scroll=i/app.columns*(app.cardHeight+app.Px(20));
            app.Layout();
        }return 0;
    }
    return DefWindowProcW(hwnd,msg,w,l);
}
LRESULT CALLBACK WindowProc(HWND hwnd,UINT msg,WPARAM w,LPARAM l) {
    if(msg==WM_SIZE){if(app.table)app.Layout();
        return 0;
    }
    if(msg==WM_DPICHANGED){app.dpi=HIWORD(w);
        auto rect=reinterpret_cast<RECT*>(l);
        SetWindowPos(hwnd,nullptr,rect->left,rect->top,rect->right-rect->left,rect->bottom-rect->top,SWP_NOZORDER|SWP_NOACTIVATE);
        app.Theme();
        app.Layout();
        return 0;
    }
    if(msg==WM_TIMER && w==Poll){app.Drain();
        return 0;
    }
    if(msg==WM_CLOSE){ShowWindow(hwnd,SW_HIDE);
        return 0;
    }
    if(msg==WM_DESTROY){app.Finish(true);
        NOTIFYICONDATAW icon{sizeof icon};
        icon.hWnd=hwnd;
        icon.uID=1;
        Shell_NotifyIconW(NIM_DELETE,&icon);
        PostQuitMessage(0);
        return 0;
    }
    if(msg==TrayMessage){
        if(l==WM_LBUTTONDBLCLK){ShowWindow(hwnd,SW_SHOW);
            SetForegroundWindow(hwnd);
        }
        if(l==WM_RBUTTONUP){HMENU menu=CreatePopupMenu();
            AppendMenuW(menu,MF_STRING,301,app.L(L"打开",L"Open"));
            AppendMenuW(menu,MF_STRING,302,app.L(L"退出",L"Exit"));
            POINT p{};
            GetCursorPos(&p);
            SetForegroundWindow(hwnd);
            int command=TrackPopupMenu(menu,TPM_RETURNCMD|TPM_RIGHTBUTTON,p.x,p.y,0,hwnd,nullptr);
            DestroyMenu(menu);
            if(command==301)ShowWindow(hwnd,SW_SHOW);
            if(command==302)DestroyWindow(hwnd);
        }return 0;
    }
    if(msg==WM_COMMAND){
        switch(LOWORD(w)){
            case Search:if(HIWORD(w)==EN_CHANGE){app.scroll=0;
                app.Filter();
                app.Update();
            }break;
            case View:app.tableMode=!app.tableMode;
            app.Layout();
            break;
            case Refresh:app.Start(true);
            break;
            case TestAll:app.Start(false);
            break;
            case TestSelected:if(app.selected>=0 && app.selected<static_cast<int>(app.nodes.size()))app.Start(false,app.nodes[app.selected].name);
            break;
            case Cancel:app.message=app.L(L"测速已取消，已完成结果保留",L"Test cancelled. Completed results retained");
            app.Finish(true);
            break;
            case Settings:app.ShowSettings();
            break;
            case Details:app.ShowDetails();
            break;
        }return 0;
    }
    if(msg==WM_GETMINMAXINFO){auto limits=reinterpret_cast<MINMAXINFO*>(l);
        limits->ptMinTrackSize={app.Px(600),app.Px(380)};
        return 0;
    }
    if(msg==WM_DRAWITEM){DrawButton(*reinterpret_cast<DRAWITEMSTRUCT*>(l));
        return TRUE;
    }
    if(msg==WM_NOTIFY){
        auto header=reinterpret_cast<NMHDR*>(l);
        if(header->hwndFrom==app.tooltip && header->code==TTN_GETDISPINFOW){
            POINT point{};
            GetCursorPos(&point);
            ScreenToClient(app.cards,&point);
            int pitch=app.cardHeight+app.Px(20),column=point.x/(app.cardWidth+app.Px(20));
            int index=(point.y+app.scroll)/pitch*app.columns+column;
            app.tipText.clear();
            if(index>=0 && index<static_cast<int>(app.visible.size())){
                auto& node=app.nodes[app.visible[index]];
                int x=point.x-column*(app.cardWidth+app.Px(20))-app.Px(17);
                int site=x*5/std::max(1,app.cardWidth-app.Px(34));
                int relativeY=(point.y+app.scroll)%pitch;
                if(relativeY>=app.cardHeight*85/100)app.tipText=std::wstring(app.L(L"内核 ",L"Core "))+CoreText(node,true);
                else if(relativeY>=app.cardHeight*52/100 && site>=0 && site<5)
                    app.tipText=std::wstring(SiteNames[site])+L" · "+SiteText(node.sites[site],app.prefs.english,true);
                else app.tipText=node.name;
            }
            reinterpret_cast<NMTTDISPINFOW*>(l)->lpszText=app.tipText.data();
            return 0;
        }
        if(header->hwndFrom==app.table){
            if(header->code==LVN_GETDISPINFOW){
                auto display=reinterpret_cast<NMLVDISPINFOW*>(l);
                auto& item=display->item;
                if(item.iItem>=0 && item.iItem<static_cast<int>(app.visible.size()) && item.mask&LVIF_TEXT){
                    auto& node=app.nodes[app.visible[item.iItem]];
                    std::wstring value;
                    switch(item.iSubItem){case 0:value=node.name;
                        break;
                        case 1:value=node.protocol;
                        break;
                        case 2:value=node.score>=0?std::to_wstring(node.score):L"—";
                        break;
                        case 8:value=node.probing?app.L(L"测速中",L"Testing"):node.state==L"queued"?app.L(L"排队中",L"Queued"):node.round?app.L(L"已回填",L"Received"):node.state==L"ready"?app.L(L"待测",L"Untested"):app.L(L"不可用",L"Unavailable");
                        break;
                        case 9:value=std::to_wstring(node.round);
                        break;
                        case 10:value=CoreText(node);
                        break;
                        default:if(item.iSubItem>=3 && item.iSubItem<=7)value=SiteText(node.sites[item.iSubItem-3],app.prefs.english);
                        break;
                    }
                    wcsncpy_s(item.pszText,item.cchTextMax,value.c_str(),_TRUNCATE);
                }return 0;
            }
            if(header->code==LVN_COLUMNCLICK){auto click=reinterpret_cast<NMLISTVIEW*>(l);
                app.descending=app.sort==click->iSubItem?!app.descending:false;
                app.sort=click->iSubItem;
                app.Filter();
                return 0;
            }
            if(header->code==LVN_ITEMCHANGED && !app.filtering){int index=ListView_GetNextItem(app.table,-1,LVNI_SELECTED);
                if(index>=0 && index<static_cast<int>(app.visible.size()))app.selected=app.visible[index];
            }
            if(header->code==NM_DBLCLK)app.ShowDetails();
        }
    }
    if(msg==WM_CTLCOLORSTATIC || msg==WM_CTLCOLOREDIT){SetTextColor(reinterpret_cast<HDC>(w),app.Text());
        SetBkColor(reinterpret_cast<HDC>(w),app.Surface());
        return reinterpret_cast<LRESULT>(app.controlBrush);
    }
    if(msg==WM_PAINT){PAINTSTRUCT paint{};
        HDC dc=BeginPaint(hwnd,&paint);
        RECT area{};
        GetClientRect(hwnd,&area);
        Fill(dc,area,app.Background());
        Label(dc,app.L(L"ProxyPartner  /  节点质量",L"ProxyPartner  /  Node quality"),{app.Px(14),app.Px(10),area.right,app.Px(37)},app.bold,app.Text());
        EndPaint(hwnd,&paint);
        return 0;
    }
    return DefWindowProcW(hwnd,msg,w,l);
}

int WINAPI wWinMain(HINSTANCE instance,HINSTANCE,PWSTR,int) {
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    app.instance=instance;
    wchar_t executable[32768]{};
    GetModuleFileNameW(nullptr,executable,32768);
    app.directory=fs::path(executable).parent_path();
    app.Load();
    int argc=0;
    LPWSTR* argv=CommandLineToArgvW(GetCommandLineW(),&argc);
    bool tray=false;
    for(int i=1;i<argc;++i){if(wcscmp(argv[i],L"--tray")==0)tray=true;
        else if(wcscmp(argv[i],L"--profile")==0 && i+1<argc)app.prefs.profile=argv[++i];
    }
    LocalFree(argv);
    INITCOMMONCONTROLSEX controls{sizeof controls,ICC_LISTVIEW_CLASSES};
    InitCommonControlsEx(&controls);
    app.appIcon=static_cast<HICON>(LoadImageW(instance,MAKEINTRESOURCEW(1),IMAGE_ICON,0,0,LR_DEFAULTSIZE|LR_SHARED));
    if(!app.appIcon)app.appIcon=LoadIconW(nullptr,IDI_APPLICATION);
    WNDCLASSEXW cls{sizeof cls};
    cls.hInstance=instance;
    cls.lpfnWndProc=WindowProc;
    cls.lpszClassName=L"ProxyPartner.NativeDesktop";
    cls.hCursor=LoadCursorW(nullptr,IDC_ARROW);
    cls.hIcon=cls.hIconSm=app.appIcon;
    RegisterClassExW(&cls);
    cls.lpfnWndProc=CardsProc;
    cls.lpszClassName=L"ProxyPartner.NativeCards";
    cls.style=CS_DBLCLKS;
    RegisterClassExW(&cls);
    cls.lpfnWndProc=AuxProc;
    cls.lpszClassName=L"ProxyPartner.NativeAux";
    RegisterClassExW(&cls);
    app.window=CreateWindowExW(WS_EX_CONTROLPARENT,L"ProxyPartner.NativeDesktop",L"ProxyPartner",WS_OVERLAPPEDWINDOW|WS_CLIPCHILDREN,
    CW_USEDEFAULT,CW_USEDEFAULT,1260,730,nullptr,nullptr,instance,nullptr);
    if(!app.window)return 1;
    app.dpi=GetDpiForWindow(app.window);
    app.status=Child(app.window,L"STATIC",L"",100);
    app.search=Child(app.window,L"EDIT",L"",Search,WS_BORDER|WS_TABSTOP|ES_AUTOHSCROLL);
    for(int id=View;id<=Details;++id)Child(app.window,L"BUTTON",L"",id,WS_TABSTOP|BS_OWNERDRAW);
    Child(app.window,L"BUTTON",L"",TestSelected,WS_TABSTOP|BS_OWNERDRAW);
    app.table=Child(app.window,WC_LISTVIEWW,L"",List,LVS_REPORT|LVS_OWNERDATA|LVS_SHOWSELALWAYS|WS_TABSTOP);
    ListView_SetExtendedListViewStyle(app.table,LVS_EX_FULLROWSELECT|LVS_EX_DOUBLEBUFFER);
    int widths[]={230,95,75,105,105,125,115,105,135,70,150};
    for(int i=0;i<11;++i){LVCOLUMNW col{};
        col.mask=LVCF_WIDTH|LVCF_TEXT;
        col.cx=app.Px(widths[i]);
        col.pszText=const_cast<wchar_t*>(L"");
        ListView_InsertColumn(app.table,i,&col);
    }
    app.cards=Child(app.window,L"ProxyPartner.NativeCards",L"",Cards,WS_VSCROLL|WS_TABSTOP);
    app.tooltip=CreateWindowExW(WS_EX_TOPMOST,TOOLTIPS_CLASSW,nullptr,TTS_ALWAYSTIP|TTS_NOPREFIX,CW_USEDEFAULT,CW_USEDEFAULT,CW_USEDEFAULT,CW_USEDEFAULT,app.window,nullptr,instance,nullptr);
    TOOLINFOW tool{sizeof tool};
    tool.uFlags=TTF_SUBCLASS|TTF_IDISHWND;
    tool.hwnd=app.window;
    tool.uId=reinterpret_cast<UINT_PTR>(app.cards);
    tool.lpszText=LPSTR_TEXTCALLBACKW;
    SendMessageW(app.tooltip,TTM_ADDTOOLW,0,reinterpret_cast<LPARAM>(&tool));
    for(int i=0;i<5;++i){fs::path path=app.directory/L"TargetIcons"/(std::wstring(SiteIds[i])+L".ico");
        app.icons[i]=static_cast<HICON>(LoadImageW(nullptr,path.c_str(),IMAGE_ICON,0,0,LR_LOADFROMFILE));
    }
    app.Theme();
    app.Layout();
    app.message=app.L(L"选择本机配置后刷新节点",L"Choose a local profile and refresh nodes");
    app.Update();
    NOTIFYICONDATAW icon{sizeof icon};
    icon.hWnd=app.window;
    icon.uID=1;
    icon.uFlags=NIF_MESSAGE|NIF_ICON|NIF_TIP;
    icon.uCallbackMessage=TrayMessage;
    icon.hIcon=app.appIcon;
    wcscpy_s(icon.szTip,L"ProxyPartner");
    Shell_NotifyIconW(NIM_ADD,&icon);
    if(!tray)ShowWindow(app.window,SW_SHOW);
    app.autoTest=true;
    if(!app.prefs.profile.empty())app.Start(true);
    MSG message{};
    while(GetMessageW(&message,nullptr,0,0)>0){if(app.preferencesWindow && IsDialogMessageW(app.preferencesWindow,&message))continue;
        if(app.settings && IsDialogMessageW(app.settings,&message))continue;
        if(IsDialogMessageW(app.window,&message))continue;
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    for(auto iconHandle:app.icons)if(iconHandle)DestroyIcon(iconHandle);
    if(app.font)DeleteObject(app.font);
    if(app.bold)DeleteObject(app.bold);
    if(app.scoreFont)DeleteObject(app.scoreFont);
    if(app.siteFont)DeleteObject(app.siteFont);
    if(app.controlBrush)DeleteObject(app.controlBrush);
    if(app.backgroundBrush)DeleteObject(app.backgroundBrush);
    return 0;
}
