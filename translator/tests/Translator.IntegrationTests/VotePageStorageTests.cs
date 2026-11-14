using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class VotePageStorageTests
{
    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(32)] [InlineData(48)] [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void OriginalVotePageConstructsAndDestroysEveryCardWithoutOverwritingState(int players)
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
        var temp=Path.Combine(Path.GetTempPath(),"mkw-vote-page-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try {
            uint[] entries={0x80611b70,0x80611c9c,0x8061168c,0x806116c8,0x80020494,0x8002058c,0x80612af8};
            uint[] fixtures={0x805dd09c,0x805d4094,0x8060c378,0x8060c4a0,0x805d40b8,0x805dd124,0x80229a90};
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
                {{string.Join("\n",entries.Select(pc=>$"extern \"C\" void func_{pc:X8}(CpuContext*);"))}}
                constexpr uint32_t n={{players}},page=0x81010000,stack=0x81f00000,
                    size={{layout.Symbol("MenuVotePage.size")}},cards={{layout.Symbol("MenuVotePage.controls.offset")}},
                    stride={{layout.Symbol("MenuVotePage.controls.stride")}},ids={{layout.Symbol("MenuVotePage.voterIds.offset")}},
                    tail={{layout.Symbol("MenuVotePage.tail.offset")}};
                #define CHECK(x) do{if(!(x)){std::fprintf(stderr,"vote page n=%u line=%d: %s\n",n,__LINE__,#x);std::abort();} }while(false)
                unsigned constructed=0,destroyed=0,deallocated=0;
                void noop(CpuContext*){}
                void page_ctor(CpuContext* c){CHECK(c->gpr[3]==page);std::memset(Memory::GetPointer(page,0x44),0,0x44);}
                void input_ctor(CpuContext* c){CHECK(c->gpr[3]==page+0x58);std::memset(Memory::GetPointer(page+0x58,16),0,16);}
                void control_ctor(CpuContext* c){auto p=c->gpr[3];CHECK(p==page+0x6c || p==page+0x1e0 || p==page+0x354 || (p>=page+cards && p<page+cards+n*stride && (p-page-cards)%stride==0));++constructed;std::memset(Memory::GetPointer(p,0x174),0,0x174);}
                void control_dtor(CpuContext*){++destroyed;}
                void deallocate(CpuContext* c){CHECK(c->gpr[3]==page);++deallocated;}
                void entry(uint32_t pc,void(*f)(CpuContext*)){TranslatedFunctionInfo i{};i.address=pc;i.name="voting page fixture";i.rawCpuInvoker=f;i.entryPoint=reinterpret_cast<void*>(f);TranslatedFunctionRegistry::Register(i);}
                int main(int argc,char** argv){
                    CHECK(argc==2);Memory::Init(32*1024*1024);auto f=std::fopen(argv[1],"rb");CHECK(f);
                    CHECK(std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},f)=={{image.Memory.Length}});std::fclose(f);
                    {{string.Join("\n",entries.Select(pc=>$"entry(0x{pc:X8},func_{pc:X8});"))}}
                    entry(0x805dd09c,page_ctor);entry(0x805d4094,input_ctor);entry(0x8060c378,control_ctor);entry(0x8060c4a0,control_dtor);
                    entry(0x805d40b8,noop);entry(0x805dd124,noop);entry(0x80229a90,deallocate);
                    CpuContext c{};c.gpr[1]=stack;c.gpr[13]=0x80388880;c.gpr[2]=0x8038ac20;CpuContextScope scope(&c);
                    for(uint32_t r=14;r<32;r++)c.gpr[r]=0x12340000+r;
                    std::memset(Memory::GetPointer(page-16,size+32),0xa5,size+32);c.gpr[3]=page;func_80611B70(&c);
                    CHECK(c.gpr[3]==page && constructed==n+3);
                    std::vector<uint8_t> expected(size,0xa5);
                    auto word=[&](uint32_t at,uint32_t value){for(unsigned i=0;i<4;i++)expected[at+i]=value>>(24-i*8);};
                    std::memset(expected.data(),0,0x44);word(0,0x808b9154);word(0x44,0x808b91b8);word(0x48,page);
                    for(unsigned i=0;i<3;i++)word(0x4c+i*4,Memory::Read32(0x808b9088+i*4));
                    std::memset(expected.data()+0x58,0,16);word(0x68,0xffffffff);
                    for(auto off:{0x6cu,0x1e0u,0x354u})std::memset(expected.data()+off,0,0x174);
                    word(0x6c,0x808d5184);word(0x1e0,0x808d5148);
                    for(unsigned p=0;p<n;p++){std::memset(expected.data()+cards+p*stride,0,0x174);word(cards+p*stride,0x808b91c4);word(ids+p*4,0xffffffff);}
                    word(tail,0);for(uint32_t i=0,shown=0;i<size && shown<24;i++)if(expected[i]!=Memory::Read8(page+i)){std::fprintf(stderr,"offset %x expected %x actual %x\n",i,expected[i],Memory::Read8(page+i));++shown;}CHECK(!std::memcmp(expected.data(),Memory::GetPointer(page,size),size));
                    for(unsigned state=0;state<5;state++){
                        Memory::Write32(page+tail+8,state);c.gpr[3]=page;func_80612AF8(&c);CHECK(c.gpr[3]==(state==3?0x51:0xffffffffu));
                    }
                    // Every voter ID has its own storage before the state tail.
                    Memory::Write32(page+tail,0x12345678);for(unsigned p=0;p<n;p++)Memory::Write32(page+ids+p*4,p);
                    CHECK(Memory::Read32(page+tail)==0x12345678);
                    c.gpr[3]=page;c.gpr[4]=1;func_80611C9C(&c);CHECK(c.gpr[3]==page && destroyed==n+3 && deallocated==1);
                    for(unsigned i=0;i<16;i++)CHECK(Memory::Read8(page-16+i)==0xa5 && Memory::Read8(page+size+i)==0xa5);
                    CHECK(c.gpr[1]==stack);for(uint32_t r=14;r<32;r++)CHECK(c.gpr[r]==0x12340000+r);
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!) {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Vote page compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){RedirectStandardError=true})!;var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(15000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Vote page execution timed out");Assert.True(run.ExitCode==0,$"Vote page exited {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        } finally {Directory.Delete(temp,recursive:true);}
    }
}
