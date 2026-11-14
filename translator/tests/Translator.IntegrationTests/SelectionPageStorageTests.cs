using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class SelectionPageStorageTests
{
    [Theory]
        [InlineData(12, true)]
    [InlineData(12, false)]
    [InlineData(13, true)]
    [InlineData(13, false)]
    [InlineData(24, true)]
    [InlineData(24, false)]
    [InlineData(32, true)]
    [InlineData(32, false)]
    [InlineData(48, true)]
    [InlineData(48, false)]
    [InlineData(100, true)]
    [InlineData(100, false)]
    [Trait("Requires", "USA game assets and Clang")]
    public void OriginalRatingAndSelectionPagesKeepExpandedStorageSeparate(int players, bool rating)
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
        var temp=Path.Combine(Path.GetTempPath(),"mkw-selection-page-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try {
            uint ctor=rating?0x8064db7cu:0x8061c8c8u, dtor=rating?0x8064dc68u:0x8061c95cu;
            uint[] entries=rating?new uint[]{ctor,dtor,0x80020494,0x8002058c}:new uint[]{ctor,dtor};
            uint[] fixtures={0x805dd09c,0x805d5ed4,0x8060c378,0x8060c4a0,0x805b5704,0x805b580c,0x805d6100,
                0x805d4094,0x805bac64,0x805bad6c,0x805d9594,0x805d9674,0x805badb0,0x805bac84,0x805d40b8,0x805dd124,0x80229a90};
            var type=rating?"MenuVRPage":"MenuSelectStage";
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
                    size={{layout.Symbol(type+".size")}},cards={{layout.Symbol(type+(rating?".controls.offset":".players.offset"))}},
                    stride={{layout.Symbol(type+(rating?".controls.stride":".players.stride"))}},
                    tail={{layout.Symbol(type+(rating?".movie.offset":".state.offset"))}},
                    miis={{(rating?0:layout.Symbol(type+".miis.offset"))}};
                constexpr bool rating={{(rating?"true":"false")}};
                #define CHECK(x) do{if(!(x)){std::fprintf(stderr,"selection page n=%u rating=%d line=%d: %s\n",n,rating,__LINE__,#x);std::abort();} }while(false)
                unsigned constructed=0,destroyed=0,deallocated=0,miisConstructed=0,miisDestroyed=0;
                void noop(CpuContext*){}
                void page_ctor(CpuContext* c){CHECK(c->gpr[3]==page);}
                void control_ctor(CpuContext* c){auto p=c->gpr[3];CHECK(rating);CHECK(p==page+0x27c || p==page+0x3f0 || (p>=page+cards && p<page+cards+n*stride && (p-page-cards)%stride==0));++constructed;std::memset(Memory::GetPointer(p,0x174),0x5a,0x174);}
                void control_dtor(CpuContext*){++destroyed;}
                void mii_ctor(CpuContext* c){CHECK(!rating && c->gpr[3]==page+miis);++miisConstructed;std::memset(Memory::GetPointer(page+miis,152),0x3c,152);}
                void mii_dtor(CpuContext* c){CHECK(!rating && c->gpr[3]==page+miis);++miisDestroyed;}
                void deallocate(CpuContext* c){CHECK(c->gpr[3]==page);++deallocated;}
                void entry(uint32_t pc,void(*f)(CpuContext*)){TranslatedFunctionInfo i{};i.address=pc;i.name="selection page fixture";i.rawCpuInvoker=f;i.entryPoint=reinterpret_cast<void*>(f);TranslatedFunctionRegistry::Register(i);}
                int main(int argc,char** argv){
                    CHECK(argc==2);Memory::Init(32*1024*1024);auto f=std::fopen(argv[1],"rb");CHECK(f);
                    CHECK(std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},f)=={{image.Memory.Length}});std::fclose(f);
                    {{string.Join("\n",entries.Select(pc=>$"entry(0x{pc:X8},func_{pc:X8});"))}}
                    {{string.Join("\n",fixtures.Except(new uint[]{0x805dd09c,0x8060c378,0x8060c4a0,0x805d9594,0x805d9674,0x80229a90}).Select(pc=>$"entry(0x{pc:X8},noop);"))}}
                    entry(0x805dd09c,page_ctor);entry(0x8060c378,control_ctor);entry(0x8060c4a0,control_dtor);
                    entry(0x805d9594,mii_ctor);entry(0x805d9674,mii_dtor);entry(0x80229a90,deallocate);
                    CpuContext c{};c.gpr[1]=stack;c.gpr[13]=0x80388880;c.gpr[2]=0x8038ac20;CpuContextScope scope(&c);
                    for(uint32_t r=14;r<32;r++)c.gpr[r]=0x12340000+r;
                    std::memset(Memory::GetPointer(page-16,size+32),0xa5,size+32);c.gpr[3]=page;func_{{ctor:X8}}(&c);
                    CHECK(c.gpr[3]==page);
                    if(rating){
                        CHECK(constructed==n+2 && Memory::Read32(page+tail)==0);
                        for(unsigned p=0;p<n;p++)for(unsigned j=0;j<stride;j++)CHECK(Memory::Read8(page+cards+p*stride+j)==0x5a);
                        Memory::Write32(page+tail,0x12345678);
                        std::memset(Memory::GetPointer(page+cards,n*stride),0x72,n*stride);
                        CHECK(Memory::Read32(page+tail)==0x12345678);
                    }else{
                        CHECK(miisConstructed==1 && miis+152==size);
                        for(unsigned p=0;p<n*stride+12;p++)CHECK(Memory::Read8(page+cards+p)==0xa5);
                        std::memset(Memory::GetPointer(page+cards,n*stride),0x72,n*stride);
                        for(unsigned j=0;j<12;j++)CHECK(Memory::Read8(page+tail+j)==0xa5);
                        for(unsigned j=0;j<152;j++)CHECK(Memory::Read8(page+miis+j)==0x3c);
                    }
                    c.gpr[3]=page;c.gpr[4]=1;func_{{dtor:X8}}(&c);
                    CHECK(c.gpr[3]==page && deallocated==1);
                    CHECK(rating?(destroyed==n+2):(miisDestroyed==1));
                    for(unsigned i=0;i<16;i++)CHECK(Memory::Read8(page-16+i)==0xa5 && Memory::Read8(page+size+i)==0xa5);
                    CHECK(c.gpr[1]==stack);for(uint32_t r=14;r<32;r++)CHECK(c.gpr[r]==0x12340000+r);
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!) {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Selection page compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){RedirectStandardError=true})!;var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(15000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Selection page execution timed out");Assert.True(run.ExitCode==0,$"Selection page exited {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        } finally {Directory.Delete(temp,recursive:true);}
    }
}
