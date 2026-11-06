using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class BattleAiRosterTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealBattleAiBuildsUniformTeamRostersAndReadsShiftedCounts(int players)
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
        var temp=Path.Combine(Path.GetTempPath(),"mkw-battle-ai-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var entries=new uint[]{0x8073dfc8,0x8073dd60,0x8073deec,0x8073e8c4,0x8073e9f8,0x8073ed1c};
            var fixtures=new HashSet<uint>{0x80229a48,0x80721e60,0x808888d4,0x8073ebd0,0x80888ef4,0x808894e4,0x8073ede8};
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
                constexpr uint32_t capacity={{players}},slots={{layout.Symbol("AIBattleTeamRoster.controllers.count")}},
                    size={{layout.Symbol("AIBattleTeamRoster.size")}},countOffset={{layout.Symbol("AIBattleTeamRoster.count.offset")}},
                    root=0x81010000,racedata=0x81020000,karts=0x81030000,kartPtrs=0x81031000,manager=0x81040000,
                    rosters=0x81050000,state=0x81060000,grids=0x81070000,raceinfo=0x81080000,objects=0x81100000,stack=0x81f00000;
                uint32_t allocations=0,gridConstructed=0,gridFinalized=0,gridInserted=0,tick=0;
                uint32_t counts[2]={0,0};bool gridPhase=false;
                uint32_t roster(uint32_t team){return rosters+team*1024;}
                uint32_t controller(uint32_t player){return objects+player*512+256;}
                void allocate(CpuContext* ctx){if(gridPhase){if(ctx->gpr[3]!=68 || allocations>=4)std::abort();ctx->gpr[3]=grids+(allocations++-2)*128;}
                    else {if(ctx->gpr[3]!=size || allocations>=2)std::abort();ctx->gpr[3]=roster(allocations++);} }
                void grid_ctor(CpuContext* ctx){auto team=gridConstructed++;if(team>=2 || ctx->gpr[3]!=grids+team*128 || ctx->gpr[5]!=counts[team] || ctx->gpr[6]!=counts[team])std::abort();}
                void grid_reset(CpuContext*){}
                void grid_finalize(CpuContext* ctx){if(ctx->gpr[3]!=grids+gridFinalized++*128)std::abort();}
                void grid_insert(CpuContext* ctx){auto team=(ctx->gpr[3]-grids)/128;if(team>=2 || !counts[team] || ctx->gpr[4]!=tick%counts[team])std::abort();
                    auto player=(team?counts[0]:0)+tick%counts[team];
                    for(uint32_t j=0;j<3;j++)if(Memory::ReadFloat32(ctx->gpr[5]+j*4)!=float(player)+float(j)*.25f)std::abort();gridInserted++;}
                void is_active(CpuContext* ctx){auto p=ctx->gpr[3];if(p<controller(0) || (p-controller(0))%512 || (p-controller(0))/512>=capacity)std::abort();
                    ctx->gpr[3]=((p-controller(0))/512)%3!=0;}
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="battle AI roster fixture";
                    info.rawCpuInvoker=entry;info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                bool guards(){for(uint32_t t=0;t<2;t++)for(uint32_t i=0;i<16;i++)
                    if(Memory::Read8(roster(t)-16+i)!=0x55 || Memory::Read8(roster(t)+size+i)!=0x55)return false;return true;}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    register_entry(0x80229a48,allocate);register_entry(0x80721e60,is_active);
                    register_entry(0x808888d4,grid_ctor);register_entry(0x8073ebd0,grid_reset);register_entry(0x80888ef4,grid_finalize);register_entry(0x808894e4,grid_insert);register_entry(0x8073ede8,grid_reset);
                    Memory::Write32(0x809b8f68,racedata);Memory::Write32(0x809bd110,karts);Memory::Write32(0x809be398,manager);
                    Memory::Write32(karts+32,kartPtrs);Memory::Write32(0x809b8f70,raceinfo);
                    Memory::Write32(manager+{{layout.Symbol("AIManager.battle.offset")}},root);
                    Memory::Write32(racedata+{{layout.Symbol("Racedata.race.offset+Scenario.settings.offset")}}+8,3);
                    Memory::WriteFloat32(state+420,100.0f);
                    for(uint32_t i=0;i<capacity;i++){
                        auto object=objects+i*512;Memory::Write32(kartPtrs+i*4,object);Memory::Write32(object,object+64);
                        Memory::Write32(object+64+56,controller(i));
                        Memory::Write32(controller(i),object+64);Memory::Write32(object+72,object+96);
                        Memory::Write32(object+240,object+320);Memory::Write32(object+324,object+384);
                        for(uint32_t j=0;j<3;j++)Memory::WriteFloat32(object+488+j*4,float(i)+float(j)*.25f);
                    }
                    CpuContext ctx{};ctx.gpr[1]=stack;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                    for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                    // The stock team assignment keeps each side within ceil(capacity/2).
                    // Exercise every legal team size, including empty, odd and full fields,
                    // rather than appending extension slots to the six authored entries.
                    for(uint32_t red=0;red<=slots;red++)for(uint32_t blue=0;blue<=slots && red+blue<=capacity;blue++){
                        counts[0]=red;counts[1]=blue;uint32_t activeCounts[2]={0,0};allocations=0;gridPhase=false;
                        for(uint32_t t=0;t<2;t++)std::memset(Memory::GetPointer(roster(t)-16,size+32),0x55,size+32);
                        Memory::Write32(manager+20,red+blue);
                        for(uint32_t i=0;i<red+blue;i++)Memory::Write32(racedata+{{layout.Symbol("Racedata.race.offset+Scenario.players.offset")}}+i*240+204,i<red?0:1);
                        ctx.gpr[3]=root;ctx.gpr[4]=red;ctx.gpr[5]=blue;func_8073DFC8(&ctx);
                        if(allocations!=2 || !guards())return 4;
                        for(uint32_t t=0;t<2;t++){
                            if(Memory::Read32(root+192+t*4)!=roster(t) || Memory::Read32(roster(t)+countOffset)!=counts[t])return 5;
                            for(uint32_t j=0;j<slots;j++){
                                auto i=j+(t?red:0),expected=j<counts[t]?controller(i):0;
                                if(Memory::Read32(roster(t)+j*4)!=expected)return 6;
                                if(j<counts[t] && i%3!=0)activeCounts[t]++;
                            }
                            ctx.gpr[3]=root;ctx.gpr[4]=t;func_8073DEEC(&ctx);
                            if(ctx.gpr[3]!=activeCounts[t] || !guards())return 7;
                        }
                        for(uint32_t i=0;i<red+blue;i++){
                            ctx.gpr[3]=root;ctx.gpr[4]=i;func_8073DD60(&ctx);
                            if(ctx.gpr[3]!=(i<red?blue:red))return 8;
                        }
                        gridPhase=true;gridConstructed=gridFinalized=gridInserted=0;
                        Memory::Write32(state+12,0);ctx.gpr[3]=state;func_8073E8C4(&ctx);
                        if(allocations!=4 || gridConstructed!=2 || gridFinalized!=2 || !guards())return 11;
                        for(uint32_t t=0;t<2;t++)if(Memory::Read32(state+4+t*4)!=grids+t*128)return 12;
                        // Empty-team update grids are absent; the stock update skips
                        // a null grid. Nonempty rosters select a real controller by
                        // frame modulo the shifted count and submit its position.
                        for(uint32_t t=0;t<2;t++)if(!counts[t])Memory::Write32(state+4+t*4,0);
                        for(tick=0;tick<3*slots;tick++){
                            Memory::Write32(raceinfo+32,tick);ctx.gpr[3]=state;func_8073E9F8(&ctx);
                            if(gridInserted!=(tick+1)*uint32_t((red!=0)+(blue!=0)) || !guards())return 13;
                        }
                        for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r)return 9;
                        if(ctx.gpr[1]!=stack)return 10;
                    }
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Battle AI roster compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Battle AI roster execution timed out");
            Assert.True(run.ExitCode==0,$"Battle AI roster returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
