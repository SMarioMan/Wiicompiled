using System.Diagnostics;
using System.Buffers.Binary;
using Translator.Core.Disassembly;
using Translator.Core.Lifting;
using Translator.Core.Analysis.Ssa;
using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class EffectCreatorRoleTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealEffectConstructionKeepsRacerIdsAndMovesReservedRolesBeyondThePlayerRange(int players)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var manifest=Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json");
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));
        var options=new TranslationOptions(EnableLeafInlining:false,KnownFunctionEntryPoints:map.Addresses.ToHashSet());
        var relocations=GuestLayoutRelocations.Read(manifest,players);
        var translator=new FunctionTranslator(image,layoutRelocations:relocations);
        var temp=Path.Combine(Path.GetTempPath(),"mkw-ai-objects-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var entries=new uint[] {0x80222c2c,0x800374c0};
            var sources=new List<string>();
            foreach(var pc in entries)
            {
                var file=Path.Combine(temp,$"{pc:X8}.cpp");File.WriteAllText(file,translator.Translate(pc,options).CxxCode);sources.Add(file);
            }
            foreach(var (entry,begin,end,name) in new[] {
                (0x8068abbcu,0x8068b39cu,0x8068b3b0u,"self_effect_init"),
                (0x8074c488u,0x8074c4c0u,0x8074c4d0u,"world_effect_init")})
            {
                var nodes=new List<IrInstruction>();
                for(var pc=begin;pc<end;pc+=4)
                {
                    var word=BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(pc,4),4));
                    var instruction=PpcDecoder.Decode(pc,word);
                    nodes.AddRange(relocations.Apply(instruction,new PpcLifter().Lift(new[] {instruction})[0].Ir));
                }
                Assert.DoesNotContain(nodes,node=>node is IrBranch);
                nodes.Add(new IrReturn(null));
                var function=new IrFunction(name,$"0x{begin:X8}",new[] {new IrBasicBlock($"0x{begin:X8}",nodes)});
                var region=translator.LowerCanonical(begin,new SsaTransformer().Convert(function).Function,options with {PreferredName=name});
                var file=Path.Combine(temp,name+".cpp");File.WriteAllText(file,region.CxxCode);sources.Add(file);
            }
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var declarations=string.Join("\n",entries.Select(pc=>$"extern \"C\" void func_{pc:X8}(CpuContext*);"));
            var registrations=string.Join("\n",entries.Select(pc=>$"register_entry(0x{pc:X8},func_{pc:X8});"));
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <array>
                #include <cstdlib>
                #include <cstring>
                #include <cstdio>
                {{declarations}}
                extern "C" void self_effect_init(CpuContext*);
                extern "C" void world_effect_init(CpuContext*);
                constexpr uint32_t data=0x81010000,sp=0x81f00000,n={{players}};
                uint32_t copied=0,resetCalls=0;
                void copy_name(CpuContext* ctx){for(uint32_t i=0;i<32;i++){auto c=Memory::Read8(ctx->gpr[4]+i);
                    Memory::Write8(ctx->gpr[3]+i,c);if(!c){copied++;return;} }std::abort();}
                // The virtual reset is a fixture; the real constructor's ID store
                // and SDK handle constructor execute, as do both audited game sites.
                void reset(CpuContext*){resetCalls++;}
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="effect creator role";
                    info.rawCpuInvoker=entry;info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);
                    FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    register_entry(0x800125c0,copy_name);register_entry(0x8022423c,reset);
                    for(uint32_t i=0;i<16;i++){Memory::Write8(data-16+i,0x55);Memory::Write8(data+0x7c+i,0x55);
                        Memory::Write8(sp-0x40-16+i,0x55);Memory::Write8(sp+8+i,0x55);}
                    CpuContext ctx{};ctx.gpr[1]=sp;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                    for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                    ctx.gpr[3]=data;self_effect_init(&ctx);
                    if(Memory::Read32(data+0x24)!=n+3)return 4;
                    ctx.gpr[3]=data;world_effect_init(&ctx);
                    if(Memory::Read32(data+0x24)!=n+2)return 5;
                    const uint32_t name=data+0x100;Memory::Write8(name,'a');Memory::Write8(name+1,0);
                    for(uint32_t i=0;i<n;i++){
                        ctx.gpr[3]=data;ctx.gpr[4]=name;ctx.gpr[5]=i;func_80222C2C(&ctx);
                        if(Memory::Read32(data+0x24)!=i)return 6;
                    }
                    if(copied!=n+2 || resetCalls!=n+2 || ctx.gpr[1]!=sp)return 7;
                    for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r)return 8;
                    for(uint32_t i=0;i<16;i++)if(Memory::Read8(data-16+i)!=0x55 || Memory::Read8(data+0x7c+i)!=0x55 ||
                        Memory::Read8(sp-0x40-16+i)!=0x55 || Memory::Read8(sp+8+i)!=0x55)return 9;
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(45000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Effect creator role compilation timed out");
                Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(5000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Effect creator role execution timed out");
            Assert.True(run.ExitCode==0,$"Effect creator role lifecycle returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
