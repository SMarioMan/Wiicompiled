using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Translator.Core.Analysis.Ssa;
using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class AiStorageLayoutTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealAiStorageInitializationAndAccessorsUseUniformArrays(int players)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln"))) directory=directory.Parent;
        var root=directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var path=Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json");
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        using var doc=JsonDocument.Parse(File.ReadAllText(path));
        var layouts=new GuestLayouts(doc.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!,players);
        var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));
        var translator=new FunctionTranslator(image,layoutRelocations:GuestLayoutRelocations.Read(path,players));
        var options=new TranslationOptions(EnableLeafInlining:false,KnownFunctionEntryPoints:map.Addresses.ToHashSet());
        var discovery=translator.Discover(0x8072b8ac,options);
        // Real constructor region after the external disposer constructor, before
        // SetCCAndDifficulty and subordinate allocations. Incoming r31 is this.
        var blocks=new List<IrBasicBlock>();
        foreach(var block in discovery.LinearIr.Blocks)
        {
            var start=uint.Parse(block.Label.AsSpan(2),NumberStyles.HexNumber,CultureInfo.InvariantCulture);
            if(start>0x8072b9a4) continue;
            var instructions=new List<IrInstruction>();var included=start>0x8072b8cc;
            foreach(var node in block.Instructions)
            {
                if(!included) {if(node is IrTracePpc {Address:0x8072b8cc}) included=true;continue;}
                instructions.Add(node);
                if(node is IrTracePpc {Address:0x8072b9a4}) {instructions.Add(new IrReturn(null));break;}
            }
            if(instructions.Count>0) blocks.Add(block with {Instructions=instructions});
        }
        Assert.NotEmpty(blocks);
        Assert.DoesNotContain(blocks.SelectMany(block=>block.Instructions),node=>node is IrCall or IrIndirectCall);
        var function=discovery.LinearIr with {Name="ai_init_region",Blocks=blocks};
        var initialized=translator.LowerCanonical(0x8072b8ac,new SsaTransformer().Convert(function).Function,options with {PreferredName="ai_init_region"});
        var temp=Path.Combine(Path.GetTempPath(),"mkw-ai-storage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var sources=new List<string>();
            foreach(var entry in new uint[] {0x8072ec98,0x8072bcd8,0x8072bce8,0x8072c194})
            {
                var source=Path.Combine(temp,$"{entry:X8}.cpp");File.WriteAllText(source,translator.Translate(entry,options).CxxCode);sources.Add(source);
            }
            var init=Path.Combine(temp,"init.cpp");File.WriteAllText(init,initialized.CxxCode);sources.Add(init);
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <cstring>
                extern "C" void ai_init_region(CpuContext*);
                extern "C" void func_8072EC98(CpuContext*);
                extern "C" void func_8072C194(CpuContext*);
                extern "C" void func_8072BCD8(CpuContext*);
                extern "C" void func_8072BCE8(CpuContext*);
                int main() {
                    Memory::Init(16*1024*1024);
                    constexpr uint32_t manager=0x80010000,routes=0x80020000,racedata=0x80030000;
                    constexpr uint32_t size={{layouts.Symbol("AIManager.size")}},routeSize={{layouts.Symbol("AIEnemyRoutes.size")}};
                    constexpr uint32_t all={{layouts.Symbol("AIManager.controllers.offset")}}, real={{layouts.Symbol("AIManager.realControllers.offset")}};
                    constexpr uint32_t tail={{layouts.Symbol("AIManager.specialItem.offset")}}, count={{layouts.Symbol("AIEnemyRoutes.count.offset")}};
                    std::memset(Memory::GetPointer(manager,size),0x7e,size);
                    std::memset(Memory::GetPointer(routes,routeSize),0x7e,routeSize);
                    Memory::Write32(0x809b8f68,racedata);Memory::Write8(racedata+0x24,{{players}});
                    Memory::Write32(racedata+{{layouts.Symbol("Racedata.race.offset+Scenario.settings.offset")}}+8,1);
                    for(uint32_t i=0;i<16;i++) {Memory::Write8(manager-16+i,0x55);Memory::Write8(manager+size+i,0x55);
                        Memory::Write8(routes-16+i,0x55);Memory::Write8(routes+routeSize+i,0x55);}
                    CpuContext ctx{};ctx.gpr[31]=manager;ai_init_region(&ctx);
                    if(Memory::Read32(manager+0x14)!={{players}}) return 1;
                    for(uint32_t i=0;i<{{players}};i++)
                        if(Memory::Read32(manager+all+i*4)!=0 || Memory::Read32(manager+real+i*4)!=0) return 2;
                    for(uint32_t p=tail;p<size;p+=4) if(Memory::Read32(manager+p)!=0) return 3;
                    ctx.gpr[3]=routes;func_8072EC98(&ctx);
                    if(Memory::Read32(routes+count)!=0) return 4;
                    for(uint32_t i=0;i<{{players}};i++) if(Memory::Read32(routes+4+i*4)!=0) return 5;
                    for(uint32_t i=0;i<{{players}};i++) {
                        Memory::Write32(manager+all+i*4,0xa0000000+i);Memory::Write32(manager+real+i*4,0xb0000000+i);
                        ctx.gpr[3]=manager;ctx.gpr[4]=i;func_8072BCD8(&ctx);if(ctx.gpr[3]!=0xa0000000+i) return 6;
                        ctx.gpr[3]=manager;ctx.gpr[4]=i;func_8072BCE8(&ctx);if(ctx.gpr[3]!=0xb0000000+i) return 7;
                    }
                    constexpr uint32_t params=0x80040000;
                    Memory::Write32(manager+{{layouts.Symbol("AIManager.params.offset")}},params);
                    const uint32_t settings=racedata+{{layouts.Symbol("Racedata.race.offset+Scenario.settings.offset")}};
                    for(uint32_t mode : {0u,1u,2u,3u,4u,5u,7u,8u,9u,10u}) {
                        Memory::Write32(settings+8,mode);
                        for(uint32_t variant : {1u,2u}) {
                            Memory::Write32(params+0xc,variant);ctx.gpr[3]=manager;func_8072C194(&ctx);
                            const uint32_t expected=(mode<=1 || (mode==4 && variant!=1)) ? 1 : 0;
                            if(ctx.gpr[3]!=expected) return 9;
                        }
                    }
                    for(uint32_t i=0;i<16;i++)
                        if(Memory::Read8(manager-16+i)!=0x55 || Memory::Read8(manager+size+i)!=0x55 ||
                           Memory::Read8(routes-16+i)!=0x55 || Memory::Read8(routes+routeSize+i)!=0x55) return 8;
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)) {RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var exited=compile.WaitForExit(45000);
                if(!exited) compile.Kill(entireProcessTree:true);
                Assert.True(exited,"AI storage compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe){RedirectStandardError=true,UseShellExecute=false})!;
            var output=run.StandardError.ReadToEndAsync();var done=run.WaitForExit(5000);if(!done)run.Kill(entireProcessTree:true);
            Assert.True(done,"AI storage execution timed out");Assert.True(run.ExitCode==0,$"AI storage returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally {Directory.Delete(temp,recursive:true);}
    }
}
