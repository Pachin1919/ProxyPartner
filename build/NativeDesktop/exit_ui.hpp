#pragma once

bool App::ClearExitTransfer() {
    if(exitStateFile){CloseHandle(exitStateFile);exitStateFile=nullptr;}
    if(exitStatePath.empty())return true;
    if(!DeleteFileW(exitStatePath.c_str()) && GetLastError()!=ERROR_FILE_NOT_FOUND)return false;
    exitStatePath.clear();
    return true;
}
bool App::PrepareExitTransfer() {
    if(!ClearExitTransfer())return false;
    if(activeExitFacts->state.is_null())return true;
    wchar_t folder[32768]{},idText[64]{};
    DWORD count=GetTempPathW(32768,folder);
    GUID id{};
    if(!count || count>=32768 || FAILED(CoCreateGuid(&id)) || !StringFromGUID2(id,idText,64))return false;
    exitStatePath=fs::path(folder)/(std::wstring(L"ProxyPartner-exit-")+idText+L".json");
    HANDLE file=CreateFileW(exitStatePath.c_str(),GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_NEW,FILE_ATTRIBUTE_TEMPORARY,nullptr);
    if(file==INVALID_HANDLE_VALUE){exitStatePath.clear();return false;}
    exitStateFile=file;
    std::string data=activeExitFacts->state.dump();
    DWORD written=0;
    if(WriteFile(file,data.data(),static_cast<DWORD>(data.size()),&written,nullptr) && written==data.size())return true;
    ClearExitTransfer();
    return false;
}
bool App::ApplyExitEvent(const Json& value) {
    if(!activeExitFacts || !value.is_object())return false;
    auto text=[](const Json& object,const char* key,size_t maximum=64)->std::wstring{
        auto item=object.find(key);
        if(item==object.end() || !item->is_string() || item->get_ref<const std::string&>().size()>maximum*4)return {};
        auto result=Wide(item->get<std::string>());
        return result.size()<=maximum && std::none_of(result.begin(),result.end(),[](wchar_t c){return c<L' ';})?result:std::wstring{};
    };
    auto number=[](const Json& object,const char* key,int fallback,int maximum){
        auto item=object.find(key);
        return item!=object.end() && item->is_number_integer() && *item>=0 && *item<=maximum?item->get<int>():fallback;
    };
    auto boolean=[](const Json& object,const char* key){auto item=object.find(key);return item!=object.end() && item->is_boolean() && item->get<bool>();};
    std::wstring kind=text(value,"event");
    auto& facts=*activeExitFacts;
    if(kind==L"catalog")return number(value,"nodes",0,1)==1;
    if(kind==L"error"){
        facts.notice=L(L"本次出口请求未成功，保留最近有效观察。",L"This exit request failed. Last valid observations retained.");
        return true;
    }
    if(kind==L"exit_state_input"){
        std::wstring state=text(value,"state");
        if(state!=L"accepted" && state!=L"ignored")return false;
        facts.notice=state==L"accepted"?L(L"已沿用本 GUI 会话的同节点、同配置观察状态。",L"State reused for the same node and profile in this GUI session."):
            L(L"旧状态未沿用；保留的旧观察仅供历史参考。",L"Previous state was not reused. Retained observations are historical reference.");
        return true;
    }
    if(text(value,"node",4096)!=exitNode)return false;
    if(kind==L"exit_begin"){
        if(number(value,"rounds",0,6)!=1 || number(value,"sources",0,2)!=2 || !boolean(value,"independent"))return false;
        facts.protocol=text(value,"protocol");
        return !facts.protocol.empty();
    }
    if(kind==L"exit_source"){
        int index=number(value,"source",0,2)-1;
        if(index<0 || number(value,"round",0,6)!=1)return false;
        const wchar_t* hosts[]={L"ipv4.icanhazip.com",L"api.ipify.org"};
        if(text(value,"host")!=hosts[index])return false;
        ExitSource source;
        source.seen=true;
        source.observed=text(value,"observedUtc");
        source.response={number(value,"httpStatus",-1,599),number(value,"elapsedMs",-1,3600000),true,boolean(value,"timedOut"),false};
        if(text(value,"state")==L"public"){
            source.family=number(value,"family",0,6);
            source.address=text(value,"address");
            if(!ExitLiteral(source.address,source.family))return false;
        }
        facts.sources[index]=std::move(source);
        return true;
    }
    if(kind==L"exit_observation"){
        if(number(value,"round",0,6)!=1)return false;
        int family=number(value,"family",0,6);
        ExitObservation observation;
        observation.address=text(value,"address");observation.otherAddress=text(value,"corroboratingAddress");
        observation.lastVerified=text(value,"lastVerifiedAddress");
        if(!ExitLiteral(observation.address,family) || !observation.otherAddress.empty() && !ExitLiteral(observation.otherAddress,family) ||
            !observation.lastVerified.empty() && !ExitLiteral(observation.lastVerified,family))return false;
        observation.observers=number(value,"observers",0,2);
        observation.changes=number(value,"changes",-1,1000000);
        observation.confirmedSamples=number(value,"confirmedSamples",-1,1000000);
        observation.agreement=text(value,"agreement");observation.observed=text(value,"observedUtc");
        observation.consistentSince=text(value,"consistentSince");
        bool confirmed=observation.agreement==L"confirmed",conflict=observation.agreement==L"conflict",single=observation.agreement==L"single_source";
        if(observation.changes<0 || observation.confirmedSamples<0 || observation.observed.empty() ||
            confirmed && (observation.observers!=2 || observation.otherAddress!=observation.address || observation.lastVerified!=observation.address || observation.confirmedSamples==0) ||
            conflict && (observation.observers!=2 || observation.otherAddress.empty() || observation.otherAddress==observation.address) ||
            single && (observation.observers!=1 || !observation.otherAddress.empty()) || !confirmed && !conflict && !single)return false;
        observation.valid=true;
        facts.observations[family==4?0:1]=std::move(observation);
        return true;
    }
    if(kind==L"exit_state"){
        Json state;
        if(!NormalizeExitState(value,exitNode,facts.protocol,state))return false;
        facts.state=std::move(state);
        return true;
    }
    if(kind==L"exit_summary"){
        if(number(value,"rounds",-1,1)<0 || number(value,"publicObservations",-1,2)<0)return false;
        exitCancelled=boolean(value,"cancelled");
        if(exitCancelled)facts.notice=L(L"本次观察已取消，保留最近有效观察。",L"This observation was cancelled. Last valid observations retained.");
        sawExitSummary=true;
        return true;
    }
    return false;
}
std::wstring App::ExitReport(const Node& node) const {
    std::wstring report=std::wstring(L"\r\n")+L(L"出口 IP · 两个独立 HTTPS 来源 · GUI 会话内观察",L"Exit IP · two independent HTTPS sources · GUI session observations")+L"\r\n";
    if(exitMode && Running() && exitNode==node.name)report+=std::wstring(L(L"观察中",L"Observing"))+L"\r\n";
    if(!node.exit)return report+L(L"尚无出口观察。",L"No exit observations yet.");
    const auto& facts=*node.exit;
    if(!facts.protocol.empty())report+=facts.protocol+L"\r\n";
    if(!facts.notice.empty())report+=facts.notice+L"\r\n";
    const wchar_t* hosts[]={L"ipv4.icanhazip.com",L"api.ipify.org"};
    for(int i=0;i<2;++i){
        const auto& source=facts.sources[i];
        report+=std::wstring(hosts[i])+L" · ";
        if(!source.seen)report+=L(L"本次尚未返回",L"No result in this request");
        else {
            report+=SiteText(source.response,prefs.english,true);
            report+=source.address.empty()?std::wstring(L" · ")+L(L"未取得有效公开地址",L"No valid public address"):
                L" · IPv"+std::to_wstring(source.family)+L" "+source.address;
            report+=L" · "+source.observed;
        }
        report+=L"\r\n";
    }
    for(int i=0;i<2;++i){
        const auto& observation=facts.observations[i];
        report+=std::wstring(L"\r\nIPv")+(i==0?L"4":L"6")+L" · ";
        if(!observation.valid){report+=L(L"无有效观察",L"No valid observation");continue;}
        report+=observation.address+L" · "+(observation.agreement==L"confirmed"?L(L"两源一致",L"Two sources agree"):
            observation.agreement==L"conflict"?L(L"两源冲突",L"Sources conflict"):L(L"单源，未交叉确认",L"Single source, uncorroborated"));
        if(!observation.otherAddress.empty())report+=L" · "+observation.otherAddress;
        report+=L"\r\n"+observation.observed+L"\r\n"+L(L"上次确认 ",L"Last confirmed ")+
            (observation.lastVerified.empty()?L(L"未知",L"Unknown"):observation.lastVerified);
        report+=L"\r\n"+std::wstring(L(L"确认变化次数 ",L"Confirmed changes "))+std::to_wstring(observation.changes)+
            L" · "+L(L"连续确认样本 ",L"Consecutive confirmed samples ")+std::to_wstring(observation.confirmedSamples);
        if(!observation.consistentSince.empty())report+=std::wstring(L" · ")+L(L"一致起始 ",L"Consistent since ")+observation.consistentSince;
    }
    return report+L"\r\n";
}
