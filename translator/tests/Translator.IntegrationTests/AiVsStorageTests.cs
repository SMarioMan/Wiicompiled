using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Translator.Core.Analysis.Ssa;
using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class AiVsStorageTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealSubordinateAiInitializationAndInsertionRespectBothArrayRoles(int players)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var manifest=Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json");
        using var doc=JsonDocument.Parse(File.ReadAllText(manifest));
        var layout=new GuestLayouts(doc.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!,players);
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));
        var options=new TranslationOptions(EnableLeafInlining:false,KnownFunctionEntryPoints:map.Addresses.ToHashSet());
        var translator=new FunctionTranslator(image,layoutRelocations:GuestLayoutRelocations.Read(manifest,players));
        var original=translator.Discover(0x80730e50,options);var blocks=new List<IrBasicBlock>();
        // The real constructor's storage initialization, after its base constructor
        // and before allocating subordinate action objects. Keep its actual CFG.
        foreach(var block in original.LinearIr.Blocks)
        {
            var start=uint.Parse(block.Label.AsSpan(2),NumberStyles.HexNumber,CultureInfo.InvariantCulture);
            if(start>0x80731268)continue;
            var nodes=new List<IrInstruction>();var included=start>0x80730e6c;
            foreach(var node in block.Instructions)
            {
                if(!included){if(node is IrTracePpc {Address:0x80730e6c})included=true;continue;}
                nodes.Add(node);
                if(node is IrTracePpc {Address:0x80731268}){nodes.Add(new IrReturn(null));break;}
            }
            if(nodes.Count>0)blocks.Add(block with {Instructions=nodes});
        }
        Assert.DoesNotContain(blocks.SelectMany(block=>block.Instructions),node=>node is IrIndirectCall ||
            node is IrCall call && call.Target.StartsWith("0x",StringComparison.Ordinal));
        var function=original.LinearIr with {Name="sub_init_region",Blocks=blocks};
        var init=translator.LowerCanonical(0x80730e50,new SsaTransformer().Convert(function).Function,
            options with {PreferredName="sub_init_region"});
        var temp=Path.Combine(Path.GetTempPath(),"mkw-ai-vs-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var source=Path.Combine(temp,"init.cpp");File.WriteAllText(source,init.CxxCode);
            var add=Path.Combine(temp,"add.cpp");File.WriteAllText(add,translator.Translate(0x807315ec,options).CxxCode);
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <cstring>
                extern "C" void sub_init_region(CpuContext*);
                extern "C" void func_807315EC(CpuContext*);
                // External classification/action callbacks are fixtures; storage
                // initialization and insertion execute the original game routines.
                void is_cpu(CpuContext* ctx) {ctx->gpr[3]=Memory::Read32(ctx->gpr[3]+4);}
                void action(CpuContext* ctx) {ctx->gpr[3]=Memory::Read32(ctx->gpr[3]);}
                void register_fixture(uint32_t address,void(*entry)(CpuContext*)) {
                    TranslatedFunctionInfo info{};info.address=address;info.name="external fixture";
                    info.rawCpuInvoker=entry;info.entryPoint=reinterpret_cast<void*>(entry);
                    TranslatedFunctionRegistry::Register(info);
                }
                int main() {
                    Memory::Init(16*1024*1024);
                    constexpr uint32_t data=0x80010000,size={{layout.Symbol("AIVsSub.size")}},sp=0x800f0000;
                    constexpr uint32_t cpu={{layout.Symbol("AIVsSub.cpuPairs.offset")}},local={{layout.Symbol("AIVsSub.localPairs.offset")}};
                    constexpr uint32_t cpuCount={{layout.Symbol("AIVsSub.cpuCount.offset")}},localCount={{layout.Symbol("AIVsSub.localCount.offset")}};
                    std::memset(Memory::GetPointer(data,size),0x7e,size);
                    for(uint32_t i=0;i<16;i++){Memory::Write8(data-16+i,0x55);Memory::Write8(data+size+i,0x55);
                        Memory::Write8(sp-0x20+i,0x55);Memory::Write8(sp+8+i,0x55);}
                    CpuContext ctx{};ctx.gpr[1]=sp;ctx.gpr[30]=data;ctx.gpr[31]=0x808c5e80;
                    CpuContextScope scope(&ctx);sub_init_region(&ctx);
                    for(uint32_t i=0;i<{{players}}*8;i++)if(Memory::Read8(data+cpu+i)!=0)return 1;
                    for(uint32_t i=0;i<4*8;i++)if(Memory::Read8(data+local+i)!=0)return 2;
                    for(uint32_t offset : {cpuCount,localCount,{{layout.Symbol("AIVsSub.field180.offset")}}u,{{layout.Symbol("AIVsSub.scratch.offset")}}u})
                        if(Memory::Read32(data+offset)!=0)return 3;
                    register_fixture(0x80721e1c,is_cpu);register_fixture(0x80001080,action);
                    constexpr uint32_t vtable=0x80080000;
                    Memory::Write32(vtable+0x3c,0x80001080);
                    for(uint32_t i=0;i<{{players}}+4;i++) {
                        const bool isCpu=i<{{players}};const uint32_t index=isCpu?i:i-{{players}};
                        const uint32_t kart=0x80030000+i*0x400,player=kart+0x80,driver=kart+0x100,actor=kart+0x200;
                        Memory::Write32(kart+4,isCpu);Memory::Write32(kart+0x10,player);Memory::Write32(player+0x144,driver);
                        Memory::Write32(driver+0x34,vtable);Memory::Write32(driver,actor);
                        ctx.gpr[3]=data;ctx.gpr[4]=kart;ctx.gpr[30]=0x12340030;ctx.gpr[31]=0x12340031;
                        func_807315EC(&ctx);
                        const uint32_t pair=data+(isCpu?cpu:local)+index*8;
                        if(Memory::Read32(pair)!=actor || Memory::Read32(pair+4)!=0)return 4;
                        if(Memory::Read32(data+(isCpu?cpuCount:localCount))!=index+1)return 5;
                        if(ctx.gpr[1]!=sp || ctx.gpr[30]!=0x12340030 || ctx.gpr[31]!=0x12340031)return 6;
                    }
                    if(Memory::Read32(data+{{layout.Symbol("AIVsSub.primary.offset")}})!=0 ||
                       Memory::Read32(data+{{layout.Symbol("AIVsSub.scratch.offset")}})!=0)return 7;
                    for(uint32_t i=0;i<16;i++)if(Memory::Read8(data-16+i)!=0x55 || Memory::Read8(data+size+i)!=0x55 ||
                        Memory::Read8(sp-0x20+i)!=0x55 || Memory::Read8(sp+8+i)!=0x55)return 8;
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,new[] {source,add},harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(45000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Subordinate AI compilation timed out");
                Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(5000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Subordinate AI execution timed out");
            Assert.True(run.ExitCode==0,$"Subordinate AI returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
