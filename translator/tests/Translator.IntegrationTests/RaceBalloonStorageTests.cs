using System.Diagnostics;
using System.Text.Json;
using System.Buffers.Binary;
using Translator.Core.Analysis.Ssa;
using Translator.Core.Disassembly;
using Translator.Core.Lifting;
using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class RaceBalloonStorageTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealHudAllocationConstructsAndDestroysEveryPositionForAllFourPhysicalViews(int players)
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
        var relocations=GuestLayoutRelocations.Read(manifest,players);
        var translator=new FunctionTranslator(image,layoutRelocations:relocations);
        var temp=Path.Combine(Path.GetTempPath(),"mkw-ai-objects-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var entries=new uint[] {0x800202d4,0x80020494,0x8002058c,0x80596f78,0x8050f18c,0x807e7630,0x80836dd4,0x807e777c,0x807e7628,0x807e771c};
            var sources=new List<string>();
            foreach(var pc in entries)
            {
                var file=Path.Combine(temp,$"{pc:X8}.cpp");File.WriteAllText(file,translator.Translate(pc,options).CxxCode);sources.Add(file);
            }
            var nodes=new List<IrInstruction>();
            for(uint pc=0x80836794;pc<0x808367c0;pc+=4)
            {
                var word=BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(pc,4),4));
                var instruction=PpcDecoder.Decode(pc,word);
                nodes.AddRange(relocations.Apply(instruction,new PpcLifter().Lift(new[] {instruction})[0].Ir));
            }
            nodes.Add(new IrReturn(null));
            var function=new IrFunction("hud_balloon_allocate","0x80836794",new[] {new IrBasicBlock("0x80836794",nodes)});
            var allocation=translator.LowerCanonical(0x80836794,new SsaTransformer().Convert(function).Function,
                options with {PreferredName="hud_balloon_allocate"});
            var region=Path.Combine(temp,"allocation.cpp");File.WriteAllText(region,allocation.CxxCode);sources.Add(region);
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var declarations=string.Join("\n",entries.Select(pc=>$"extern \"C\" void func_{pc:X8}(CpuContext*);"));
            var registrations=string.Join("\n",entries.Where(pc=>pc!=0x80596f78 && pc!=0x8050f18c).Select(pc=>$"register_entry(0x{pc:X8},func_{pc:X8});"));
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <array>
                #include <cstdlib>
                #include <cstring>
                #include <cstdio>
                {{declarations}}
                extern "C" void hud_balloon_allocate(CpuContext*);
                constexpr uint32_t raw=0x81010000,data=raw+16,page=0x81100000,sp=0x81f00000,
                    n={{players}},size={{layout.Symbol("RaceBalloons.size")}},bytes=4*size+16;
                std::array<bool,n*4> initialized{},destroyed{};uint32_t allocations=0,constructed=0,destructed=0;
                uint32_t position_index(uint32_t p){
                    for(uint32_t hud=0;hud<4;hud++){
                        const uint32_t begin=data+hud*size+0x20;
                        if(p>=begin && p<begin+n*12 && (p-begin)%12==0)return hud*n+(p-begin)/12;
                    }
                    std::abort();
                }
                void allocate(CpuContext* ctx){if(ctx->gpr[3]!=bytes)std::abort();allocations++;ctx->gpr[3]=raw;}
                void construct_position(CpuContext* ctx){auto i=position_index(ctx->gpr[3]);if(initialized[i])std::abort();
                    initialized[i]=true;constructed++;func_80596F78(ctx);}
                void destroy_position(CpuContext* ctx){auto i=position_index(ctx->gpr[3]);if(destroyed[i])std::abort();
                    destroyed[i]=true;destructed++;func_8050F18C(ctx);}
                void focused_player(CpuContext* ctx){ctx->gpr[3]=ctx->gpr[4];}
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="race balloon storage";
                    info.rawCpuInvoker=entry;info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);
                    FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    register_entry(0x80229a6c,allocate);register_entry(0x80596f78,construct_position);
                    register_entry(0x8050f18c,destroy_position);register_entry(0x8052d428,focused_player);
                    std::memset(Memory::GetPointer(raw,bytes),0x7e,bytes);
                    for(uint32_t i=0;i<16;i++){Memory::Write8(raw-16+i,0x55);Memory::Write8(raw+bytes+i,0x55);
                        Memory::Write8(sp-0x100-16+i,0x55);Memory::Write8(sp+8+i,0x55);}
                    CpuContext ctx{};ctx.gpr[1]=sp;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                    for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                    ctx.gpr[15]=page;ctx.gpr[19]=4;hud_balloon_allocate(&ctx);
                    if(allocations!=1 || constructed!=4*n || Memory::Read32(page+0x1d4)!=data ||
                        Memory::Read32(raw)!=size || Memory::Read32(raw+4)!=4)return 4;
                    for(uint32_t hud=0;hud<4;hud++){
                        const uint32_t p=data+hud*size;
                        if(Memory::Read32(p+4)!=0)return 5;
                        for(uint32_t i=0;i<n*12;i++)if(Memory::Read8(p+0x20+i)!=0)return 6;
                        for(uint32_t a=0x20+n*12;a<size;a++)if(Memory::Read8(p+a)!=0x7e)return 7;
                        ctx.gpr[3]=p;ctx.gpr[4]=hud;func_807E771C(&ctx);
                        if(Memory::Read8(p+3)!=hud || Memory::Read32(p+0x14)!=hud)return 8;
                        for(uint32_t i=0;i<n;i++){
                            ctx.gpr[3]=p;ctx.gpr[4]=i;func_807E777C(&ctx);
                            if(ctx.gpr[3]!=p+0x20+i*12)return 9;
                            Memory::Write32(ctx.gpr[3],i);Memory::Write32(ctx.gpr[3]+4,hud);Memory::Write32(ctx.gpr[3]+8,0x3fa00000);
                        }
                        ctx.gpr[3]=p;ctx.gpr[4]=0x81120000+hud*0x100;func_807E7628(&ctx);
                        if(Memory::Read32(p+{{layout.Symbol("RaceBalloons.itemBalloon.offset")}})!=0x81120000+hud*0x100)return 10;
                        ctx.gpr[3]=p;ctx.gpr[4]=0;func_80836DD4(&ctx);
                    }
                    if(destructed!=4*n || ctx.gpr[1]!=sp)return 11;
                    for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=(r==15?page:r==19?4:0x12340000+r))return 12;
                    for(uint32_t i=0;i<16;i++)if(Memory::Read8(raw-16+i)!=0x55 || Memory::Read8(raw+bytes+i)!=0x55 ||
                        Memory::Read8(sp-0x100-16+i)!=0x55 || Memory::Read8(sp+8+i)!=0x55)return 13;
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(45000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Race balloon compilation timed out");
                Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(5000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Race balloon execution timed out");
            Assert.True(run.ExitCode==0,$"Race balloon lifecycle returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
