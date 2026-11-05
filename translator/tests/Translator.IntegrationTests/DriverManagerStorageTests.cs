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

public class DriverManagerStorageTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealDriverLifecycleUpdatesAndAllFinishClassifiersUseUniformPlayerStorage(int players)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var manifest=Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json");
        using var doc=JsonDocument.Parse(File.ReadAllText(manifest));
        var layout=new GuestLayouts(doc.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!,players);
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));
        var options=new TranslationOptions(KnownFunctionEntryPoints:map.Addresses.ToHashSet());
        var relocations=GuestLayoutRelocations.Read(manifest,players);
        var translator=new FunctionTranslator(image,layoutRelocations:relocations);
        var temp=Path.Combine(Path.GetTempPath(),"mkw-driver-storage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var entries=new uint[] {0x80020494,0x8002058c,0x80596f78,0x8050f18c,0x80517f00,0x80517ec0,
                0x807839dc,0x80783a50,0x80783e18,0x80783f40,0x80784818,0x80783f98,0x80784194,0x8078436c,0x80787e1c};
            var sources=new List<string>();
            foreach(var pc in entries)
            {
                var file=Path.Combine(temp,$"{pc:X8}.cpp");File.WriteAllText(file,translator.Translate(pc,options).CxxCode);sources.Add(file);
            }
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var declarations=string.Join("\n",entries.Select(pc=>$"extern \"C\" void func_{pc:X8}(CpuContext*);"));
            var registrations=string.Join("\n",entries.Where(pc=>pc!=0x80596f78 && pc!=0x8050f18c && pc!=0x80517f00 && pc!=0x80517ec0).Select(pc=>$"register_entry(0x{pc:X8},func_{pc:X8});"));
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <array>
                #include <cstdlib>
                #include <cstring>
                #include <cstdio>
                {{declarations}}
                constexpr uint32_t data=0x81010000,root=0x81020000,race=0x81040000,nodes=0x81041000,
                    kart=0x81060000,save=0x81070000,models=0x81100000,sp=0x81f00000,n={{players}},
                    size={{layout.Symbol("DriverManager.size")}},
                    position={{layout.Symbol("DriverManager.positions.offset")}},
                    target={{layout.Symbol("DriverManager.targetingItemPosition.offset")}},
                    frames={{layout.Symbol("DriverManager.frameCounter.offset")}},
                    inverted={{layout.Symbol("DriverManager.invertedFrameCounter.offset")}},
                    ranks={{layout.Symbol("DriverManager.racePositions.offset")}},
                    finish={{layout.Symbol("DriverManager.finishTypes.offset")}},
                    top5={{layout.Symbol("DriverManager.top5.offset")}},
                    settings={{layout.Symbol("Racedata.race.offset+Scenario.settings.offset")}};
                std::array<bool,n> initialized{},destroyed{};
                uint32_t allocations=0,constructed=0,destructed=0,timers=0,timerDestructed=0,modelDestructed=0;
                uint32_t position_index(uint32_t p){if(p< data+position || p>=data+position+n*12 || (p-data-position)%12)std::abort();return (p-data-position)/12;}
                void allocate(CpuContext* ctx){if(ctx->gpr[3]!=size)std::abort();allocations++;ctx->gpr[3]=data;}
                void no_op(CpuContext*){}
                void no_record(CpuContext* ctx){ctx->gpr[3]=0;}
                void construct_position(CpuContext* ctx){auto i=position_index(ctx->gpr[3]);if(initialized[i])std::abort();
                    initialized[i]=true;constructed++;func_80596F78(ctx);}
                void destroy_position(CpuContext* ctx){auto i=position_index(ctx->gpr[3]);if(destroyed[i])std::abort();
                    destroyed[i]=true;destructed++;func_8050F18C(ctx);}
                void construct_timer(CpuContext* ctx){if(ctx->gpr[3]!=data+top5+timers*12 || timers>=5)std::abort();timers++;func_80517F00(ctx);}
                void destroy_timer(CpuContext* ctx){if(ctx->gpr[3]!=data+top5+(4-timerDestructed)*12 || timerDestructed>=5)std::abort();timerDestructed++;func_80517EC0(ctx);}
                void destroy_model(CpuContext* ctx){if(ctx->gpr[3]!=models+modelDestructed*0x300 || modelDestructed>=n)std::abort();modelDestructed++;}
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="driver manager storage";
                    info.rawCpuInvoker=entry;info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);
                    FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    register_entry(0x80229a48,allocate);register_entry(0x8021a050,no_op);register_entry(0x8021a0a4,no_op);
                    register_entry(0x807ae770,no_op);register_entry(0x807b8778,no_op);register_entry(0x805438f4,no_record);
                    register_entry(0x80596f78,construct_position);register_entry(0x8050f18c,destroy_position);
                    register_entry(0x80517f00,construct_timer);register_entry(0x80517ec0,destroy_timer);
                    register_entry(0x807c3d24,destroy_model);
                    Memory::Write32(0x809be740,0);Memory::Write32(0x809b8f68,root);Memory::Write32(0x809b8f70,race);
                    Memory::Write32(0x809bd110,kart);Memory::Write32(0x809b8f88,save);Memory::Write16(save+0x36,0xffff);
                    Memory::Write8(root+0x24,n);Memory::Write32(root+settings+8,0);Memory::Write32(root+settings+0x28,0);
                    Memory::Write32(race+0xc,nodes);Memory::Write8(0x809bf0b1,0);
                    for(uint32_t i=0;i<n;i++){auto p=nodes+0x400+i*0x80;Memory::Write32(nodes+i*4,p);Memory::Write8(p+0x20,n-i);Memory::Write32(p+0x38,0);}
                    std::memset(Memory::GetPointer(data,size),0x7e,size);
                    for(uint32_t i=0;i<16;i++){Memory::Write8(data-16+i,0x55);Memory::Write8(data+size+i,0x55);
                        Memory::Write8(sp-0x100-16+i,0x55);Memory::Write8(sp+8+i,0x55);}
                    CpuContext ctx{};ctx.gpr[1]=sp;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                    for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                    func_807839DC(&ctx);
                    if(allocations!=1 || constructed!=n || timers!=5 || ctx.gpr[3]!=data || Memory::Read32(0x809be740)!=data)return 4;
                    for(uint32_t i=0;i<n;i++){
                        if(Memory::Read32(data+0x10+i*4) || Memory::Read32(data+target+i*4) || Memory::Read32(data+frames+i*4) ||
                            Memory::Read32(data+inverted+i*4) || Memory::Read32(data+finish+i*4)!=4 || Memory::Read8(data+ranks+i)!=n-i)return 5;
                        ctx.gpr[3]=data;ctx.gpr[4]=i;ctx.gpr[5]=models+i*0x300;func_80783F40(&ctx);
                        Memory::Write32(models+i*0x300+0x2bc,0);
                    }
                    if(Memory::Read8(data+{{layout.Symbol("DriverManager.focusedPlayer.offset")}})!=n)return 6;
                    for(uint32_t i=0;i<5;i++)if(Memory::Read8(data+{{layout.Symbol("DriverManager.hasTime.offset")}}+i))return 7;
                    for(auto awards:{0u,1u}){
                        Memory::Write8(data+{{layout.Symbol("DriverManager.isAwards.offset")}},awards);
                        ctx.gpr[3]=data;func_80787E1C(&ctx);if(ctx.gpr[3]!=awards)return 15;
                    }
                    Memory::Write8(data+{{layout.Symbol("DriverManager.isAwards.offset")}},0);
                    ctx.gpr[3]=data;func_80784818(&ctx);
                    for(uint32_t i=0;i<n;i++)if(Memory::Read32(data+frames+i*4)!=1 || Memory::Read32(data+inverted+i*4)!=0xffffffff ||
                        Memory::Read8(data+ranks+i)!=n-i || Memory::Read32(data+0x10+i*4)!=models+i*0x300)return 8;
                    // Execute every authored field-size row, every expanded row, every position,
                    // all four rank-based modes, and all three real classifier entry points.
                    const uint32_t authored[]={1,1,2,2,3,3,4,4,5,5,5,5};
                    for(auto classifier:{func_80783F98,func_80784194,func_8078436C})
                    for(auto mode:{0u,1u,7u,8u})for(uint32_t count=1;count<=n;count++){
                        Memory::Write32(root+settings+8,mode);Memory::Write8(kart+0x24,count);
                        auto cutoff=count<=12?authored[count-1]:5*count/12;
                        for(uint32_t i=0;i<count;i++){
                            auto p=Memory::Read32(nodes+i*4);Memory::Write8(p+0x20,i+1);Memory::Write32(data+finish+i*4,4);
                            ctx.gpr[3]=i;classifier(&ctx);uint32_t expected=i==0?0:i+1<=cutoff?1:2;
                            if(ctx.gpr[3]!=expected){std::fprintf(stderr,"finish mode=%u count=%u player=%u got=%u expected=%u\n",mode,count,i,ctx.gpr[3],expected);return 9;}
                            if(classifier==func_80783F98){if(Memory::Read32(data+finish+i*4)!=expected)return 10;
                                Memory::Write8(p+0x20,count+1);ctx.gpr[3]=i;classifier(&ctx);if(ctx.gpr[3]!=expected)return 11;}
                        }
                    }
                    Memory::Write32(0x809be740,0);ctx.gpr[3]=data;ctx.gpr[4]=0;func_80783E18(&ctx);
                    if(destructed!=n || timerDestructed!=5 || modelDestructed!=n || ctx.gpr[1]!=sp)return 12;
                    for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r)return 13;
                    for(uint32_t i=0;i<16;i++)if(Memory::Read8(data-16+i)!=0x55 || Memory::Read8(data+size+i)!=0x55 ||
                        Memory::Read8(sp-0x100-16+i)!=0x55 || Memory::Read8(sp+8+i)!=0x55)return 14;
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(45000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Driver manager compilation timed out");
                Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(5000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Driver manager execution timed out");
            Assert.True(run.ExitCode==0,$"Driver manager lifecycle returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
