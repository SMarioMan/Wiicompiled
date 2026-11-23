using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class RoomPacketExportTests
{
    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(32)] [InlineData(33)] [InlineData(128)]
    [Trait("Requires", "USA game assets and Clang")]
    public void OriginalRoomExporterVisitsEveryConnectedAidIncludingSparseHighestAid(int players)
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
        var temp=Path.Combine(Path.GetTempPath(),"mkw-room-export-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try {
            uint[] entries={0x80656bc0,0x80020aa4};uint[] fixtures={0x801aacbc,0x806609d8,0x80655ec4};
            var sources=new List<string>();
            foreach(var pc in entries){var file=Path.Combine(temp,$"{pc:X8}.cpp");File.WriteAllText(file,translator.Translate(pc,new TranslationOptions(EnableLeafInlining:false,KnownFunctionEntryPoints:map.Addresses.ToHashSet(),LeafInliningBlockedTargets:fixtures.ToHashSet())).CxxCode);sources.Add(file);}
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <cstdio>
                #include <set>
                #include <cstdlib>
                extern "C" void func_80656BC0(CpuContext*);
                extern "C" void func_80020AA4(CpuContext*);
                constexpr uint32_t n={{players}},root=0x81010000,controller=0x81050000,pool=0x81100000,dest=0x81200000,stack=0x81f00000,
                    send={{layout.Symbol("RKNetRoomHandler.send.offset")}},lastAid={{layout.Symbol("RKNetRoomHandler.tail.offset+RKNetRoomTail.lastAid.offset")}},
                    lastTime={{layout.Symbol("RKNetRoomHandler.tail.offset+RKNetRoomTail.lastTime.offset")}},currentSub={{layout.Symbol("RKNetController.currentSub.offset")}},
                    subs={{layout.Symbol("RKNetController.subs.offset")}},subStride={{layout.Symbol("RKNetSub.size")}},rows={{layout.Symbol("RKNetController.splitSend.offset")}},rowStride={{layout.Symbol("RKNetPointerRow.size")}},
                    indices={{layout.Symbol("RKNetController.sendIndices.offset")}};
                static uint32_t aid=0,prepared=0;static std::set<uint32_t> delivered;
                #define CHECK(x) do{if(!(x)){std::fprintf(stderr,"room export check failed at %d n=%u aid=%u\n",__LINE__,n,aid);std::abort();} }while(false)
                void now(CpuContext* c){c->gpr[3]=0;c->gpr[4]=60750000;}
                void user(CpuContext* c){aid=c->gpr[4];CHECK(aid<n && c->gpr[3]==dest+0x1000);++prepared;}
                void copy(CpuContext* c){CHECK(c->gpr[3]==dest+aid*16 && c->gpr[4]==root+send+aid*4 && c->gpr[5]==4);
                    CHECK(Memory::Read32(c->gpr[4])==0x01000300+aid);delivered.insert(aid);}
                void entry(uint32_t pc,void(*f)(CpuContext*)){TranslatedFunctionInfo i{};i.address=pc;i.name="room export fixture";i.rawCpuInvoker=f;i.entryPoint=reinterpret_cast<void*>(f);TranslatedFunctionRegistry::Register(i);}
                int main(int argc,char** argv){
                    CHECK(argc==2);Memory::Init(32*1024*1024);auto file=std::fopen(argv[1],"rb");CHECK(file);
                    CHECK(std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},file)=={{image.Memory.Length}});std::fclose(file);
                    entry(0x80020aa4,func_80020AA4);entry(0x801aacbc,now);entry(0x806609d8,user);entry(0x80655ec4,copy);
                    Memory::Write32(0x800000f8,243000000);Memory::Write32(0x809bd918,controller);Memory::Write32(0x809bd958,dest+0x1000);
                    CpuContext c{};c.gpr[1]=stack;c.gpr[13]=0x80388880;c.gpr[2]=0x8038ac20;CpuContextScope scope(&c);
                    for(uint32_t r=14;r<32;r++)c.gpr[r]=0x12340000+r;
                    for(unsigned current=0;current<2;current++)for(unsigned local:{0u,n-1})for(bool sparse:{false,true}){
                        const uint32_t sub=controller+subs+current*subStride;
                        Memory::Write32(controller+currentSub,current);Memory::Write8(sub+33,local);
                        const uint32_t remote=local?0:n-1;
                        auto lowBit=[](uint32_t id){return id<32?1u<<id:0u;};
                        const uint32_t fullMask=uint32_t((uint64_t{1}<<(n<32?n:32))-1);
                        Memory::Write32(sub+16,sparse?lowBit(local)|lowBit(remote):fullMask);
                        if (n>32) {
                            constexpr uint32_t sdk=0x81500000, match=sdk+0x360;
                            Memory::Write32(0x80381f70,sdk);
                            Memory::Write32(0x80381f8c,match);
                            Memory::Write32(match+0x30,sparse?2:n);
                            for(uint32_t p=0;p<(sparse?2:n);p++)
                                Memory::Write8(match+0x38+p*0x30+0x16,sparse?(p?remote:local):p);
                        }
                        for(uint32_t p=0;p<n;p++){
                            Memory::Write32(root+send+p*4,0x01000300+p);Memory::Write32(controller+indices+p*4,p%2);
                            Memory::Write32(controller+rows+(p%2)*rowStride+p*4,pool+p*32);Memory::Write32(pool+p*32+12,dest+p*16);
                        }
                        Memory::Write8(root+lastAid,local);delivered.clear();prepared=0;
                        for(uint32_t round=0;round<n;round++){
                            Memory::Write32(root+lastTime,0);Memory::Write32(root+lastTime+4,0);c.gpr[3]=root;func_80656BC0(&c);
                            CHECK(c.gpr[1]==stack);for(uint32_t r=14;r<32;r++)CHECK(c.gpr[r]==0x12340000+r);
                        }
                        CHECK(delivered.size()==(sparse?1:n-1));CHECK(!delivered.contains(local));CHECK(delivered.contains(remote));
                    }
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!) {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(45000);if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Room export compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){RedirectStandardError=true})!;var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(15000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Room export execution timed out");Assert.True(run.ExitCode==0,output.GetAwaiter().GetResult());
        } finally {Directory.Delete(temp,recursive:true);}
    }
}
