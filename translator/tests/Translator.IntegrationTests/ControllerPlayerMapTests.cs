using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class ControllerPlayerMapTests
{
    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(32)] [InlineData(48)] [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void OriginalControllerImportsAndResetsTheWholePlayerAidMap(int players)
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
        var temp=Path.Combine(Path.GetTempPath(),"mkw-controller-map-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try {
            uint[] entries={0x80655738,0x80655898},fixtures={0x8065c1dc,0x80658590};
            var sources=new List<string>();
            foreach(var pc in entries){var source=Path.Combine(temp,$"{pc:X8}.cpp");File.WriteAllText(source,translator.Translate(pc,new TranslationOptions(EnableLeafInlining:false,KnownFunctionEntryPoints:map.Addresses.ToHashSet(),LeafInliningBlockedTargets:fixtures.ToHashSet())).CxxCode);sources.Add(source);}
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <cstdio>
                #include <cstring>
                #include <cstdlib>
                #include <vector>
                extern "C" void func_80655738(CpuContext*);
                extern "C" void func_80655898(CpuContext*);
                constexpr uint32_t n={{players}},controller=0x81010000,rh1=0x81080000,selectHandler=0x81090000,
                    source=0x810a0000,stack=0x81f00000,size={{layout.Symbol("RKNetController.size")}},
                    aids={{layout.Symbol("RKNetController.playerAids.offset")}},
                    disconnectedAids={{layout.Symbol("RKNetController.disconnectedAids.offset")}},
                    disconnectedPlayers={{layout.Symbol("RKNetController.disconnectedPlayers.offset")}};
                #define CHECK(x) do{if(!(x)){std::fprintf(stderr,"player map n=%u line=%d: %s\n",n,__LINE__,#x);std::abort();} }while(false)
                unsigned rh1Calls=0,selectCalls=0;
                void rh1_map(CpuContext* c){CHECK(c->gpr[3]==rh1);++rh1Calls;c->gpr[3]=source;}
                void select_map(CpuContext* c){CHECK(c->gpr[3]==selectHandler);++selectCalls;c->gpr[3]=source;}
                void entry(uint32_t pc,void(*f)(CpuContext*)){TranslatedFunctionInfo i{};i.address=pc;i.name="controller map fixture";i.rawCpuInvoker=f;i.entryPoint=reinterpret_cast<void*>(f);TranslatedFunctionRegistry::Register(i);}
                int main(int argc,char** argv){
                    CHECK(argc==2);Memory::Init(32*1024*1024);auto f=std::fopen(argv[1],"rb");CHECK(f);
                    CHECK(std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},f)=={{image.Memory.Length}});std::fclose(f);
                    entry(0x8065c1dc,rh1_map);entry(0x80658590,select_map);
                    CpuContext c{};c.gpr[1]=stack;c.gpr[13]=0x80388880;c.gpr[2]=0x8038ac20;CpuContextScope scope(&c);
                    for(uint32_t r=14;r<32;r++)c.gpr[r]=0x12340000+r;
                    for(unsigned active:{2u,n})for(unsigned path=0;path<3;path++){
                        std::memset(Memory::GetPointer(controller-16,size+32),0xa5,size+32);
                        std::vector<uint8_t> expected(size,0xa5);
                        std::memset(expected.data()+disconnectedAids,0,4);std::memset(expected.data()+disconnectedPlayers,0,4);
                        Memory::Write32(0x809bd940,path==0?rh1:0);Memory::Write32(0x809bd930,path<=1?selectHandler:0);
                        for(unsigned p=0;p<n;p++){auto aid=p<active?p%32:255;Memory::Write8(source+p,aid);expected[aids+p]=path<2?aid:255;}
                        rh1Calls=selectCalls=0;c.gpr[3]=controller;func_80655738(&c);
                        CHECK(rh1Calls==(path==0) && selectCalls==(path==1));
                        for(unsigned p=0;p<n;p++)CHECK(Memory::Read8(controller+aids+p)==expected[aids+p]);
                        CHECK(!std::memcmp(expected.data(),Memory::GetPointer(controller,size),size));
                        std::memset(expected.data()+aids,255,n);c.gpr[3]=controller;func_80655898(&c);
                        CHECK(!std::memcmp(expected.data(),Memory::GetPointer(controller,size),size));
                        for(unsigned i=0;i<16;i++)CHECK(Memory::Read8(controller-16+i)==0xa5 && Memory::Read8(controller+size+i)==0xa5);
                    }
                    CHECK(c.gpr[1]==stack);for(uint32_t r=14;r<32;r++)CHECK(c.gpr[r]==0x12340000+r);
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!) {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Controller map compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){RedirectStandardError=true})!;var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(15000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Controller map execution timed out");Assert.True(run.ExitCode==0,$"Controller map exited {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        } finally {Directory.Delete(temp,recursive:true);}
    }
}
