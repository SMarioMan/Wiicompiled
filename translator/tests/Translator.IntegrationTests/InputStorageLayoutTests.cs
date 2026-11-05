using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Translator.Core.Analysis.Ssa;
using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class InputStorageLayoutTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void OriginalInputManagerRegionConstructsEveryRealVirtualControllerHolder(int players)
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
        var original=translator.Discover(0x8051ee7c,options);var blocks=new List<IrBasicBlock>();
        // Actual virtual-array construction after the physical real-holder array,
        // stopping before the following dummy controller is constructed.
        foreach(var block in original.LinearIr.Blocks)
        {
            var start=uint.Parse(block.Label.AsSpan(2),NumberStyles.HexNumber,CultureInfo.InvariantCulture);
            if(start>0x8051eee0)continue;
            var nodes=new List<IrInstruction>();var included=start>0x8051eec0;
            foreach(var node in block.Instructions)
            {
                if(!included){if(node is IrTracePpc {Address:0x8051eec0})included=true;continue;}
                nodes.Add(node);
                if(node is IrTracePpc {Address:0x8051eee0}){nodes.Add(new IrReturn(null));break;}
            }
            if(nodes.Count>0)blocks.Add(block with {Instructions=nodes});
        }
        Assert.NotEmpty(blocks);
        var function=original.LinearIr with {Name="input_construct_region",EntryLabel=blocks[0].Label,Blocks=blocks};
        var region=translator.LowerCanonical(0x8051ee7c,new SsaTransformer().Convert(function).Function,
            options with {PreferredName="input_construct_region"});
        var temp=Path.Combine(Path.GetTempPath(),"mkw-input-storage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var sources=new List<string>();
            var entries=new uint[] {0x80020494,0x8051f02c,0x8051caf0,0x8051a734,0x8051a3e8,0x8051d0e0,0x8051af08,0x80006038,0x80005f84,0x8051cc9c};
            foreach(var pc in entries)
            {
                var file=Path.Combine(temp,$"{pc:X8}.cpp");File.WriteAllText(file,translator.Translate(pc,options).CxxCode);sources.Add(file);
            }
            var init=Path.Combine(temp,"init.cpp");File.WriteAllText(init,region.CxxCode);sources.Add(init);
            var declarations=string.Join("\n",entries.Select(pc=>$"extern \"C\" void func_{pc:X8}(CpuContext*);"));
            var registrations=string.Join("\n",entries.Select(pc=>$"register_entry(0x{pc:X8},func_{pc:X8});"));
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <cstdio>
                #include <cstring>
                {{declarations}}
                extern "C" void input_construct_region(CpuContext*);
                void register_entry(uint32_t address,void(*entry)(CpuContext*)) {
                    TranslatedFunctionInfo info{};info.address=address;info.name="real constructor chain";
                    info.rawCpuInvoker=entry;info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);
                }
                int main(int argc,char** argv) {
                    if(argc!=2)return 1;
                    Memory::Init(32*1024*1024);
                    FILE* image=std::fopen(argv[1],"rb");if(!image)return 2;
                    const auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},image);
                    std::fclose(image);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    constexpr uint32_t data=0x81000000,sp=0x81f00000,size={{layout.Symbol("InputManager.size")}};
                    constexpr uint32_t begin={{layout.Symbol("InputManager.virtualHolders.offset")}},end={{layout.Symbol("InputManager.virtualHolders.end")}};
                    std::memset(Memory::GetPointer(data,size),0x7e,size);
                    for(uint32_t i=0;i<16;i++){Memory::Write8(data-16+i,0x55);Memory::Write8(data+size+i,0x55);
                        Memory::Write8(sp-0x100+i,0x55);Memory::Write8(sp+8+i,0x55);}
                    CpuContext ctx{};ctx.gpr[1]=sp;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;
                    for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;ctx.gpr[29]=data;
                    CpuContextScope scope(&ctx);input_construct_region(&ctx);
                    for(uint32_t i=0;i<{{players}};i++) {
                        const uint32_t holder=data+begin+i*0x180,controller=holder+0xd8;
                        if(Memory::Read32(holder)!=0x808ae704 || Memory::Read32(holder+4)!=controller ||
                           Memory::Read32(holder+8)!=controller || Memory::Read32(controller)!=0x808ae928)return 4;
                        if(Memory::Read32(controller+0x90)!=0x808ae87c)return 5;
                    }
                    for(uint32_t i=0;i<begin;i++)if(Memory::Read8(data+i)!=0x7e)return 6;
                    for(uint32_t i=end;i<size;i++)if(Memory::Read8(data+i)!=0x7e)return 7;
                    if(ctx.gpr[1]!=sp)return 8;
                    for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=(r==29?data:0x12340000+r))return 9;
                    for(uint32_t i=0;i<16;i++)if(Memory::Read8(data-16+i)!=0x55 || Memory::Read8(data+size+i)!=0x55 ||
                        Memory::Read8(sp-0x100+i)!=0x55 || Memory::Read8(sp+8+i)!=0x55)return 10;
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(45000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Input storage compilation timed out");
                Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            var runInfo=new ProcessStartInfo(exe){UseShellExecute=false,RedirectStandardError=true};runInfo.ArgumentList.Add(binary);
            using var run=Process.Start(runInfo)!;var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Input storage execution timed out");
            Assert.True(run.ExitCode==0,$"Input storage returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
