using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class ObjectPoolCapacityTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealObjectManagerAllocatesAndRegistersTheCompleteExpandedObjectBudget(int players)
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
        var temp=Path.Combine(Path.GetTempPath(),"mkw-object-pools-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var entries=new uint[]{0x800202d4,0x80816878,0x808175d4,0x80816b80};
            var fixtures=new HashSet<uint>{0x80229a48,0x80229a6c,0x808185f8,0x807ede88,0x80596f78,
                0x80010000,0x80010004,0x80010008,0x8001000c,0x80229a90};
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
                constexpr uint32_t n={{players}},bytes={{layout.Symbol("ObjectPoolCapacity.size")}},budget=bytes/4,
                    root=0x81010000,racedata=0x81020000,vtable=0x81030000,indexTable=0x81040000,flowEntries=0x81050000,
                    objects=0x81400000,stack=0x81f00000;
                uint32_t scalars=0,arrays=0,vectors=0,destroyed=0;
                std::array<uint32_t,8> pools{};
                void no_op(CpuContext*){}
                void scalar(CpuContext* ctx){auto size=scalars==0?16:20;if(scalars>=5 || ctx->gpr[3]!=size)std::abort();ctx->gpr[3]=0x81100000+scalars++*0x10000;}
                void array(CpuContext* ctx){if(arrays>=8)std::abort();auto size=arrays==6?3*bytes+16:bytes;
                    if(ctx->gpr[3]!=size)std::abort();auto p=0x81200010+arrays*0x10000;pools[arrays++]=p;
                    std::memset(Memory::GetPointer(p-16,size+32),0x55,size+32);ctx->gpr[3]=p;}
                void vector(CpuContext* ctx){if(vectors>=budget || ctx->gpr[3]!=pools[6]+16+vectors++*12)std::abort();}
                void properties(CpuContext* ctx){ctx->gpr[3]=0xb;}
                void object_id(CpuContext* ctx){ctx->gpr[3]=0;}
                void solid(CpuContext* ctx){ctx->gpr[3]=1;}
                void destroy(CpuContext* ctx){auto p=ctx->gpr[3];if(p!=objects+destroyed++*256 || ctx->gpr[4]!=1)std::abort();}
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="object pool capacity fixture";
                    info.rawCpuInvoker=entry;info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                bool guards(){for(uint32_t j=0;j<8;j++){auto size=j==6?3*bytes+16:bytes;
                    for(uint32_t i=0;i<16;i++)if(Memory::Read8(pools[j]-16+i)!=0x55 || Memory::Read8(pools[j]+size+i)!=0x55)return false;}return true;}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    register_entry(0x80229a48,scalar);register_entry(0x80229a6c,array);register_entry(0x808185f8,no_op);
                    register_entry(0x807ede88,no_op);register_entry(0x80596f78,vector);register_entry(0x80229a90,no_op);
                    register_entry(0x80010000,properties);register_entry(0x80010004,object_id);register_entry(0x80010008,solid);register_entry(0x8001000c,destroy);
                    Memory::Write32(0x809b8f68,racedata);Memory::Write32(racedata+{{layout.Symbol("Racedata.race.offset+Scenario.settings.offset")}},8);
                    Memory::Write32(racedata+{{layout.Symbol("Racedata.race.offset+Scenario.settings.offset")}}+8,3);
                    Memory::Write32(0x809bfaf0,root);std::memset(Memory::GetPointer(root,0x160),0,0x160);
                    CpuContext ctx{};ctx.gpr[1]=stack;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                    for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                    ctx.gpr[3]=root;func_80816878(&ctx);
                    if(ctx.gpr[3]!=root || scalars!=5 || arrays!=8 || vectors!=budget || !guards())return 4;
                    constexpr std::array<uint32_t,7> offsets={0x1c,0x24,0x2c,0x3c,0x34,0x40,0x48};
                    for(uint32_t i=0;i<offsets.size();i++)if(Memory::Read32(root+offsets[i])!=pools[i==6?7:i])return 5;
                    if(Memory::Read32(root+0x44)!=pools[6]+16)return 6;
                    auto flow=Memory::Read32(root+4);Memory::Write32(flow+12,indexTable);Memory::Write32(flow+8,flowEntries);
                    Memory::Write16(indexTable,0);Memory::Write16(flowEntries+0x6a,1);
                    Memory::Write32(vtable+0x2c,0x80010000);Memory::Write32(vtable+0x24,0x80010004);
                    Memory::Write32(vtable+0xb4,0x80010008);Memory::Write32(vtable+8,0x8001000c);
                    for(uint32_t i=0;i<budget;i++){
                        auto p=objects+i*256;Memory::Write32(p,vtable);ctx.gpr[3]=root;ctx.gpr[4]=p;func_808175D4(&ctx);
                        if(ctx.gpr[3]!=i || Memory::Read32(p+0xa4)!=i || !guards())return 7;
                    }
                    for(uint32_t offset:{0x18u,0x20u,0x28u,0x30u,0x38u}){
                        if(Memory::Read16(root+offset)!=budget)return 8;
                        auto pool=Memory::Read32(root+offset+4);
                        for(uint32_t i=0;i<budget;i++)if(Memory::Read32(pool+i*4)!=objects+i*256)return 9;
                    }
                    ctx.gpr[3]=root;ctx.gpr[4]=objects;func_808175D4(&ctx);if(ctx.gpr[3]!=uint32_t(-1) || !guards())return 10;
                    ctx.gpr[3]=root;ctx.gpr[4]=0;func_80816B80(&ctx);if(destroyed!=budget || !guards())return 11;
                    for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r)return 12;
                    if(ctx.gpr[1]!=stack)return 13;
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Object pool compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Object pool execution timed out");
            Assert.True(run.ExitCode==0,$"Object pool returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
