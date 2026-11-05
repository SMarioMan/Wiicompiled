using System.Diagnostics;
using System.Globalization;
using Translator.Core.Analysis.Ssa;
using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class EffectCreatorStorageTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealEffectManagerAllocatesAndInitializesTheWholeExpandedCreatorNamespace(int players)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var manifest=Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json");
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));
        var options=new TranslationOptions(EnableLeafInlining:false,KnownFunctionEntryPoints:map.Addresses.ToHashSet());
        var translator=new FunctionTranslator(image,layoutRelocations:GuestLayoutRelocations.Read(manifest,players));
        var temp=Path.Combine(Path.GetTempPath(),"mkw-ai-objects-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var entries=new uint[] {0x80225184,0x802242a8,0x80224f7c,0x8022464c,0x80677040};
            var sources=new List<string>();
            foreach(var pc in entries)
            {
                var file=Path.Combine(temp,$"{pc:X8}.cpp");File.WriteAllText(file,translator.Translate(pc,options).CxxCode);sources.Add(file);
            }
            var discovered=translator.Discover(0x806770f8,options);var blocks=new List<IrBasicBlock>();
            foreach(var block in discovered.LinearIr.Blocks)
            {
                var start=uint.Parse(block.Label.AsSpan(2),NumberStyles.HexNumber,CultureInfo.InvariantCulture);
                if(start>0x80677144)continue;
                var nodes=new List<IrInstruction>();var included=start>0x8067710c;
                foreach(var node in block.Instructions)
                {
                    if(node is IrTracePpc {Address:0x8067710c})included=true;
                    if(node is IrTracePpc {Address:0x80677144}){nodes.Add(new IrReturn(null));break;}
                    if(included)nodes.Add(node);
                }
                if(nodes.Count>0)blocks.Add(block with {Instructions=nodes});
            }
            var function=discovered.LinearIr with {Name="effect_creators_init",Blocks=blocks};
            var init=translator.LowerCanonical(0x806770f8,new SsaTransformer().Convert(function).Function,
                options with {PreferredName="effect_creators_init"});
            var region=Path.Combine(temp,"region.cpp");File.WriteAllText(region,init.CxxCode);sources.Add(region);
            var declarations=string.Join("\n",entries.Select(pc=>$"extern \"C\" void func_{pc:X8}(CpuContext*);"));
            var registrations=string.Join("\n",entries.Where(pc=>pc!=0x8022464c).Select(pc=>$"register_entry(0x{pc:X8},func_{pc:X8});"));
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <array>
                #include <cstdlib>
                #include <cstring>
                {{declarations}}
                extern "C" void effect_creators_init(CpuContext*);
                constexpr uint32_t manager=0x80010000,table=0x80020000,objects=0x80030000,
                    owner=0x80050000,heap=0x80060000,sp=0x800f0000,n={{players}}+4;
                uint32_t allocated=0,created=0;
                void allocate_table(CpuContext* ctx){if(ctx->gpr[3]!=n*4 || ctx->gpr[4]!=heap || ctx->gpr[5]!=4)std::abort();
                    allocated++;ctx->gpr[3]=table;}
                void allocate_creator(CpuContext* ctx){if(ctx->gpr[3]!=24 || created>=n)std::abort();ctx->gpr[3]=objects+24*created++;}
                void empty(CpuContext*){}
                void effect_system(CpuContext* ctx){ctx->gpr[3]=0x80070000;}
                void create_checked(CpuContext* ctx){if(ctx->gpr[3]!=n || ctx->gpr[4]!=heap)std::abort();func_8022464C(ctx);}
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="effect creator storage";
                    info.rawCpuInvoker=entry;info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                int main(){
                    Memory::Init(16*1024*1024);
                    {{registrations}}
                    register_entry(0x80229a80,allocate_table);register_entry(0x80229a48,allocate_creator);
                    register_entry(0x800125c0,empty);register_entry(0x8021a050,empty);register_entry(0x80027090,effect_system);
                    register_entry(0x802246c8,empty);register_entry(0x8022464c,create_checked);
                    std::memset(Memory::GetPointer(table,n*4),0x7e,n*4);
                    for(uint32_t i=0;i<16;i++){Memory::Write8(table-16+i,0x55);Memory::Write8(table+n*4+i,0x55);
                        Memory::Write8(objects-16+i,0x55);Memory::Write8(objects+n*24+i,0x55);}
                    CpuContext ctx{};ctx.gpr[1]=sp;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                    for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                    ctx.gpr[3]=manager;ctx.gpr[4]=n;ctx.gpr[5]=heap;func_80225184(&ctx);
                    if(allocated!=1 || Memory::Read32(manager)!=n*4 || Memory::Read32(manager+4)!=n ||
                        Memory::Read32(manager+0x28)!=table)return 1;
                    for(uint32_t i=0;i<n;i++)if(Memory::Read32(table+i*4)!=0)return 2;
                    Memory::Write32(0x80382ab0,manager);Memory::Write32(0x809bda10,owner);
                    ctx.gpr[3]=heap;func_80677040(&ctx);
                    if(ctx.gpr[1]!=sp)return 3;
                    for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r)return 4;
                    ctx.gpr[3]=owner;ctx.gpr[4]=2;effect_creators_init(&ctx);
                    if(created!=n)return 5;
                    for(uint32_t i=0;i<n;i++)if(Memory::Read32(table+i*4)!=objects+i*24 ||
                        Memory::Read32(objects+i*24+0x10)!=i || Memory::Read32(objects+i*24+0x14)!=i*4 ||
                        Memory::Read32(objects+i*24)!=0x8029eb10)return 6;
                    for(uint32_t i=0;i<16;i++)if(Memory::Read8(table-16+i)!=0x55 || Memory::Read8(table+n*4+i)!=0x55 ||
                        Memory::Read8(objects-16+i)!=0x55 || Memory::Read8(objects+n*24+i)!=0x55)return 7;
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(45000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Effect creator compilation timed out");
                Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(5000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Effect creator execution timed out");
            Assert.True(run.ExitCode==0,$"Effect creator lifecycle returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
