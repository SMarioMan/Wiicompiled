using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class EventHandlerStorageTests
{
    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(32)] [InlineData(48)] [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void OriginalEventStorageAndPacketRowsCoverEveryParticipant(int players)
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
        var temp=Path.Combine(Path.GetTempPath(),"mkw-event-storage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try {
            uint[] entries={0x80650388,0x80656ecc,0x80657024,0x8065737c,0x80657578,0x806576c8,0x80006038,0x80005f84,0x80005f34};uint[] fixtures={0x80229a48,0x80655ec4};
            var sources=new List<string>();
            foreach(var pc in entries){var file=Path.Combine(temp,$"{pc:X8}.cpp");File.WriteAllText(file,translator.Translate(pc,new TranslationOptions(EnableLeafInlining:false,KnownFunctionEntryPoints:map.Addresses.ToHashSet(),LeafInliningBlockedTargets:fixtures.ToHashSet())).CxxCode);sources.Add(file);}
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
                extern "C" void func_80650388(CpuContext*);
                extern "C" void func_80656ECC(CpuContext*);
                extern "C" void func_80657024(CpuContext*);
                extern "C" void func_8065737C(CpuContext*);
                extern "C" void func_80657578(CpuContext*);
                extern "C" void func_806576C8(CpuContext*);
                extern "C" void func_80006038(CpuContext*);
                extern "C" void func_80005F84(CpuContext*);
                extern "C" void func_80005F34(CpuContext*);
                constexpr uint32_t n={{players}},root=0x81010000,controller=0x81050000,manager=0x81080000,pool=0x81100000,stack=0x81f00000,
                    size={{layout.Symbol("RKNetEventHandler.size")}},send={{layout.Symbol("RKNetEventHandler.send.offset")}},
                    received={{layout.Symbol("RKNetEventHandler.received.offset")}},times={{layout.Symbol("RKNetEventHandler.receiveTimes.offset")}},
                    remaining={{layout.Symbol("RKNetEventHandler.remaining.offset")}},
                    currentSub={{layout.Symbol("RKNetController.currentSub.offset")}},subs={{layout.Symbol("RKNetController.subs.offset")}},
                    subStride={{layout.Symbol("RKNetSub.size")}},rows={{layout.Symbol("RKNetController.splitSend.offset")}},
                    rowStride={{layout.Symbol("RKNetPointerRow.size")}},indices={{layout.Symbol("RKNetController.sendIndices.offset")}};
                static unsigned allocations;
                static std::set<uint32_t> delivered;
                static std::array<uint8_t,248> wire{};
                static uint32_t length;
                #define CHECK(x) do{if(!(x)){std::fprintf(stderr,"event storage check at %d n=%u\n",__LINE__,n);std::abort();} }while(false)
                void allocate(CpuContext* c){CHECK(c->gpr[3]==size && !allocations++);c->gpr[3]=root;}
                void copy(CpuContext* c){CHECK(c->gpr[3]>=pool && c->gpr[3]<pool+n*16 && (c->gpr[3]-pool)%16==0);
                    CHECK(c->gpr[4]==manager+0x94 && c->gpr[5]==length);CHECK(!std::memcmp(wire.data(),Memory::GetPointer(c->gpr[4],248),248));
                    CHECK(delivered.insert((c->gpr[3]-pool)/16).second);}
                void entry(uint32_t pc,void(*f)(CpuContext*)){TranslatedFunctionInfo i{};i.address=pc;i.name="event storage fixture";i.rawCpuInvoker=f;i.entryPoint=reinterpret_cast<void*>(f);TranslatedFunctionRegistry::Register(i);}
                int main(int argc,char** argv){
                    CHECK(argc==2);Memory::Init(32*1024*1024);auto f=std::fopen(argv[1],"rb");CHECK(f);
                    CHECK(std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},f)=={{image.Memory.Length}});std::fclose(f);
                    entry(0x806576c8,func_806576C8);entry(0x80006038,func_80006038);entry(0x80005f84,func_80005F84);entry(0x80005f34,func_80005F34);entry(0x80229a48,allocate);entry(0x80655ec4,copy);
                    CpuContext c{};c.gpr[1]=stack;c.gpr[13]=0x80388880;c.gpr[2]=0x8038ac20;CpuContextScope scope(&c);
                    for(uint32_t r=14;r<32;r++)c.gpr[r]=0x12340000+r;
                    std::array<uint8_t,size> expected;
                    auto word=[&](uint32_t at,uint32_t value){for(uint32_t b=0;b<4;b++)expected[at+b]=value>>(24-b*8);};
                    auto reset_entry=[&](uint32_t at){std::memset(expected.data()+at,0,29);expected[at+26]=16;};
                    for(unsigned kind=0;kind<3;kind++){
                        expected.fill(0x55);std::memset(Memory::GetPointer(root-16,size+32),0x55,size+32);
                        expected[0]=1;for(uint32_t slot=0;slot<24;slot++)reset_entry(send+slot*32);
                        for(uint32_t p=0;p<n;p++)for(uint32_t slot=0;slot<24;slot++)reset_entry(received+p*768+slot*32);
                        std::memset(expected.data()+times,0,n*96);word(remaining,224);
                        c.gpr[3]=root;if(kind==0){Memory::Write32(0x809bd928,0);func_80656ECC(&c);CHECK(Memory::Read32(0x809bd928)==root && allocations==1);}
                        else if(kind==1)func_80657024(&c);else func_8065737C(&c);
                        CHECK(!std::memcmp(expected.data(),Memory::GetPointer(root,size),size));
                        for(uint32_t i=0;i<16;i++)CHECK(Memory::Read8(root-16+i)==0x55 && Memory::Read8(root+size+i)==0x55);
                    }
                    Memory::Write32(0x809bd918,controller);Memory::Write32(0x809bd790,manager);
                    wire.fill(0);length=24;
                    for(uint32_t slot=0;slot<24;slot++){
                        uint32_t at=send+slot*32,type=slot%4,subtype=slot%3,payload=slot%4;
                        expected[at+25]=1;expected[at+26]=type;expected[at+27]=subtype;expected[at+28]=payload;
                        wire[slot]=type|(subtype<<5);
                        for(uint32_t k=0;k<payload;k++)wire[length++]=expected[at+4+k]=0x30+slot+k;
                    }
                    word(remaining,248-length);
                    if(n<=32)for(unsigned current=0;current<2;current++)for(unsigned local:{0u,n-1})for(bool sparse:{false,true}){
                        std::memcpy(Memory::GetPointer(root,size),expected.data(),size);
                        uint32_t mask=sparse?((1u<<local)|(1u<<(local?0:n-1))):(0xffffffffu>>(32-n));
                        Memory::Write32(controller+currentSub,current);Memory::Write32(controller+subs+current*subStride+16,mask);Memory::Write8(controller+subs+current*subStride+33,local);
                        for(uint32_t p=0;p<n;p++){
                            Memory::Write32(controller+indices+p*4,p%2);auto split=pool+0x1000+p*32;
                            Memory::Write32(controller+rows+(p%2)*rowStride+p*4,split);Memory::Write32(split+28,pool+p*16);
                        }
                        delivered.clear();c.gpr[3]=root;func_80657578(&c);
                        CHECK(delivered.size()==(sparse?1:n-1));CHECK(!delivered.contains(local));CHECK(delivered.contains(local?0:n-1));
                        CHECK(!std::memcmp(expected.data(),Memory::GetPointer(root,size),size));
                        c.gpr[3]=0;func_80650388(&c);CHECK(c.gpr[3]==248-length);
                    }
                    constexpr uint32_t racedata=0x810b0000,receiveRows={{layout.Symbol("RKNetController.splitReceived.offset")}},
                        receiveIndices={{layout.Symbol("RKNetController.receiveIndices.offset")}},playerAids={{layout.Symbol("RKNetController.playerAids.offset")}};
                    Memory::Write32(0x809b8f68,racedata);Memory::Write8(racedata+{{layout.Symbol("Racedata.race.offset")}}+4,n);
                    if(n<=32)for(unsigned current=0;current<2;current++)for(unsigned local:{0u,n-1})for(bool sparse:{false,true})for(bool valid:{false,true}){
                        expected.fill(0x55);std::memcpy(Memory::GetPointer(root,size),expected.data(),size);
                        uint32_t mask=sparse?((1u<<local)|(1u<<(local?0:n-1))):(0xffffffffu>>(32-n));
                        Memory::Write32(controller+currentSub,current);Memory::Write32(controller+subs+current*subStride+16,mask);Memory::Write8(controller+subs+current*subStride+33,local);
                        for(uint32_t p=0;p<n;p++){
                            auto aid=n-1-p;Memory::Write8(controller+playerAids+p,aid);Memory::Write32(controller+receiveIndices+aid*32+28,p%2);
                            for(uint32_t row=0;row<2;row++){
                                auto split=pool+0x20000+aid*1024+row*512,holder=split+32,payload=holder+16;
                                Memory::Write32(controller+receiveRows+row*rowStride+aid*4,split);Memory::Write32(split+28,holder);
                                Memory::Write32(holder,payload);Memory::Write32(holder+8,valid?24:0);
                                for(uint32_t slot=0;slot<24;slot++)Memory::Write8(payload+slot,(slot+p+row)%5==4?16:(slot+p+row)%4);
                            }
                            if(valid && aid!=local && (mask&(1u<<aid)))for(uint32_t slot=0;slot<24;slot++){
                                auto at=received+p*768+slot*32;expected[at+26]=(slot+p+p%2)%5==4?16:(slot+p+p%2)%4;
                                expected[at+27]=expected[at+28]=0;
                            }
                        }
                        c.gpr[3]=root;func_806576C8(&c);CHECK(!std::memcmp(expected.data(),Memory::GetPointer(root,size),size));
                    }
                    CHECK(c.gpr[1]==stack);for(uint32_t r=14;r<32;r++)CHECK(c.gpr[r]==0x12340000+r);
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!) {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(45000);if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Event storage compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){RedirectStandardError=true})!;var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(15000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Event storage execution timed out");Assert.True(run.ExitCode==0,output.GetAwaiter().GetResult());
        } finally {Directory.Delete(temp,recursive:true);}
    }
}
