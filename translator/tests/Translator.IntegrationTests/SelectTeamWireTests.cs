using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class SelectTeamWireTests
{
    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(48)] [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealTeamWritersSortShuffleCopyAndMapEveryConfiguredSlot(int players)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var manifest=Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json");
        using var doc=JsonDocument.Parse(File.ReadAllText(manifest));
        var layout=new GuestLayouts(doc.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!,players);
        var plan=GuestLayoutRelocations.Read(manifest,players);
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));
        var translator=new FunctionTranslator(image,layoutRelocations:plan);
        var temp=Path.Combine(Path.GetTempPath(),"mkw-select-teams-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var entries=new uint[]{0x80657ad0,0x80657bf0,0x806599f8,0x8065a354,0x8065a64c,0x8065a0f8,0x8065a2a0,
                0x800aeec0,0x800aeee0,0x800af120,0x800af0e0,0x800af100,0x800af070,0x800aef50};
            var sources=new List<string>();
            foreach(var pc in entries){var path=Path.Combine(temp,$"{pc:X8}.cpp");File.WriteAllText(path,translator.Translate(pc,
                new TranslationOptions(EnableLeafInlining:false,KnownFunctionEntryPoints:map.Addresses.ToHashSet())).CxxCode);sources.Add(path);}
            var declarations=string.Join("\n",entries.Select(pc=>$"extern \"C\" void func_{pc:X8}(CpuContext*);"));
            var registrations=string.Join("\n",entries.Select(pc=>$"entry(0x{pc:X8},func_{pc:X8});"));
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <algorithm>
                #include <array>
                #include <cstdio>
                #include <cstring>
                #include <numeric>
                #include <vector>
                {{{declarations}}}
                constexpr uint32_t capacity={{{players}}},words={{{layout.Symbol("RKNetSelectPacket.teams.count")}}},
                    self=0x81010000,teams=0x81020000,orderBuffer=0x81021000,compareBuffer=0x81022000,
                    controller=0x81040000,user=0x81070000,stack=0x81f00000,
                    size={{{layout.Symbol("RKNetSelectHandler.size")}}},packetSize={{{layout.Symbol("RKNetSelectPacket.size")}}},
                    send={{{layout.Symbol("RKNetSelectHandler.send.offset")}}},mapOffset={{{layout.Symbol("RKNetSelectPacket.playerAids.offset")}}},
                    teamOffset={{{layout.Symbol("RKNetSelectPacket.teams.offset")}}},controllerSize={{{layout.Symbol("RKNetController.size")}}},
                    subs={{{layout.Symbol("RKNetController.subs.offset")}}},subStride={{{layout.Symbol("RKNetSub.size")}}},
                    users={{{layout.Symbol("RKNetSub.users.offset")}}},currentSub={{{layout.Symbol("RKNetController.currentSub.offset")}}},
                    receivedUser={{{layout.Symbol("RKNetUserHandler.received.offset")}}},userSize={{{layout.Symbol("RKNetUserHandler.size")}}};
                uint32_t rngCalls=0;
                void entry(uint32_t address,void(*fn)(CpuContext*)){
                    TranslatedFunctionInfo i{};i.address=address;i.name="SELECT team fixture";i.rawCpuInvoker=fn;
                    i.entryPoint=reinterpret_cast<void*>(fn);TranslatedFunctionRegistry::Register(i);}
                void rng_init(CpuContext*){}
                void rng_destroy(CpuContext*){}
                void rng_limited(CpuContext* c){if(!c->gpr[4] || c->gpr[4]>capacity)std::abort();rngCalls++;c->gpr[3]=c->gpr[4]==2?1:0;}
                void local_id(CpuContext* c){if(c->gpr[3]!=controller)std::abort();c->gpr[3]=255;}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);auto f=std::fopen(argv[1],"rb");if(!f)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{{image.Memory.Length}}}),1,{{{image.Memory.Length}}},f);
                    std::fclose(f);if(loaded!={{{image.Memory.Length}}})return 3;
                    {{{registrations}}}
                    entry(0x8054f444,rng_init);entry(0x8054f518,rng_destroy);entry(0x8054f5ac,rng_limited);entry(0x806558d0,local_id);
                    Memory::Write32(0x809bd918,controller);Memory::Write32(0x809bd958,user);
                    CpuContext c{};CpuContextScope scope(&c);
                    auto setup=[&](){
                        c={};c.gpr[1]=stack;c.gpr[2]=0x8038ac20;c.gpr[13]=0x80388880;c.lr=0x80006000;
                        for(uint32_t r=14;r<32;r++)c.gpr[r]=0x12340000+r;
                        Memory::Write32(stack-0x4004,0xdeadbeef);Memory::Write32(stack+8,0xcafebabe);
                    };
                    auto preserved=[&](){if(c.gpr[1]!=stack || c.lr!=0x80006000 || Memory::Read32(stack-0x4004)!=0xdeadbeef || Memory::Read32(stack+8)!=0xcafebabe)return false;
                        for(uint32_t r=14;r<32;r++)if(c.gpr[r]!=0x12340000+r)return false;return true;};
                    for(uint32_t scenario=0;scenario<4;scenario++)for(uint32_t snapshot=0;snapshot<2;snapshot++){
                        std::memset(Memory::GetPointer(controller,controllerSize),0,controllerSize);
                        std::memset(Memory::GetPointer(user,userSize),0,userSize);
                        Memory::Write32(controller+currentSub,snapshot);
                        const auto sub=controller+subs+snapshot*subStride;
                        Memory::Write8(sub+33,255);
                        std::vector<uint32_t> active,counts(capacity,0),vr(capacity),br(capacity);
                        for(uint32_t aid=0;aid<capacity;aid++){
                            vr[aid]=4000+(capacity-aid)*7;br[aid]=4000+aid*5;
                            if(scenario==1 && (aid==0 || aid==std::min(capacity-1,31u)))counts[aid]=2;
                            if(scenario==2 && aid<32)counts[aid]=1;
                            if(scenario==3 && aid<32 && aid<(capacity+1)/2)counts[aid]=aid*2+1<capacity?2:1;
                            if(counts[aid])active.push_back(aid);
                            Memory::Write8(sub+users+aid*4,counts[aid]);
                            Memory::Write16(user+receivedUser+aid*192+184,vr[aid]);Memory::Write16(user+receivedUser+aid*192+186,br[aid]);
                        }
                        uint32_t mask=0;for(auto aid:active)mask|=1u<<aid;
                        Memory::Write32(sub+16,mask);Memory::Write32(sub+8,active.size());
                        for(uint32_t mode=1;mode<=4;mode++){
                            const bool privateMode=mode>=3;
                            Memory::Write32(self,mode);
                            std::vector<uint32_t> order(capacity);std::iota(order.begin(),order.end(),0);
                            if(privateMode){for(uint32_t end=capacity-1;end;end--){auto selected=end+1==2?1u:0u;std::swap(order[end],order[selected]);}}
                            else {auto& rating=mode==1?vr:br;std::stable_sort(order.begin(),order.end(),[&](auto a,auto b){return (counts[a]?rating[a]:0)>(counts[b]?rating[b]:0);});}
                            std::array<uint32_t,words> expected{};expected.fill(0xa5000000);
                            uint32_t red=0,blue=0,seen=0;bool lastRed=false;
                            for(auto aid:order)if(counts[aid]){
                                bool last=++seen==active.size();
                                bool groupBlue=privateMode?red>=blue:(red>blue || (red==blue && !lastRed));
                                for(uint32_t local=0;local<counts[aid];local++){
                                    bool useBlue=last?(privateMode?red>=blue:(red>blue || (red==blue && !lastRed))):groupBlue;
                                    if(useBlue){expected[aid/12]|=1u<<((aid%12)*2+local);blue++;lastRed=false;}else{red++;lastRed=true;}
                                }
                            }
                            Memory::Write32(teams-4,0xdeadbeef);Memory::Write32(teams+words*4,0xcafebabe);
                            for(uint32_t w=0;w<words;w++)Memory::Write32(teams+w*4,0xa5000000);
                            setup();c.gpr[3]=self;c.gpr[4]=teams;
                            if(privateMode)func_8065A64C(&c);else func_8065A354(&c);
                            if(!preserved())return 4;
                            for(uint32_t w=0;w<words;w++)if(Memory::Read32(teams+w*4)!=expected[w]){
                                std::fprintf(stderr,"teams scenario=%u mode=%u capacity=%u word=%u expected=%08x got=%08x\n",scenario,mode,capacity,w,expected[w],Memory::Read32(teams+w*4));return 5;}
                            if(Memory::Read32(teams-4)!=0xdeadbeef || Memory::Read32(teams+words*4)!=0xcafebabe)return 6;
                            // Real caller owns the output word array and copies all words into SELECT.
                            std::memset(Memory::GetPointer(self+send,packetSize),0x55,packetSize);
                            setup();c.gpr[3]=self;func_806599F8(&c);if(!preserved())return 7;
                            for(uint32_t w=0;w<words;w++){
                                auto target=(expected[w]&0x00ffffffu)|(w==0?0x80000000u:0);
                                if(Memory::Read32(self+send+teamOffset+w*4)!=target)return 8;
                            }
                            if(Memory::Read32(self+send+teamOffset-4)!=0x55555555 || Memory::Read32(self+send+teamOffset+words*4)!=0x55555555)return 9;
                        }
                        // Global player IDs must cover every local slot, followed by 0xff.
                        std::vector<uint32_t> expectedMap;
                        for(auto aid:active)for(uint32_t local=0;local<counts[aid];local++)expectedMap.push_back(aid);
                        expectedMap.resize(capacity,255);
                        std::memset(Memory::GetPointer(self+send+mapOffset-1,capacity+2),0x55,capacity+2);
                        setup();c.gpr[3]=self;func_8065A0F8(&c);if(!preserved())return 10;
                        for(uint32_t p=0;p<capacity;p++)if(Memory::Read8(self+send+mapOffset+p)!=expectedMap[p])return 11;
                        if(Memory::Read8(self+send+mapOffset-1)!=0x55 || Memory::Read8(self+send+mapOffset+capacity)!=0x55)return 12;
                        // A mismatch in each slot, especially the final partial six-byte group.
                        Memory::Write8(orderBuffer-1,0x11);Memory::Write8(orderBuffer+capacity,0x22);
                        Memory::Write8(compareBuffer-1,0x33);Memory::Write8(compareBuffer+capacity,0x44);
                        for(uint32_t p=0;p<capacity;p++){Memory::Write8(orderBuffer+p,p);Memory::Write8(compareBuffer+p,p);}
                        for(uint32_t changed=0;changed<=capacity;changed++){
                            if(changed<capacity)Memory::Write8(compareBuffer+changed,255);
                            setup();c.gpr[4]=orderBuffer;c.gpr[5]=compareBuffer;func_8065A2A0(&c);
                            if(!preserved() || c.gpr[3]!=(changed==capacity?1u:0u))return 13;
                            if(changed<capacity)Memory::Write8(compareBuffer+changed,changed);
                        }
                        if(Memory::Read8(orderBuffer-1)!=0x11 || Memory::Read8(orderBuffer+capacity)!=0x22 || Memory::Read8(compareBuffer-1)!=0x33 || Memory::Read8(compareBuffer+capacity)!=0x44)return 14;
                    }
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(120000);if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"SELECT team compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());}
            using var run=Process.Start(new ProcessStartInfo(exe,binary){RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(20000);if(!finished)run.Kill(entireProcessTree:true);
            Assert.True(finished,"SELECT team execution timed out");Assert.True(run.ExitCode==0,$"SELECT team returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
