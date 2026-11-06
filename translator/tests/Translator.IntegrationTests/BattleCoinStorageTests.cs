using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class BattleCoinStorageTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealCoinConstructorAndResetKeepNestedPlayerArraysSeparate(int players)
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
        var temp=Path.Combine(Path.GetTempPath(),"mkw-battle-coins-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            // The two reset regions end at test-only BLRs. Neither exit is a
            // referenced instruction; production image files remain untouched.
            foreach(var exit in new uint[]{0x8086f4e8,0x8086f0dc,0x8086e014})
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(exit,4),4),0x4e800020);
            var entries=new uint[]{0x8086e684,0x8086e778,0x8086f470,0x8086f03c,0x80726b6c,0x8086dff0};
            var fixtures=new HashSet<uint>{0x80020494,0x805337fc,0x80229a48};
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
                constexpr uint32_t capacity={{players}},maxActive=capacity<24?capacity:24,
                    size={{layout.Symbol("BattleCoinManager.size")}},stride={{layout.Symbol("BattleCoinEntry.size")}},
                    positionOffset={{layout.Symbol("BattleCoinManager.positions.offset")}},
                    activeOffset={{layout.Symbol("BattleCoinManager.active.offset")}},
                    eligibleCount={{layout.Symbol("BattleCoinManager.eligibleCount.offset")}},
                    eligibleIds={{layout.Symbol("BattleCoinManager.eligibleIds.offset")}},
                    eligibleIndices={{layout.Symbol("BattleCoinManager.eligibleIndices.offset")}},
                    playerEffects={{layout.Symbol("BattleCoinManager.playerEffects.offset")}},
                    localCount={{layout.Symbol("BattleCoinManager.localCount.offset")}},
                    localEffects={{layout.Symbol("BattleCoinManager.localEffects.offset")}},
                    eventWords={{layout.Symbol("BattleCoinManager.playerEventWords.offset")}},
                    eventStates={{layout.Symbol("BattleCoinManager.playerEventStates.offset")}},
                    eventFlags={{layout.Symbol("BattleCoinManager.playerEventFlags.offset")}},
                    flags={{layout.Symbol("BattleCoinManager.flags.offset")}},
                    root=0x81010000,racedata=0x81030000,karts=0x81050000,stack=0x81f00000;
                uint32_t active=0,allocations=0,positions=0;
                void allocate(CpuContext* ctx){if(ctx->gpr[3]!=size || allocations++)std::abort();ctx->gpr[3]=root;}
                void array_ctor(CpuContext* ctx){
                    if(ctx->gpr[3]!=root+positionOffset || ctx->gpr[6]!=20 || ctx->gpr[7]!=255)std::abort();
                    positions++;std::memset(Memory::GetPointer(ctx->gpr[3],5100),0x33,5100);}
                void course(CpuContext*){std::abort();} // KMG is absent in this storage fixture.
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="coin storage fixture";
                    info.rawCpuInvoker=entry;info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                bool guards(){for(uint32_t i=0;i<16;i++)if(Memory::Read8(root-16+i)!=0x55 || Memory::Read8(root+size+i)!=0x55)return false;return true;}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    register_entry(0x80229a48,allocate);register_entry(0x80020494,array_ctor);register_entry(0x805337fc,course);
                    Memory::Write32(0x809b8f68,racedata);Memory::Write32(0x809bd110,karts);Memory::Write32(0x809b8f70,0x81060000);Memory::Write32(0x8106003c,0);
                    auto settings=racedata+{{layout.Symbol("Racedata.race.offset+Scenario.settings.offset")}};
                    Memory::Write32(settings+8,3);Memory::Write32(settings+16,1);
                    CpuContext ctx{};ctx.gpr[1]=stack;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                    for(active=1;active<=maxActive;active++)for(uint32_t local=1;local<=4;local++)for(uint32_t classification=0;classification<3;classification++){
                        for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                        allocations=positions=0;std::memset(Memory::GetPointer(root-16,size+32),0x55,size+32);
                        Memory::Write8(karts+36,active);Memory::Write8(racedata+37,local);Memory::Write32(0x809c014c,0);
                        Memory::Write32(settings+8,classification?9:3);uint32_t eligible=0;
                        for(uint32_t p=0;p<active;p++){
                            bool participates=classification==0 || (classification==1 && p%2==0);eligible+=participates;
                            Memory::Write32(racedata+{{layout.Symbol("Racedata.race.offset+Scenario.players.offset")}}+p*240+16,participates?0:1);
                        }
                        func_8086E684(&ctx);
                        if(allocations!=1 || positions!=1 || !guards() || Memory::Read32(0x809c014c)!=root)return 4;
                        if(Memory::Read8(root+activeOffset)!=active || Memory::Read8(root+localCount)!=local || Memory::Read8(root+eligibleCount)!=eligible){std::fprintf(stderr,"active %u/%u local %u/%u eligible %u/%u class %u\n",Memory::Read8(root+activeOffset),active,Memory::Read8(root+localCount),local,Memory::Read8(root+eligibleCount),eligible,classification);return 5;}
                        uint32_t next=0;
                        for(uint32_t p=0;p<active;p++){
                            bool participates=classification==0 || (classification==1 && p%2==0);
                            if(Memory::Read8(root+eligibleIndices+p)!=(participates?next:eligible))return 6;
                            if(participates && Memory::Read8(root+eligibleIds+next++)!=p)return 7;
                            if(Memory::Read32(root+playerEffects+p*4)!=0)return 8;
                        }
                        for(uint32_t p=active;p<capacity;p++)if(Memory::Read32(root+playerEffects+p*4)!=0x55555555)return 9;
                        for(uint32_t p=0;p<4;p++)if(Memory::Read32(root+localEffects+p*4)!=(p<local?0:0x55555555))return 10;
                        for(uint32_t i=0;i<100;i++){
                            if(Memory::Read32(root+12+i*stride)!=0)return 11;
                            for(uint32_t j=4;j<stride;j++)if(Memory::Read8(root+12+i*stride+j)!=0x55)return 12;
                        }
                        for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r)return 13;
                        if(ctx.gpr[1]!=stack)return 14;
                        // The real reset's first player loop initializes three
                        // neighboring arrays. Verify every other manager byte too.
                        std::array<uint8_t,32768> expected{};if(size>expected.size())return 15;
                        std::memcpy(expected.data(),Memory::GetPointer(root,size),size);
                        auto word=[&](uint32_t off,uint32_t v){for(int j=0;j<4;j++)expected[off+j]=v>>(24-j*8);};
                        expected[{{layout.Symbol("BattleCoinManager.events.offset")}}+8]=0;
                        word({{layout.Symbol("BattleCoinManager.events.offset")}}+12,0);
                        word({{layout.Symbol("BattleCoinManager.events.offset")}}+8740,0);
                        word({{layout.Symbol("BattleCoinManager.events.offset")}}+8744,0);
                        word({{layout.Symbol("BattleCoinManager.events.offset")}}+36,0);
                        word({{layout.Symbol("BattleCoinManager.events.offset")}}+8748,0);
                        word({{layout.Symbol("BattleCoinManager.events.offset")}}+8752,0);
                        for(uint32_t p=0;p<active;p++){word(eventWords+p*4,eligible?0:4);expected[eventStates+p]=1;expected[eventFlags+p]=0;}
                        ctx.gpr[24]=root;func_8086F470(&ctx);
                        if(!guards() || std::memcmp(expected.data(),Memory::GetPointer(root,size),size))return 16;
                        // Initialize each coin's participant flags using the real
                        // eight-at-a-time loop and scalar tail. Adjacent coins and
                        // unused capacity slots must retain their sentinels.
                        for(uint32_t coin=0;coin<100;coin++){
                            std::memcpy(expected.data(),Memory::GetPointer(root,size),size);
                            std::memset(expected.data()+27+coin*stride,0xA7,active);
                            Memory::Write8(0x808d7ab0,0xA7);
                            ctx.gpr[3]=root;ctx.gpr[5]=active;ctx.gpr[7]=0;ctx.gpr[8]=root+coin*stride;ctx.gpr[9]=coin*stride;
                            func_8086F03C(&ctx);
                            if(!guards() || std::memcmp(expected.data(),Memory::GetPointer(root,size),size))return 17;
                        }
                    }
                    // Two real external users obtain coin model pointers from
                    // the nested record stride, independently of player count.
                    Memory::Write8(root,100);Memory::Write32(0x809c014c,root);
                    for(uint32_t coin=0;coin<100;coin++){
                        auto model=0x81200000+coin*512,states=0x81300000+coin*16;
                        Memory::Write32(root+12+coin*stride,model);
                        Memory::Write32(model+0x160,states);Memory::Write32(model+0x168,0);
                        Memory::Write32(states,coin%4);
                        ctx.gpr[4]=coin;func_80726B6C(&ctx);
                        if(ctx.gpr[3]!=(coin%4>=2))return 18;
                        ctx.gpr[19]=coin;ctx.gpr[27]=root;func_8086DFF0(&ctx);
                        if(ctx.gpr[25]!=model || !guards())return 19;
                    }
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Coin storage compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(30000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Coin storage execution timed out");
            Assert.True(run.ExitCode==0,$"Coin storage returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
