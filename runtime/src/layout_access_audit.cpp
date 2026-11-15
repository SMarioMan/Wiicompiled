#include "guest_abi/layout_access_audit.h"

#include <algorithm>
#include <array>
#include <fstream>
#include <map>
#include <memory>
#include <mutex>
#include <sstream>
#include <utility>
#include <unordered_map>
#include <vector>

namespace LayoutAccessAudit {
std::atomic<bool> enabled{false};
namespace {
struct OwnedField {
    std::string name;
    uint32_t oldOffset, oldSize, oldCount, oldStride, newOffset, newSize, newCount, newStride;
    bool perRacer;
};
struct OwnedType { uint32_t oldSize, newSize; std::vector<OwnedField> fields; };
struct OwnedSite {
    uint32_t pc; std::string type, field, function, rule;
    uint32_t index, memberOffset, objectBase; bool native;
    explicit OwnedSite(const Site& s) : pc(s.pc),type(s.type),field(s.field),function(s.function),rule(s.rule),
        index(s.index),memberOffset(s.memberOffset),objectBase(s.objectBase),native(s.native) {}
    Site view() const { return {pc,type,field,function,rule,index,memberOffset,objectBase,native}; }
};
struct Instance { uint64_t generation; std::string type; uint32_t base,bytes,owner; std::string lifetime; uint64_t frame = 0; uint32_t context = 0; };
struct Hit {
    uint32_t pc,address,width,base,expected,index; uint64_t generation,count;
    bool write,high,native,exact;
    std::string type,field,rule,function,phase,lifetime,classification;
    uint32_t oldOffset,newOffset,oldSize,newSize;
    std::vector<uint32_t> stack;
};
struct alignas(64) AccessCounters { std::atomic<uint64_t> observed{0},filtered{0}; };
struct State {
    std::mutex mutex, reportMutex;
    Options options;
    std::map<std::string,OwnedType,std::less<>> types;
    std::map<uint32_t,OwnedSite> sites;
    std::map<uint32_t,std::string> stackSites;
    std::map<uint32_t,uint64_t> unscopedStackSites;
    std::map<uint64_t,Instance> instances;
    std::map<std::string,Hit> hits;
    uint64_t nextGeneration=1, nextFrame=1, watchedAccesses=0;
    std::array<AccessCounters,64> counters{};
    std::atomic<uint32_t> nextCounterSlot{0};
    // Diagnostic index only: no guest pages are protected or poisoned.
    std::unique_ptr<std::atomic<uint32_t>[]> watchedPages;
    std::array<std::atomic<uint64_t>,1024> stackPcFilter{};
    std::string phase;
};
State& state(){static State value;return value;}
AccessCounters& threadCounters(State& s){
    thread_local const uint32_t slot=s.nextCounterSlot.fetch_add(1,std::memory_order_relaxed)%s.counters.size();
    return s.counters[slot];
}
uint64_t accessCount(const State& s,bool filtered){
    uint64_t total=0;
    for(const auto& counter:s.counters)
        total+=(filtered?counter.filtered:counter.observed).load(std::memory_order_relaxed);
    return total;
}

void ensurePageIndex(State& s){
    if(!s.watchedPages)s.watchedPages=std::make_unique<std::atomic<uint32_t>[]>(1u<<20);
}
void changePages(State& s,const Instance& instance,bool add){
    ensurePageIndex(s);
    const uint64_t bytes=std::max(instance.bytes,s.types.at(instance.type).oldSize);
    const uint64_t end=std::min(uint64_t(instance.base)+bytes,uint64_t(UINT32_MAX)+1);
    const uint64_t last=(end-1)>>12;
    for(uint64_t page=instance.base>>12;page<=last;page++){
        if(add)s.watchedPages[page].fetch_add(1,std::memory_order_release);
        else s.watchedPages[page].fetch_sub(1,std::memory_order_release);
    }
}
template<class Predicate> void retireMatching(State& s,Predicate predicate){
    std::erase_if(s.instances,[&](const auto& item){
        if(!predicate(item.second))return false;
        changePages(s,item.second,false);return true;
    });
}
bool touchesWatchedPage(const State& s,uint32_t address,uint32_t width){
    if(!s.watchedPages)return false;
    const uint64_t end=uint64_t(address)+width;
    if(end>uint64_t(UINT32_MAX)+1)return true; // retain full invalid-range handling
    for(uint64_t page=address>>12;page<=((end-1)>>12);page++)
        if(s.watchedPages[page].load(std::memory_order_acquire))return true;
    return false;
}
bool mayStartStack(const State& s,uint32_t pc){
    const auto index=(pc>>2)&0xffffu;
    return (s.stackPcFilter[index>>6].load(std::memory_order_acquire)&(uint64_t(1)<<(index&63)))!=0;
}

struct ExecutionContext { Site site; bool extendedSite=false; std::vector<uint32_t> stack; std::vector<uint64_t> frames; };
thread_local std::unordered_map<uint32_t,ExecutionContext> contexts;
thread_local ExecutionContext* cachedContext=nullptr;
thread_local uint32_t cachedContextKey=0;
bool hasFieldIntent(const Site& site){
    return !site.type.empty() || !site.field.empty() || !site.function.empty() || !site.rule.empty() ||
           site.index!=UnknownIndex || site.memberOffset || site.objectBase || site.native;
}
std::atomic<uint32_t (*)()> contextProvider{nullptr};
uint32_t contextKey(){const auto provider=contextProvider.load(std::memory_order_relaxed);return provider?provider():0;}
ExecutionContext& context(){
    const auto key=contextKey();
    if(!cachedContext || cachedContextKey!=key){cachedContext=&contexts[key];cachedContextKey=key;}
    return *cachedContext;
}

bool contains(uint64_t start,uint64_t size,uint64_t address,uint64_t width){
    return width && address>=start && address+width>=address && address+width<=start+size;
}
bool overlaps(uint64_t start,uint64_t size,uint64_t address,uint64_t width){
    return width && address<start+size && address+width>start;
}
std::string quote(std::string_view text){
    std::string out="\"";
    constexpr char digits[]="0123456789abcdef";
    for(unsigned char c:text){
        if(c=='"'||c=='\\'){out+='\\';out+=char(c);}
        else if(c<32){out+="\\u00";out+=digits[c>>4];out+=digits[c&15];}
        else out+=char(c);
    }
    return out+'"';
}
std::string hex(uint32_t value){std::ostringstream out;out<<"0x"<<std::hex<<value;return out.str();}
std::string reportLocked(const State& s){
    std::ostringstream out;
    out<<"{\"format\":\"retired-layout-access-report-v1\",\"mode\":"
       <<quote(s.options.mode==Mode::Strict?"strict":s.options.mode==Mode::Log?"log":"off")
       <<",\"registeredTypes\":"<<s.types.size()<<",\"boundSites\":"<<s.sites.size()<<",\"stackLifetimeSites\":"<<s.stackSites.size()<<",\"instancesRegistered\":"<<(s.nextGeneration-1)<<",\"observedAccesses\":"<<accessCount(s,false)<<",\"filteredAccesses\":"<<accessCount(s,true)<<",\"watchedAccesses\":"<<s.watchedAccesses<<",\"activeInstances\":"<<s.instances.size()<<",\"hits\":[";
    bool first=true;
    for(const auto& [key,h]:s.hits){
        (void)key;if(!std::exchange(first,false))out<<',';
        out<<"{\"guestPc\":"<<quote(hex(h.pc))<<",\"operation\":"<<quote(h.write?"write":"read")
           <<",\"address\":"<<quote(hex(h.address))<<",\"width\":"<<h.width
           <<",\"objectBase\":"<<quote(hex(h.base))<<",\"generation\":"<<h.generation
           <<",\"type\":"<<quote(h.type)<<",\"field\":"<<quote(h.field)
           <<",\"classification\":"<<quote(h.classification)<<",\"highConfidence\":"<<(h.high?"true":"false")
           <<",\"count\":"<<h.count<<",\"expectedAddress\":"<<(h.exact?quote(hex(h.expected)):"null")
           <<",\"racerIndex\":"<<(h.index==UnknownIndex?"null":std::to_string(h.index))
           <<",\"oldOffset\":"<<h.oldOffset<<",\"newOffset\":"<<h.newOffset
           <<",\"oldSize\":"<<h.oldSize<<",\"newSize\":"<<h.newSize
           <<",\"origin\":"<<quote(h.native?"native":"translated-or-caller")
           <<",\"function\":"<<quote(h.function)<<",\"relocationRule\":"<<quote(h.rule)
           <<",\"phase\":"<<quote(h.phase)<<",\"lifetime\":"<<quote(h.lifetime)<<",\"translatedStack\":[";
        for(size_t i=0;i<h.stack.size();i++){if(i)out<<',';out<<quote(hex(h.stack[i]));}out<<"]}";
    }
    out<<"],\"unscopedStackEntries\":[";first=true;
    for(const auto& [pc,count]:s.unscopedStackSites){
        if(!std::exchange(first,false))out<<',';
        out<<"{\"guestPc\":"<<quote(hex(pc))<<",\"type\":"<<quote(s.stackSites.at(pc))<<",\"count\":"<<count<<'}';
    }
    out<<"]}\n";return out.str();
}
bool writeSnapshot(const std::string& path,const std::string& report){
    std::lock_guard lock(state().reportMutex);
    std::ofstream file(path);if(!file)return false;file<<report;return bool(file);
}
void validateField(const Field& f,uint32_t oldSize,uint32_t newSize){
    if(f.name.empty() || uint64_t(f.oldOffset)+f.oldSize>oldSize || uint64_t(f.newOffset)+f.newSize>newSize ||
       !f.oldCount || !f.newCount || !f.oldStride || !f.newStride ||
       uint64_t(f.oldCount)*f.oldStride>f.oldSize || uint64_t(f.newCount)*f.newStride>f.newSize)
        throw std::invalid_argument("Invalid retired-layout field extent");
}
}

void Configure(Options options){
    auto& s=state();std::lock_guard lock(s.mutex);s.options=std::move(options);
    if(s.options.mode!=Mode::Off)ensurePageIndex(s);
    enabled.store(s.options.mode!=Mode::Off,std::memory_order_release);
}
void RegisterType(const Type& type){
    if(type.name.empty()||!type.oldSize||!type.newSize)throw std::invalid_argument("Invalid retired-layout type");
    OwnedType copy{type.oldSize,type.newSize,{}};
    for(const auto& f:type.fields){validateField(f,type.oldSize,type.newSize);
        if(std::any_of(copy.fields.begin(),copy.fields.end(),[&](const auto& x){return x.name==f.name;}))
            throw std::invalid_argument("Duplicate retired-layout field");
        copy.fields.push_back({std::string(f.name),f.oldOffset,f.oldSize,f.oldCount,f.oldStride,
            f.newOffset,f.newSize,f.newCount,f.newStride,f.perRacer});}
    auto& s=state();std::lock_guard lock(s.mutex);
    if(s.types.contains(type.name))throw std::invalid_argument("Duplicate retired-layout type registration");
    s.types.emplace(type.name,std::move(copy));
}
bool HasType(std::string_view type){auto& s=state();std::lock_guard lock(s.mutex);return s.types.contains(type);}
void BindSite(const Site& site){
    if(!site.pc||site.type.empty()||site.field.empty())throw std::invalid_argument("Retired-layout site needs field intent");
    auto& s=state();std::lock_guard lock(s.mutex);
    if(s.sites.contains(site.pc))throw std::invalid_argument("Ambiguous retired-layout site binding");
    s.sites.emplace(site.pc,OwnedSite(site));
}
void BindStackSite(uint32_t pc,std::string_view type){
    auto& s=state();std::lock_guard lock(s.mutex);
    if(!pc||!s.types.contains(type)||s.stackSites.contains(pc))throw std::invalid_argument("Invalid stack lifetime binding");
    s.stackSites.emplace(pc,type);
    const auto index=(pc>>2)&0xffffu;
    s.stackPcFilter[index>>6].fetch_or(uint64_t(1)<<(index&63),std::memory_order_release);
}
uint64_t RegisterInstance(std::string_view type,uint32_t base,uint32_t bytes,std::string_view lifetime,uint32_t owner){
    auto& s=state();std::lock_guard lock(s.mutex);const auto found=s.types.find(type);
    if(!base||found==s.types.end())throw std::invalid_argument("Unknown retired-layout instance type/base");
    if(!bytes)bytes=found->second.newSize;
    if(uint64_t(base)+bytes>uint64_t(UINT32_MAX)+1)throw std::invalid_argument("Retired-layout instance wraps guest address space");
    for(const auto& [id,instance]:s.instances){(void)id;if(instance.base==base&&instance.type==type)
        throw std::invalid_argument("Retired-layout instance is already live; retire before reuse");}
    const auto generation=s.nextGeneration++;
    s.instances.emplace(generation,Instance{generation,std::string(type),base,bytes,owner,std::string(lifetime)});
    changePages(s,s.instances.at(generation),true);
    return generation;
}
void RetireInstance(uint64_t id){auto& s=state();std::lock_guard lock(s.mutex);retireMatching(s,[&](const Instance& x){return x.generation==id;});}
void RetireAllocation(uint32_t base){auto& s=state();std::lock_guard lock(s.mutex);retireMatching(s,[&](const Instance& x){return x.base==base;});}
void RetireOwner(uint32_t owner){if(!owner)return;auto& s=state();std::lock_guard lock(s.mutex);retireMatching(s,[&](const Instance& x){return x.owner==owner;});}
void RetireAllInstances(){auto& s=state();std::lock_guard lock(s.mutex);retireMatching(s,[](const Instance&){return true;});}
void SetPhase(std::string_view phase){auto& s=state();std::lock_guard lock(s.mutex);s.phase=phase;}
void SetExecutionContextProvider(uint32_t (*provider)()){contextProvider.store(provider,std::memory_order_relaxed);}
void RetireExecutionContext(uint32_t id){
    auto& s=state();std::lock_guard lock(s.mutex);
    retireMatching(s,[&](const Instance& x){return x.frame && x.context==id;});
    if(cachedContext && cachedContextKey==id)cachedContext=nullptr;
    contexts.erase(id);
}
void SetGuestPc(uint32_t pc){
    if(!enabled.load(std::memory_order_relaxed))return;
    auto& current=context();
    if(current.extendedSite){current.site={};current.extendedSite=false;}
    current.site.pc=pc;
}
ScopedSite::ScopedSite(Site site):previous_(context().site),context_(contextKey()){auto& current=contexts[context_];current.site=site;current.extendedSite=hasFieldIntent(site);}
ScopedSite::~ScopedSite(){auto& current=contexts[context_];current.site=previous_;current.extendedSite=hasFieldIntent(previous_);}
FunctionScope::FunctionScope(uint32_t entry):previous_{},context_(0),active_(enabled.load(std::memory_order_relaxed)){
    if(active_){context_=contextKey();previous_=contexts[context_].site;auto& s=state();{std::lock_guard lock(s.mutex);frame_=s.nextFrame++;}
        contexts[context_].stack.push_back(entry);contexts[context_].frames.push_back(frame_);SetGuestPc(entry);}
}
FunctionScope::~FunctionScope(){if(active_){
    auto& s=state();{std::lock_guard lock(s.mutex);retireMatching(s,[&](const Instance& x){return x.frame==frame_;});}
    auto& saved=contexts[context_];if(!saved.stack.empty())saved.stack.pop_back();
    if(!saved.frames.empty())saved.frames.pop_back();saved.site=previous_;saved.extendedSite=hasFieldIntent(previous_);
}}

void Observe(uint32_t address,uint32_t width,Operation op){
    if(!enabled.load(std::memory_order_acquire)||!width)return;
    auto& s=state();auto& counters=threadCounters(s);counters.observed.fetch_add(1,std::memory_order_relaxed);
    // A first prologue write can create its watch on an otherwise unwatched
    // page. The conservative PC filter must bypass the address fast path.
    if(!touchesWatchedPage(s,address,width) &&
       !(op==Operation::Write && width==4 && mayStartStack(s,context().site.pc))){
        counters.filtered.fetch_add(1,std::memory_order_relaxed);return;
    }
    bool stop=false, changed=false;std::string message,report,path;
    {
        std::lock_guard lock(s.mutex);bool watched=false;Site site=context().site;
        // A checked stwu r1,-Frame.size(r1) writes at the new frame base.
        // Its watch belongs to this invocation, not the reused stack address.
        if(op==Operation::Write && width==4){
            if(const auto binding=s.stackSites.find(site.pc);binding!=s.stackSites.end()){
                if(context().frames.empty() || context().stack.empty() || context().stack.back()!=site.pc){
                    // An inlined or non-entry prologue has no matching C++
                    // invocation lifetime. Never retain its dead stack as if
                    // it belonged to the enclosing function's longer lifetime.
                    const auto count=++s.unscopedStackSites[site.pc];
                    changed|=(count&(count-1))==0;
                }else{
                    const auto frame=context().frames.back();
                    const bool live=std::any_of(s.instances.begin(),s.instances.end(),[&](const auto& x){return x.second.frame==frame;});
                    if(!live){const auto id=s.nextGeneration++;
                        s.instances.emplace(id,Instance{id,binding->second,address,s.types.at(binding->second).newSize,0,"translated stack frame",frame,contextKey()});
                        changePages(s,s.instances.at(id),true);}
                }
            }
        }
        if(site.type.empty())if(const auto binding=s.sites.find(site.pc);binding!=s.sites.end())site=binding->second.view();
        for(const auto& [id,instance]:s.instances){
            if(site.objectBase&&site.objectBase!=instance.base)continue;
            if(!site.type.empty()&&site.type!=instance.type)continue;
            const auto& type=s.types.at(instance.type);
            if(!overlaps(instance.base,std::max(instance.bytes,type.oldSize),address,width))continue;
            if(!watched){++s.watchedAccesses;watched=true;}
            for(const auto& field:type.fields){
                if(field.oldOffset==field.newOffset && field.oldStride==field.newStride)continue;
                const bool known=!site.type.empty()&&site.field==field.name;
                // Without an explicit base, overlapping instances of the same
                // type cannot establish which object this instruction targets.
                const bool unique=site.objectBase || std::count_if(s.instances.begin(),s.instances.end(),[&](const auto& x){
                    return x.second.type==instance.type && overlaps(x.second.base,x.second.bytes,address,width);
                })==1;
                if(!site.field.empty()&&!known)continue;
                const uint64_t oldStart=uint64_t(instance.base)+field.oldOffset,newStart=uint64_t(instance.base)+field.newOffset;
                bool stale=false,exact=false;uint32_t expected=0,index=UnknownIndex;
                if(known&&site.index!=UnknownIndex){
                    index=site.index;
                    const uint64_t oldAddress=oldStart+uint64_t(index)*field.oldStride+site.memberOffset;
                    const uint64_t newAddress=newStart+uint64_t(index)*field.newStride+site.memberOffset;
                    if(address==newAddress)continue;
                    stale=unique&&address==oldAddress&&oldAddress!=newAddress;
                    if(newAddress<=UINT32_MAX){expected=uint32_t(newAddress);exact=stale;}
                }else{
                    if(contains(newStart,field.newSize,address,width))continue;
                    // A known field consumer may retain its old base while
                    // indexing racers beyond the original twelve entries.
                    // Project the old stride over the new count, but only
                    // outside the intended current field; unknown accesses
                    // retain the smaller historical range and stay leads.
                    const uint64_t oldExtent=known?std::max<uint64_t>(field.oldSize,uint64_t(field.newCount)*field.oldStride):field.oldSize;
                    if(!contains(oldStart,oldExtent,address,width))continue;
                    stale=known&&unique;
                    if(stale){
                        const uint32_t relative=uint32_t(address-oldStart);index=relative/field.oldStride;
                        const uint64_t adjusted=newStart+uint64_t(index)*field.newStride+relative%field.oldStride;
                        exact=field.oldStride==field.newStride&&adjusted<=UINT32_MAX;
                        if(exact)expected=uint32_t(adjusted);
                    }
                }
                if(!stale&&!s.options.includeAmbiguous)continue;
                // Without field intent, an overlapping current array is never a
                // proven bug. Keep such observations optional and non-fatal.
                const std::string key=hex(site.pc)+":"+(op==Operation::Write?"w:":"r:")+instance.type+":"+field.name+":"+std::to_string(width)+":"+(stale?"stale":"lead");
                if(auto hit=s.hits.find(key);hit!=s.hits.end()){const auto count=++hit->second.count;changed|=(count&(count-1))==0;}
                else {changed=true;s.hits.emplace(key,Hit{site.pc,address,width,instance.base,expected,field.perRacer?index:UnknownIndex,id,1,
                    op==Operation::Write,stale,site.native,exact,instance.type,field.name,std::string(site.rule),
                    std::string(site.function),s.phase,instance.lifetime,stale?"stale-field-access":"unattributed-overlap-lead",
                    field.oldOffset,field.newOffset,field.oldSize,field.newSize,context().stack});}
                if(stale&&s.options.mode==Mode::Strict){stop=true;message="Retired guest layout access at "+hex(site.pc)+" to "+instance.type+"."+field.name+" ("+hex(address)+")";}
            }
        }
        if((stop||changed)&&!s.options.reportPath.empty()){report=reportLocked(s);path=s.options.reportPath;}
    }
    if(!report.empty())writeSnapshot(path,report);
    if(stop)throw StaleAccess(message);
}
std::string ReportJson(){auto& s=state();std::lock_guard lock(s.mutex);return reportLocked(s);}
void WriteReport(){auto& s=state();std::string path,report;{std::lock_guard lock(s.mutex);path=s.options.reportPath;report=reportLocked(s);}if(path.empty())return;if(!writeSnapshot(path,report))throw std::runtime_error("Cannot write retired-layout audit report: "+path);}
void Reset(){
    enabled.store(false,std::memory_order_relaxed);auto& s=state();std::lock_guard lock(s.mutex);
    retireMatching(s,[](const Instance&){return true;});
    for(auto& bits:s.stackPcFilter)bits.store(0,std::memory_order_relaxed);
    s.options={};s.types.clear();s.sites.clear();s.stackSites.clear();s.unscopedStackSites.clear();s.hits.clear();
    s.nextGeneration=1;s.nextFrame=1;s.watchedAccesses=0;
    for(auto& counter:s.counters){counter.observed=0;counter.filtered=0;}
    s.phase.clear();cachedContext=nullptr;contexts.clear();contextProvider.store(nullptr,std::memory_order_relaxed);
}
bool ParseArgument(std::string_view arg){
    auto& s=state();Options options;{std::lock_guard lock(s.mutex);options=s.options;}
    if(arg.starts_with("--layout-audit=")){
#ifndef MKW_ENABLE_LAYOUT_ACCESS_AUDIT
        if(arg!="--layout-audit=off")throw std::invalid_argument("Layout auditing requires a build with MKW_ENABLE_LAYOUT_ACCESS_AUDIT=ON");
        options.mode=Mode::Off;
#else
        const auto mode=arg.substr(15);if(mode=="log")options.mode=Mode::Log;else if(mode=="strict")options.mode=Mode::Strict;else if(mode=="off")options.mode=Mode::Off;else throw std::invalid_argument("Layout audit mode must be off, log or strict");
        if(options.reportPath.empty())options.reportPath="layout-access-audit.json";
#endif
    }else if(arg.starts_with("--layout-audit-report="))options.reportPath=arg.substr(22);
    else if(arg=="--layout-audit-ambiguous")options.includeAmbiguous=true;
    else return false;
    Configure(std::move(options));return true;
}
}
