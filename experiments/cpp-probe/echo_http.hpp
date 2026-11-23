#pragma once

// Fixed-storage HTTP/1 response reader for tiny echo bodies. No allocation or decoding.
template<class Read> bool read_echo_response(Read read,char* body,unsigned capacity,unsigned* used,int* status) {
    *used=0; *status=0;
    if(capacity<65) return false;
    char buffer[1024]; int offset=0,available=0;
    auto byte=[&](char& c) {
        if(offset==available) { available=read(buffer,static_cast<int>(sizeof(buffer))); offset=0; }
        if(available<=0) return available;
        c=buffer[offset++]; return 1;
    };
    unsigned wireBytes=0;
    auto line=[&](char* text,unsigned limit,unsigned& size) {
        size=0;
        char c;
        while(size+1<limit && ++wireBytes<=32768) {
            if(byte(c)!=1) return false;
            if(c=='\r') {
                if(++wireBytes>32768 || byte(c)!=1 || c!='\n') return false;
                text[size]=0; return true;
            }
            if(c=='\n' || c==0) return false;
            text[size++]=c;
        }
        return false;
    };
    char text[8192]; unsigned size=0;
    bool chunked=false,hasLength=false; unsigned length=0;
    for(unsigned responses=0;;++responses) {
        if(responses==8 || !line(text,sizeof(text),size) || size<12) return false;
        if(text[0]!='H'||text[1]!='T'||text[2]!='T'||text[3]!='P'||text[4]!='/'||text[5]!='1'||text[6]!='.'||(text[7]!='0'&&text[7]!='1')||text[8]!=' ') return false;
        int code=0;
        for(int i=9;i<12;++i) { if(text[i]<'0'||text[i]>'9')return false; code=code*10+text[i]-'0'; }
        if(code<100 || code>599 || (size>12 && text[12]!=' ')) return false;
        chunked=false; hasLength=false; length=0;
        while(true) {
            if(!line(text,sizeof(text),size)) return false;
            if(!size) break;
            unsigned colon=0;
            while(colon<size && text[colon]!=':') {
                char& c=text[colon]; if(c>='A'&&c<='Z')c+='a'-'A';
                if(!((c>='a'&&c<='z')||(c>='0'&&c<='9')||c=='-'))return false;
                ++colon;
            }
            if(!colon || colon==size) return false;
            unsigned first=colon+1,last=size;
            while(first<last && (text[first]==' '||text[first]=='\t'))++first;
            while(last>first && (text[last-1]==' '||text[last-1]=='\t'))--last;
            auto name=[&](const char* expected) { unsigned i=0;while(expected[i] && i<colon && expected[i]==text[i])++i;return i==colon && !expected[i]; };
            if(name("content-encoding")) return false;
            if(name("content-length")) {
                if(hasLength || first==last) return false;
                hasLength=true;
                for(unsigned i=first;i<last;++i) { if(text[i]<'0'||text[i]>'9')return false;length=length*10+text[i]-'0';if(length>64)return false; }
            }
            if(name("transfer-encoding")) {
                if(chunked || last-first!=7) return false;
                const char expected[]="chunked";
                for(unsigned i=0;i<7;++i) { char c=text[first+i];if(c>='A'&&c<='Z')c+='a'-'A';if(c!=expected[i])return false; }
                chunked=true;
            }
        }
        if(chunked && hasLength) return false;
        if(code<200) { if(code==101||chunked||hasLength)return false;continue; }
        *status=code;
        if(code!=200) return false;
        break;
    }
    if(chunked) {
        for(unsigned chunks=0;chunks<128;++chunks) {
            if(!line(text,sizeof(text),size) || !size) return false;
            unsigned count=0;
            for(unsigned i=0;i<size;++i) {
                char c=text[i];unsigned digit=c>='0'&&c<='9'?c-'0':c>='a'&&c<='f'?c-'a'+10:c>='A'&&c<='F'?c-'A'+10:16;
                if(digit==16) return false; // Extensions deliberately unsupported.
                count=count*16+digit; if(count>64-*used)return false;
            }
            if(!count) { if(!line(text,sizeof(text),size) || size)return false;body[*used]=0;return true; }
            for(unsigned i=0;i<count;++i) { if(byte(body[*used])!=1)return false;++*used; }
            char c;if(byte(c)!=1||c!='\r'||byte(c)!=1||c!='\n')return false;
        }
        return false;
    }
    if(hasLength) {
        for(unsigned i=0;i<length;++i) { if(byte(body[*used])!=1)return false;++*used; }
    } else {
        char c;int result;
        while((result=byte(c))==1) { if(*used==64)return false;body[(*used)++]=c; }
        if(result<0) return false;
    }
    body[*used]=0;return true;
}
