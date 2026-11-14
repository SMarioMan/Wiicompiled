using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class VoteSubmissionTests
{
    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(32)] [InlineData(48)] [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void OriginalVoteSubmissionReadsMovedSelectionCountAndMiiGroup(int players)
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
        var temp=Path.Combine(Path.GetTempPath(),"mkw-vote-submit-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try {
            uint[] fixtures={0x80658598,0x80658638,0x80611884};
            var source=Path.Combine(temp,"vote.cpp");File.WriteAllText(source,translator.Translate(0x80612b6c,new TranslationOptions(EnableLeafInlining:false,KnownFunctionEntryPoints:map.Addresses.ToHashSet(),LeafInliningBlockedTargets:fixtures.ToHashSet())).CxxCode);
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <cstdio>
                #include <cstring>
                #include <cstdlib>
                extern "C" void func_80612B6C(CpuContext*);
                constexpr uint32_t n={{players}},page=0x81010000,stage=0x81030000,manager=0x81040000,
                    section=0x81050000,controller=0x81060000,vtable=0x81080000,stack=0x81f00000,
                    size={{layout.Symbol("MenuVotePage.size")}},cards={{layout.Symbol("MenuVotePage.controls.offset")}},
                    stride={{layout.Symbol("MenuVotePage.controls.stride")}},ids={{layout.Symbol("MenuVotePage.voterIds.offset")}},
                    tail={{layout.Symbol("MenuVotePage.tail.offset")}},
                    state={{layout.Symbol("MenuSelectStage.state.offset")}},miis={{layout.Symbol("MenuSelectStage.miis.offset")}};
                #define CHECK(x) do{if(!(x)){std::fprintf(stderr,"vote submission n=%u line=%d: %s\n",n,__LINE__,#x);std::abort();} }while(false)
                unsigned submitted=0;
                void type_info(CpuContext* c){c->gpr[3]=0x809bd5d0;}
                void vote(CpuContext* c){CHECK(c->gpr[4]==submitted && c->gpr[5]==0);c->gpr[3]=0;}
                void setup(CpuContext* c){CHECK(c->gpr[3]==page+cards+submitted*stride);CHECK(c->gpr[6]==stage+miis);CHECK(c->gpr[7]==submitted);CHECK(c->gpr[9]==1000+submitted);++submitted;}
                void entry(uint32_t pc,void(*f)(CpuContext*)){TranslatedFunctionInfo i{};i.address=pc;i.name="vote submission fixture";i.rawCpuInvoker=f;i.entryPoint=reinterpret_cast<void*>(f);TranslatedFunctionRegistry::Register(i);}
                int main(int argc,char** argv){
                    CHECK(argc==2);Memory::Init(32*1024*1024);auto f=std::fopen(argv[1],"rb");CHECK(f);
                    CHECK(std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},f)=={{image.Memory.Length}});std::fclose(f);
                    entry(0x80001000,type_info);entry(0x80658598,vote);entry(0x80658638,vote);entry(0x80611884,setup);
                    CpuContext c{};c.gpr[1]=stack;c.gpr[13]=0x80388880;c.gpr[2]=0x8038ac20;CpuContextScope scope(&c);
                    for(uint32_t r=14;r<32;r++)c.gpr[r]=0x12340000+r;
                    std::memset(Memory::GetPointer(page-16,size+32),0xa5,size+32);
                    Memory::Write32(0x809bd508,manager);Memory::Write32(manager,section);Memory::Write32(section+0x248,stage);
                    Memory::Write32(stage,vtable);Memory::Write32(vtable+0x60,0x80001000);Memory::Write32(stage+state,n);
                    Memory::Write32(0x809bd918,controller);Memory::Write32(controller+{{layout.Symbol("RKNetController.currentSub.offset")}},0);
                    Memory::Write32(page+tail,0);Memory::Write32(page+tail+4,0);
                    for(unsigned p=0;p<n;p++){
                        Memory::Write32(page+ids+p*4,0xffffffff);
                        Memory::Write8(stage+0x1f4+p*12,p);Memory::Write8(stage+0x1f5+p*12,0);Memory::Write32(stage+0x1f8+p*12,1000+p);
                    }
                    for(unsigned p=0;p<n;p++){
                        c.gpr[3]=page;c.gpr[4]=p;func_80612B6C(&c);
                        CHECK(c.gpr[3]==1 && submitted==p+1);CHECK(Memory::Read32(page+tail)==p+1);CHECK(Memory::Read32(page+ids+p*4)==p);
                        c.gpr[3]=page;c.gpr[4]=p;func_80612B6C(&c);CHECK(c.gpr[3]==0 && submitted==p+1);
                    }
                    c.gpr[3]=page;c.gpr[4]=n;func_80612B6C(&c);CHECK(c.gpr[3]==0 && submitted==n);
                    // A partial lobby must use its active count rather than capacity.
                    submitted=0;Memory::Write32(stage+state,2);Memory::Write32(page+tail,0);
                    for(unsigned p=0;p<n;p++)Memory::Write32(page+ids+p*4,0xffffffff);
                    for(unsigned p=0;p<2;p++){c.gpr[3]=page;c.gpr[4]=p;func_80612B6C(&c);CHECK(c.gpr[3]==1 && submitted==p+1);}
                    c.gpr[3]=page;c.gpr[4]=2;func_80612B6C(&c);CHECK(c.gpr[3]==0 && submitted==2 && Memory::Read32(page+tail)==2);
                    for(unsigned i=0;i<16;i++)CHECK(Memory::Read8(page-16+i)==0xa5 && Memory::Read8(page+size+i)==0xa5);
                    CHECK(c.gpr[1]==stack);for(uint32_t r=14;r<32;r++)CHECK(c.gpr[r]==0x12340000+r);
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",TranslatorCppTestHarness.BuildCompileArguments(root,temp,new[]{source},harness,exe,includeDataSections:false)){RedirectStandardError=true})!) {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Vote submission compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){RedirectStandardError=true})!;var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(15000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Vote submission execution timed out");Assert.True(run.ExitCode==0,$"Vote submission exited {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        } finally {Directory.Delete(temp,recursive:true);}
    }
}
