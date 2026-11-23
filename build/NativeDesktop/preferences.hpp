#pragma once

// Boundary for both the legacy C# settings file and native desktop preferences.
bool ValidWeights(const std::array<int,3>& values) {
    return std::all_of(values.begin(),values.end(),[](int value){return value>=0 && value<=100;}) &&
        values[0]+values[1]+values[2]==100;
}
bool ValidTarget(const RegisteredTarget& target) {
    if(target.url.empty() || target.url.size()>2048 || target.shortName.empty() || target.shortName.size()>12 ||
        target.symbol.empty() || target.symbol.size()>8)return false;
    if(std::any_of(target.url.begin(),target.url.end(),[](wchar_t c){return c<=L' ' || c==L'\\';}) ||
        std::any_of(target.shortName.begin(),target.shortName.end(),[](wchar_t c){return c<L' ';}) ||
        std::any_of(target.symbol.begin(),target.symbol.end(),[](wchar_t c){return c<L' ';}))return false;
    URL_COMPONENTS url{};
    url.dwStructSize=sizeof url;
    url.dwHostNameLength=url.dwUserNameLength=url.dwPasswordLength=static_cast<DWORD>(-1);
    return WinHttpCrackUrl(target.url.c_str(),static_cast<DWORD>(target.url.size()),0,&url) &&
        (url.nScheme==INTERNET_SCHEME_HTTP || url.nScheme==INTERNET_SCHEME_HTTPS) &&
        url.dwHostNameLength>0 && url.dwUserNameLength==0 && url.dwPasswordLength==0;
}
bool DecodePreferences(const Json& data,Preferences& destination,bool legacy) {
    if(!data.is_object())return false;
    Preferences value;
    auto field=[&](const char* native,const char* old)->const Json*{
        auto found=data.find(legacy?old:native);
        return found==data.end()?nullptr:&*found;
    };
    auto text=[](const Json* item,std::wstring& result,size_t maximum){
        if(!item || item->is_null())return true;
        if(!item->is_string() || item->get_ref<const std::string&>().size()>maximum*4)return false;
        result=Wide(item->get<std::string>());
        return result.size()<=maximum && std::none_of(result.begin(),result.end(),[](wchar_t c){return c<L' ';});
    };
    auto boolean=[](const Json* item,bool& result){
        if(!item)return true;
        if(!item->is_boolean())return false;
        result=item->get<bool>();
        return true;
    };
    if(!text(field("profile","ProbeProfilePath"),value.profile,4096) ||
        !text(field("identity","Identity"),value.identity,4096) ||
        !boolean(field("english","English"),value.english) || !boolean(field("light","Light"),value.light) ||
        !boolean(field("kernel","Kernel"),value.kernel))return false;
    auto weights=[&](const Json* item,std::array<int,3>& result){
        if(!item)return !legacy;
        const char* keys[]={"First","Second","Third"};
        if(legacy && !item->is_object() || !legacy && (!item->is_array() || item->size()!=3))return false;
        for(int i=0;i<3;++i){
            auto found=legacy?item->find(keys[i]):item->begin()+i;
            if(found==item->end() || !found->is_number_integer() || *found<0 || *found>100)return false;
            result[i]=found->get<int>();
        }
        return ValidWeights(result);
    };
    if(!weights(field("components","Components"),value.components) || !weights(field("targets","Targets"),value.targets))return false;
    if(auto targets=field("customTargets","CustomTargets")){
        if(!targets->is_array() || targets->size()>16)return false;
        for(const auto& item:*targets){
            if(!item.is_object())return false;
            auto get=[&](const char* native,const char* old)->const Json*{
                auto found=item.find(legacy?old:native);
                return found==item.end()?nullptr:&*found;
            };
            RegisteredTarget target;
            if(!text(get("url","Url"),target.url,2048) || !text(get("shortName","ShortName"),target.shortName,12) ||
                !text(get("symbol","Symbol"),target.symbol,8) || !ValidTarget(target))return false;
            value.customTargets.push_back(target);
        }
    }
    auto number=[&](const char* key,int& result,int minimum,int maximum){
        auto found=data.find(key);
        if(found==data.end())return true;
        if(!found->is_number_integer() || *found<minimum || *found>maximum)return false;
        result=found->get<int>();
        return true;
    };
    if(!legacy && (!number("rounds",value.rounds,1,6) || !number("parallel",value.parallel,0,12) ||
        !number("deadline",value.deadline,0,6000) || !number("minimum",value.minimum,1,12) ||
        !number("maximum",value.maximum,1,12) || !number("proxyPort",value.proxyPort,0,65535) || value.minimum>value.maximum))return false;
    destination=std::move(value);
    return true;
}
bool LoadPreferenceFile(const fs::path& path,Preferences& destination,bool legacy) {
    std::error_code error;
    auto size=fs::file_size(path,error);
    if(error || size==0 || size>65536)return false;
    std::ifstream input(path,std::ios::binary);
    if(!input)return false;
    Json data=Json::parse(input,nullptr,false);
    return DecodePreferences(data,destination,legacy);
}
Json EncodePreferences(const Preferences& value) {
    Json targets=Json::array();
    for(const auto& target:value.customTargets)targets.push_back({{"url",Utf8(target.url)},
        {"shortName",Utf8(target.shortName)},{"symbol",Utf8(target.symbol)}});
    return {{"profile",Utf8(value.profile)},{"identity",Utf8(value.identity)},{"rounds",value.rounds},
        {"parallel",value.parallel},{"deadline",value.deadline},{"minimum",value.minimum},{"maximum",value.maximum},
        {"kernel",value.kernel},{"english",value.english},{"light",value.light},{"proxyPort",value.proxyPort},
        {"components",value.components},{"targets",value.targets},{"customTargets",targets}};
}
