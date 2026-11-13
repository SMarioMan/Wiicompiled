using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class NetworkItemStorageTests
{
    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(32)] [InlineData(48)] [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void OriginalItemStorageAcknowledgementsAndSortingCoverTheExpandedField(int players)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName ?? throw new DirectoryNotFoundException();
        var manifest=Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json");
        using var doc=JsonDocument.Parse(File.ReadAllText(manifest));
        var layout=new GuestLayouts(doc.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!,players);
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));
        var translator=new FunctionTranslator(image,layoutRelocations:GuestLayoutRelocations.Read(manifest,players));
        var temp=Path.Combine(Path.GetTempPath(),"mkw-network-items-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try {
            uint[] entries=new uint[]{0x8065cd74,0x8065d030,0x8065e8f4,0x8065e9c4,0x8065f2cc,0x80660364,0x80006038,0x80005f84,0x80005f34}.Concat(map.Addresses.Where(pc=>pc>=0x800aeec0 && pc<0x800af148)).Distinct().ToArray();uint[] fixtures={0x80229a48,0x80650398,0x80650420,0x80650490,0x806558d0,0x80655ec4};
            var sources=new List<string>();
            foreach(var pc in entries){var file=Path.Combine(temp,$"{pc:X8}.cpp");File.WriteAllText(file,translator.Translate(pc,new TranslationOptions(EnableLeafInlining:false,KnownFunctionEntryPoints:map.Addresses.ToHashSet(),LeafInliningBlockedTargets:fixtures.ToHashSet())).CxxCode);sources.Add(file);}
            var declarations=string.Join("\n",entries.Select(pc=>$"extern \"C\" void func_{pc:X8}(CpuContext*);"));
            var registrations=string.Join("\n",entries.Select(pc=>$"entry(0x{pc:X8},func_{pc:X8});"));
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <cstdio>
                #include <set>
                #include <array>
                #include <cstring>
                #include <cstdlib>
                #include <bit>
                {{declarations}}
                constexpr uint32_t n={{players}},root=0x81010000,controller=0x81050000,manager=0x81080000,
                    racedata=0x810a0000,info=0x810c0000,pool=0x81100000,output=0x81150000,stack=0x81f00000,
                    size={{layout.Symbol("RKNetItemHandler.size")}},received={{layout.Symbol("RKNetItemHandler.received.offset")}},
                    statuses={{layout.Symbol("RKNetItemHandler.statuses.offset")}},frames={{layout.Symbol("RKNetItemHandler.storedFrames.offset")}},
                    currentSub={{layout.Symbol("RKNetController.currentSub.offset")}},subs={{layout.Symbol("RKNetController.subs.offset")}},
                    subStride={{layout.Symbol("RKNetSub.size")}},users={{layout.Symbol("RKNetSub.users.offset")}},
                    send={{layout.Symbol("RKNetController.splitSend.offset")}},receiveRows={{layout.Symbol("RKNetController.splitReceived.offset")}},
                    rowStride={{layout.Symbol("RKNetPointerRow.size")}},indices={{layout.Symbol("RKNetController.sendIndices.offset")}},
                    receiveIndices={{layout.Symbol("RKNetController.receiveIndices.offset")}},pids={{layout.Symbol("RKNetController.playerAids.offset")}};
                static uint32_t allocations,locals=1,local=0,mask=0xffffffff;
                static std::set<uint32_t> delivered;
                #define CHECK(x) do{if(!(x)){std::fprintf(stderr,"network item fixture line %d n=%u local=%u locals=%u: %s\n",__LINE__,n,local,locals,#x);std::abort();} }while(false)
                uint32_t local_pid(uint32_t slot){return local?n-locals+slot:slot;}
                bool is_local(uint32_t p){for(uint32_t i=0;i<locals;i++)if(local_pid(i)==p)return true;return false;}
                void allocate(CpuContext* c){CHECK(c->gpr[3]==size && !allocations++);c->gpr[3]=root;}
                void connected(CpuContext* c){CHECK(c->gpr[3]==manager);auto p=c->gpr[4];if(p==0xffffffff){c->gpr[3]=0;return;}
                    CHECK(p<n);auto aid=Memory::Read8(controller+pids+p);c->gpr[3]=n>32?1:aid<32 && (mask&(1u<<aid));}
                void local_test(CpuContext* c){CHECK(c->gpr[3]==manager && c->gpr[4]<n);c->gpr[3]=is_local(c->gpr[4]);}
                void local_index(CpuContext* c){CHECK(c->gpr[3]==manager && c->gpr[4]<n);
                    for(uint32_t i=0;i<locals;i++)if(c->gpr[4]==local_pid(i)){c->gpr[3]=i;return;}CHECK(false);}
                void local_player(CpuContext* c){CHECK(c->gpr[3]==controller && c->gpr[4]<2);c->gpr[3]=c->gpr[4]<locals?local_pid(c->gpr[4]):255;}
                void copy(CpuContext* c){CHECK(c->gpr[3]>=pool && c->gpr[3]<pool+n*16 && (c->gpr[3]-pool)%16==0);
                    auto aid=(c->gpr[3]-pool)/16;CHECK(aid!=local && (mask&(1u<<aid)));CHECK(c->gpr[4]==root && c->gpr[5]==locals*8);
                    auto source=aid;CHECK(source<n && !is_local(source));
                    for(uint32_t i=0;i<locals;i++){
                        const uint8_t expected[8]={uint8_t(0x80+i),0,20,1,0,1,uint8_t(source+1),0};
                        CHECK(!std::memcmp(expected,Memory::GetPointer(root+i*8,8),8));
                    }
                    CHECK(delivered.insert(aid).second);}
                void entry(uint32_t pc,void(*f)(CpuContext*)){TranslatedFunctionInfo i{};i.address=pc;i.name="network item fixture";i.rawCpuInvoker=f;i.entryPoint=reinterpret_cast<void*>(f);TranslatedFunctionRegistry::Register(i);}
                int main(int argc,char** argv){
                    CHECK(argc==2);Memory::Init(32*1024*1024);auto f=std::fopen(argv[1],"rb");CHECK(f);
                    CHECK(std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},f)=={{image.Memory.Length}});std::fclose(f);
                    {{registrations}}
                    entry(0x80229a48,allocate);entry(0x80650398,connected);entry(0x80650420,local_test);
                    entry(0x80650490,local_index);entry(0x806558d0,local_player);entry(0x80655ec4,copy);
                    Memory::Write32(0x809bd918,controller);Memory::Write32(0x809bd790,manager);Memory::Write32(0x809b8f68,racedata);Memory::Write32(0x809b8f70,info);
                    Memory::Write8(racedata+{{layout.Symbol("Racedata.race.offset")}}+4,n);Memory::Write32(info+0x20,800);
                    CpuContext c{};c.gpr[1]=stack;c.gpr[13]=0x80388880;c.gpr[2]=0x8038ac20;CpuContextScope scope(&c);
                    for(uint32_t r=14;r<32;r++)c.gpr[r]=0x12340000+r;
                    std::array<uint8_t,size> clean{},expected;
                    for(uint32_t i=0;i<2;i++)clean[i*8+1]=clean[i*8+2]=20;
                    for(uint32_t p=0;p<n;p++)clean[received+p*8+1]=clean[received+p*8+2]=20;
                    for(unsigned kind=0;kind<2;kind++){
                        std::memset(Memory::GetPointer(root-16,size+32),0xa5,size+32);c.gpr[3]=root;
                        if(kind==0){Memory::Write32(0x809bd950,0);func_8065CD74(&c);CHECK(Memory::Read32(0x809bd950)==root && allocations==1);}
                        else func_8065D030(&c);
                        CHECK(!std::memcmp(clean.data(),Memory::GetPointer(root,size),size));
                        for(uint32_t i=0;i<16;i++)CHECK(Memory::Read8(root-16+i)==0xa5 && Memory::Read8(root+size+i)==0xa5);
                    }
                    // Sort the entire active field using the real guest list helpers.
                    local=0;locals=1;mask=n<=32?(0xffffffffu>>(32-n)):0xffffffff;
                    std::memcpy(Memory::GetPointer(root,size),clean.data(),size);
                    Memory::Write32(0x809bee98+4,42);
                    for(uint32_t p=0;p<n;p++){
                        Memory::Write8(controller+pids+p,p);auto at=is_local(p)?root:root+received+p*8;
                        Memory::Write8(at+1,0);Memory::Write8(at+3,1);Memory::Write32(root+frames+p*4,(n-p)*16);
                    }
                    std::memset(Memory::GetPointer(output-16,n*4+32),0xa5,n*4+32);
                    expected.fill(0);std::memcpy(expected.data(),Memory::GetPointer(root,size),size);
                    c.gpr[3]=root;c.gpr[4]=42;c.gpr[5]=output;func_80660364(&c);CHECK(c.gpr[3]==n);
                    for(uint32_t p=0;p<n;p++)CHECK(Memory::Read32(output+p*4)==n-1-p);
                    CHECK(!std::memcmp(expected.data(),Memory::GetPointer(root,size),size));
                    for(uint32_t i=0;i<16;i++)CHECK(Memory::Read8(output-16+i)==0xa5 && Memory::Read8(output+n*4+i)==0xa5);
                    // Import one- and two-player console payloads through both receive rows.
                    if(n<=32)for(uint32_t current=0;current<2;current++)for(uint32_t selectedLocal:{0u,n-1})
                        for(locals=1;locals<=2;locals++)for(bool sparse:{false,true})for(bool valid:{false,true})for(uint32_t previous:{0u,1u}){
                            local=selectedLocal;expected=clean;
                            std::array<uint32_t,n> mapping{},counts{};
                            for(uint32_t p=0;p<n;p++)mapping[p]=is_local(p)?local:p;
                            const uint32_t remote=local?0:n-1,paired=local?1:n-2;mapping[paired]=remote;
                            for(auto aid:mapping)counts[aid]++;
                            mask=sparse?((1u<<local)|(1u<<remote)):0;
                            if(!sparse)for(uint32_t aid=0;aid<n;aid++)if(counts[aid])mask|=1u<<aid;
                            auto sub=controller+subs+current*subStride;Memory::Write32(controller+currentSub,current);Memory::Write32(sub+16,mask);
                            Memory::Write8(sub+32,locals);Memory::Write8(sub+33,local);
                            for(uint32_t p=0;p<n;p++){
                                Memory::Write8(controller+pids+p,mapping[p]);expected[received+p*8+3]=previous;
                                auto value=0x12340000+p;for(uint32_t b=0;b<4;b++)expected[frames+p*4+b]=value>>(24-b*8);
                            }
                            for(uint32_t i=0;i<6;i++)Memory::Write8(controller+pids+n+i,remote);
                            for(uint32_t aid=0;aid<n;aid++){
                                CHECK(counts[aid]<=2);Memory::Write8(sub+users+aid*4,counts[aid]);Memory::Write32(controller+receiveIndices+aid*32+24,aid%2);
                                for(uint32_t row=0;row<2;row++){
                                    auto split=pool+0x20000+aid*1024+row*512,holder=split+32,payload=holder+16;
                                    Memory::Write32(controller+receiveRows+row*rowStride+aid*4,split);Memory::Write32(split+24,holder);
                                    Memory::Write32(holder,payload);Memory::Write32(holder+8,valid?counts[aid]*8:0);
                                    uint32_t slot=0;
                                    for(uint32_t p=0;p<n;p++)if(mapping[p]==aid){
                                        const uint8_t packet[8]={uint8_t(p+row*16),uint8_t(1+p%7),20,1,0,uint8_t(0x30+p),uint8_t(0x40+p),uint8_t(0x50+p)};
                                        std::memcpy(Memory::GetPointer(payload+slot*8,8),packet,8);slot++;
                                        if(valid && aid!=local && (mask&(1u<<aid)) && row==aid%2){
                                            std::memcpy(expected.data()+received+p*8,packet,8);
                                            if(!previous){auto value=uint32_t(packet[0])*8;for(uint32_t b=0;b<4;b++)expected[frames+p*4+b]=value>>(24-b*8);}
                                        }
                                    }
                                }
                            }
                            // Rebuild the pre-import state after forming the independently expected payload changes.
                            auto before=clean;
                            for(uint32_t p=0;p<n;p++){
                                before[received+p*8+3]=previous;auto value=0x12340000+p;
                                for(uint32_t b=0;b<4;b++)before[frames+p*4+b]=value>>(24-b*8);
                            }
                            std::memcpy(Memory::GetPointer(root,size),before.data(),size);c.gpr[3]=root;func_8065E9C4(&c);
                            CHECK(!std::memcmp(expected.data(),Memory::GetPointer(root,size),size));
                        }
                    if(n<=32)for(uint32_t current=0;current<2;current++)for(uint32_t selectedLocal:{0u,n-1})for(locals=1;locals<=2;locals++)for(bool sparse:{false,true}){
                        local=selectedLocal;
                        std::memcpy(Memory::GetPointer(root,size),clean.data(),size);
                        mask=sparse?((1u<<local)|(1u<<(local?0:n-1))):(0xffffffffu>>(32-n));
                        for(uint32_t p=0;p<n;p++)if(is_local(p) && p!=local)mask&=~(1u<<p);
                        auto sub=controller+subs+current*subStride;Memory::Write32(controller+currentSub,current);Memory::Write32(sub+16,mask);
                        Memory::Write8(sub+32,locals);Memory::Write8(sub+33,local);
                        for(uint32_t p=0;p<n;p++){
                            Memory::Write8(controller+pids+p,is_local(p)?local:p);Memory::Write32(root+statuses+p*4,1);
                            Memory::Write8(root+received+p*8,p+1);Memory::Write8(root+received+p*8+3,1);
                            Memory::Write32(controller+indices+p*4,p%2);auto split=pool+0x1000+p*32;
                            Memory::Write32(controller+send+(p%2)*rowStride+p*4,split);Memory::Write32(split+24,pool+p*16);
                        }
                        for(uint32_t i=0;i<locals;i++){Memory::Write8(root+i*8,0x80+i);Memory::Write8(root+i*8+1,0);Memory::Write8(root+i*8+3,1);}
                        // A missing second console player must not match bytes beyond the player-AID array.
                        for(uint32_t i=0;i<6;i++)Memory::Write8(controller+pids+n+i,local?0:n-1);
                        delivered.clear();c.gpr[3]=root;func_8065E8F4(&c);
                        CHECK(delivered.size()==std::popcount(mask)-1);CHECK(delivered.contains(local?0:n-1));
                    }
                    CHECK(c.gpr[1]==stack);for(uint32_t r=14;r<32;r++)CHECK(c.gpr[r]==0x12340000+r);
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!) {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Network item compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){RedirectStandardError=true})!;var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(15000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Network item execution timed out");Assert.True(run.ExitCode==0,output.GetAwaiter().GetResult());
        } finally {Directory.Delete(temp,recursive:true);}
    }
}
