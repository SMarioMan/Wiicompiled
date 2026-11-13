using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class NetworkReceiveBufferTests
{
    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(32)]
    [Trait("Requires", "USA game assets and Clang")]
    public void OriginalConnectionPathsRegisterASeparateFullSizeReceiveBufferForEveryAid(int players)
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
        var temp=Path.Combine(Path.GetTempPath(),"mkw-receive-buffers-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try {
            uint[] entries={0x806544a0,0x80654714,0x80006038,0x80005f84,0x8065418c,0x80654450,0x80654474,0x80654494};uint[] fixtures={0x801ebe20,0x801ec0e4,0x800ccfc0,0x8065ac58,0x800d0cc8,0x800e8700,0x800d1cc8,0x800ce34c,0x800e86b0,0x800e86e0,0x805da04c,0x800d1358,0x800eec4c,0x800ef26c,0x800d16b4,0x806526e4};
            var sources=new List<string>();
            foreach(var pc in entries){var file=Path.Combine(temp,$"{pc:X8}.cpp");File.WriteAllText(file,translator.Translate(pc,new TranslationOptions(EnableLeafInlining:false,KnownFunctionEntryPoints:map.Addresses.ToHashSet(),LeafInliningBlockedTargets:fixtures.ToHashSet())).CxxCode);sources.Add(file);}
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <cstdio>
                #include <cstring>
                #include <cstdlib>
                #include <set>
                #include <array>
                extern "C" void func_806544A0(CpuContext*);
                extern "C" void func_80654714(CpuContext*);
                extern "C" void func_80005F84(CpuContext*);
                extern "C" void func_80006038(CpuContext*);
                extern "C" void func_8065418C(CpuContext*);
                extern "C" void func_80654450(CpuContext*);
                extern "C" void func_80654474(CpuContext*);
                extern "C" void func_80654494(CpuContext*);
                constexpr uint32_t n={{players}},root=0x81010000,stack=0x81f00000,
                    size={{layout.Symbol("RKNetController.size")}},buffers={{layout.Symbol("RKNetController.receivedPackets.offset")}},
                    status={{layout.Symbol("RKNetController.status.offset")}},currentSub={{layout.Symbol("RKNetController.currentSub.offset")}},
                    subs={{layout.Symbol("RKNetController.subs.offset")}},subStride={{layout.Symbol("RKNetSub.size")}},
                    packet={{layout.Symbol("RKNetPacket.size")}},bytes={{layout.Symbol("RKNetController.receivedPackets.size")}};
                static std::set<uint32_t> registered;
                #define CHECK(x) do{if(!(x)){std::fprintf(stderr,"receive buffer check at %d n=%u\n",__LINE__,n);std::abort();} }while(false)
                void sdk_buffer(CpuContext* c){auto aid=c->gpr[3];CHECK(aid<n && registered.insert(aid).second);
                    CHECK(c->gpr[4]==root+buffers+aid*packet && c->gpr[5]==packet);}
                void no_op(CpuContext*){}
                void success(CpuContext* c){c->gpr[3]=0;}
                void ready(CpuContext* c){c->gpr[3]=1;}
                void license(CpuContext* c){c->gpr[3]=0x81080000;}
                void login(CpuContext* c){CHECK(c->gpr[3]==0x81080068 && c->gpr[5]==0x8065418c && c->gpr[6]==root);}
                unsigned friendCalls=0,queryCalls=0,updates=0;
                void friends(CpuContext* c){CHECK(c->gpr[3]==0 && c->gpr[4]==0x80654450 && c->gpr[5]==root && c->gpr[6]==0x80654490 && c->gpr[7]==root);++friendCalls;}
                void query(CpuContext* c){CHECK(Memory::Read32(c->gpr[3])==0x81080068 && c->gpr[7]==root+status+372 && c->gpr[8]==root+status+376);++queryCalls;}
                void update(CpuContext* c){CHECK(c->gpr[3]==root);++updates;}
                void entry(uint32_t pc,void(*f)(CpuContext*)){TranslatedFunctionInfo i{};i.address=pc;i.name="SDK receive-buffer fixture";i.rawCpuInvoker=f;i.entryPoint=reinterpret_cast<void*>(f);TranslatedFunctionRegistry::Register(i);}
                int main(int argc,char** argv){
                    CHECK(argc==2);Memory::Init(32*1024*1024);auto f=std::fopen(argv[1],"rb");CHECK(f);
                    CHECK(std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},f)=={{image.Memory.Length}});std::fclose(f);
                    entry(0x80006038,func_80006038);entry(0x80005f84,func_80005F84);
                    for(uint32_t pc:{0x801ebe20u,0x800ccfc0u,0x800d0cc8u,0x800e8700u,0x800d1cc8u,0x800ce34cu,0x800e86e0u})entry(pc,no_op);
                    entry(0x801ec0e4,success);entry(0x8065ac58,ready);entry(0x800e86b0,sdk_buffer);entry(0x805da04c,license);entry(0x800d1358,login);
                    entry(0x800eec4c,ready);entry(0x800ef26c,query);entry(0x800d16b4,friends);entry(0x806526e4,update);
                    Memory::Write32(0x809bd938,0x81070000);Memory::Write32(0x809bd508,0x81090000);Memory::Write32(0x81090098,0x810a0000);
                    CpuContext c{};c.gpr[1]=stack;c.gpr[13]=0x80388880;c.gpr[2]=0x8038ac20;CpuContextScope scope(&c);
                    for(uint32_t r=14;r<32;r++)c.gpr[r]=0x12340000+r;
                    for(bool connect:{false,true}){
                        std::memset(Memory::GetPointer(root-16,size+32),0x55,size+32);registered.clear();c.gpr[3]=root;
                        if(connect)func_80654714(&c);else func_806544A0(&c);
                        CHECK(registered.size()==n);
                        for(uint32_t i=0;i<size+32;i++)CHECK(Memory::Read8(root-16+i)==((i>=buffers+16 && i<buffers+bytes+16)?0:0x55));
                        CHECK(c.gpr[1]==stack);for(uint32_t r=14;r<32;r++)CHECK(c.gpr[r]==0x12340000+r);
                    }
                    for(unsigned current=0;current<2;current++)for(unsigned locals=1;locals<=2;locals++){
                        std::memset(Memory::GetPointer(root-16,size+32),0x55,size+32);
                        Memory::Write32(root+currentSub,current);Memory::Write8(root+subs+current*subStride+32,locals);
                        std::array<uint8_t,size> expected;std::memcpy(expected.data(),Memory::GetPointer(root,size),size);
                        expected[0x28]=expected[0x29]=expected[0x2a]=0;expected[0x2b]=locals==2?2:3;
                        c.gpr[3]=0;c.gpr[5]=root;func_8065418C(&c);
                        CHECK(!std::memcmp(expected.data(),Memory::GetPointer(root,size),size));
                    }
                    CHECK(queryCalls==2 && friendCalls==2);
                    for(unsigned kind=0;kind<3;kind++){
                        std::memset(Memory::GetPointer(root-16,size+32),0x55,size+32);
                        std::array<uint8_t,size> expected;expected.fill(0x55);expected[status+368]=1;
                        c.gpr[3]=0;c.gpr[4]=kind==2?root:1;c.gpr[5]=root;
                        if(kind==0)func_80654450(&c);else if(kind==1)func_80654474(&c);else func_80654494(&c);
                        CHECK(!std::memcmp(expected.data(),Memory::GetPointer(root,size),size));
                    }
                    CHECK(updates==1);for(unsigned i=0;i<16;i++)CHECK(Memory::Read8(root-16+i)==0x55 && Memory::Read8(root+size+i)==0x55);
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!) {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(45000);if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Receive buffer compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){RedirectStandardError=true})!;var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(15000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Receive buffer execution timed out");Assert.True(run.ExitCode==0,$"Receive buffer/callback fixture exited {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        } finally {Directory.Delete(temp,recursive:true);}
    }
}
