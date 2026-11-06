using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class RootHeapBudgetTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealRootHeapSetupPartitionsMappedMemoryWithoutOverlappingOwnership(int players)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var manifest=Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json");
        using var document=JsonDocument.Parse(File.ReadAllText(manifest));
        var layout=new GuestLayouts(document.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!,players);
        var plan=GuestLayoutRelocations.Read(manifest,players);
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));
        var translator=new FunctionTranslator(image,layoutRelocations:plan);
        var temp=Path.Combine(Path.GetTempPath(),"mkw-root-budget-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var entries=new uint[]{0x80241648};
            var fixtures=new HashSet<uint>{0x801a0f28,0x80229324,0x80226698,0x80226744,0x802299f0};
            var sources=new List<string>();
            foreach(var pc in entries){var path=Path.Combine(temp,$"{pc:X8}.cpp");
                File.WriteAllText(path,translator.Translate(pc,new TranslationOptions(KnownFunctionEntryPoints:map.Addresses.ToHashSet(),LeafInliningBlockedTargets:fixtures)).CxxCode);sources.Add(path);}
            var declarations=string.Join("\n",entries.Select(pc=>$"extern \"C\" void func_{pc:X8}(CpuContext*);"));
            var registrations=string.Join("\n",entries.Select(pc=>$"register_entry(0x{pc:X8},func_{pc:X8});"));
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <array>
                #include <cstdio>
                #include <cstring>
                {{declarations}}
                constexpr uint32_t capacity={{players}},root=0x81010000,heaps=0x81020000,stack=0x81f00000;
                uint32_t created=0,subheaps=0,arenaCalls=0,current=0;
                struct Range {uint32_t start,size;};std::array<Range,3> ranges{};
                void init_arena(CpuContext* ctx){if(ctx->gpr[5]!=2 || ctx->gpr[3]>=ctx->gpr[4])std::abort();ctx->gpr[3]+=256;arenaCalls++;}
                void noop(CpuContext*){}
                void create_root(CpuContext* ctx){if(created>=3 || ctx->gpr[5]!=0 || ctx->gpr[3]%32 || ctx->gpr[4]%32 || ctx->gpr[4]<128)std::abort();
                    ranges[created]={ctx->gpr[3],ctx->gpr[4]};ctx->gpr[3]=heaps+created++*128;}
                void create_system(CpuContext* ctx){if(ctx->gpr[3]!=1024*1024 || ctx->gpr[4]!=heaps || ctx->gpr[5])std::abort();ctx->gpr[3]=heaps+512;subheaps++;}
                void make_current(CpuContext* ctx){if(ctx->gpr[3]!=heaps+512)std::abort();current++;}
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="root heap partition fixture";
                    info.rawCpuInvoker=entry;info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    register_entry(0x801a0f28,init_arena);register_entry(0x80229324,noop);register_entry(0x80226698,create_root);
                    register_entry(0x80226744,create_system);register_entry(0x802299f0,make_current);
                    CpuContext ctx{};ctx.gpr[1]=stack;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                    for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                    for(uint32_t end:{0x93dc0000u,0x97dc0000u}){
                        created=subheaps=arenaCalls=current=0;ranges={};
                        std::memset(Memory::GetPointer(root-16,128),0x55,128);Memory::Write32(root+52,1024*1024);
                        Memory::Write32(ctx.gpr[13]-29024,0x80010000);Memory::Write32(ctx.gpr[13]-25416,0x81700000);
                        Memory::Write32(ctx.gpr[13]-29020,0x90000800);Memory::Write32(ctx.gpr[13]-25412,end);
                        ctx.gpr[3]=root;func_80241648(&ctx);
                        auto expectedReserve=end==0x97dc0000?uint32_t(23322624/capacity*32):0;
                        if(created!=(expectedReserve?3:2) || subheaps!=1 || arenaCalls!=2 || current!=1)return 4;
                        if(ranges[0].start!=0x80010100 || ranges[0].size!=0x81700000-0x80010100)return 5;
                        if(ranges[1].start!=0x90000900 || ranges[1].size!=end-0x90000900-expectedReserve)return 6;
                        if(expectedReserve && (ranges[2].start!=ranges[1].start+ranges[1].size || ranges[2].size!=expectedReserve || ranges[2].start+ranges[2].size!=end))return 7;
                        if(Memory::Read32(root+24)!=heaps || Memory::Read32(root+28)!=heaps+128 ||
                            Memory::Read32(root+32)!=(expectedReserve?heaps+256:0) || Memory::Read32(root+36)!=heaps+512)return 8;
                        for(uint32_t i=0;i<16;i++)if(Memory::Read8(root-16+i)!=0x55 || Memory::Read8(root+80+i)!=0x55)return 9;
                        for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r)return 10;
                        if(ctx.gpr[1]!=stack)return 11;
                    }
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Root heap budget compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Root heap budget execution timed out");
            Assert.True(run.ExitCode==0,$"Root heap budget returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
