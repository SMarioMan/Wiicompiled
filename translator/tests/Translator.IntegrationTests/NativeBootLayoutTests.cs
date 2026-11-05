using System.Diagnostics;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class NativeBootLayoutTests
{
    [Theory]
    [InlineData(12, false, 12)]
    [InlineData(12, true, 12)]
    [InlineData(24, true, 24)]
    [InlineData(24, true, 13)]
    [InlineData(48, true, 48)]
    [InlineData(48, true, 24)]
    public void RealNativeBootUsesTheTranslatedGuestLayout(int players, bool explicitLayout, int activePlayers)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "translator/Translator.sln")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var temp = Path.Combine(Path.GetTempPath(), "mkw-native-boot-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var header = Path.Combine(temp, "layout.h");
            var layout = GuestLayoutRelocations.Read(Path.Combine(root, "projects/mkwii-ntsc-u/layouts/racedata.json"), players);
            File.WriteAllText(header, layout.EmitNativeConstants());
            var glue = Path.Combine(temp, "glue.cpp");
            File.WriteAllText(glue, $"#define MKW_TRANSLATED_PLAYER_CAPACITY {players}\n" +
                (explicitLayout ? $"#define MKW_TRANSLATED_GUEST_LAYOUT_HEADER \"{header}\"\n" : "") +
                $"#include \"{root}/runtime/game/guest_layout_values.cpp\"\n");
            var harness = Path.Combine(temp, "harness.cpp"); var exe = Path.Combine(temp, "test");
            File.WriteAllText(harness, $$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include "boot_race.h"
                #include "mkw_guest_layout.h"
                #include <cstring>
                extern "C" void ArgumentParser_Parse_805b9344(CpuContext*);
                extern "C" void RacedataScenario_InitRng_8052f924(CpuContext*);
                extern "C" void Raceinfo_Update_805331b4(CpuContext*);
                extern "C" void InputManager_InitControllerHolders_805242d8(CpuContext*);
                extern "C" void RealControllerHolder_SetController_80521554(CpuContext*);
                extern "C" void Raceinfo_CreateInstance_80532084(CpuContext*);
                unsigned updateCalls=0,holderCalls=0;
                uint32_t expectedController=0;
                constexpr uint32_t inputManager=0x80090000,sectionManager=0x800a0000;
                void original_holders(CpuContext* ctx){if(ctx->gpr[3]!=inputManager)std::abort();ctx->gpr[3]=0x31415926;}
                void set_controller(CpuContext* ctx){const auto& l=MkwGuestLayout::kLayout;
                    if(ctx->gpr[3]!=inputManager+l.inputRealHolders || ctx->gpr[4]!=(expectedController?expectedController:inputManager+l.inputGCNControllers) || ctx->gpr[5]!=0)std::abort();
                    holderCalls++;Memory::Write32(ctx->gpr[3]+4,ctx->gpr[4]);ctx->gpr[3]=0xdeadbeef;ctx->gpr[4]=0;ctx->lr=0;}
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="boot input fixture";
                    info.rawCpuInvoker=entry;info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}

                void create_raceinfo(CpuContext* ctx){const auto& l=MkwGuestLayout::kLayout;
                    auto data=Memory::Read32(0x809bd728);
                    if(Memory::Read8(data+l.racedataRace+l.scenarioSettings+0x25)!=1 || Memory::Read32(data+l.racedataRace+l.scenarioSettings+0x28)!=0x104)std::abort();ctx->gpr[3]=0x80050000;}
                void original_update(CpuContext* ctx) {++updateCalls;Memory::Write32(ctx->gpr[3]+0x20,180);ctx->gpr[3]=0x31415926;}

                int main() {
                    Memory::Init(16*1024*1024);
                    const auto& l=MkwGuestLayout::kLayout;
                    constexpr uint32_t data=0x80010000;
                    if (l.playerCapacity!={{players}} || l.playerStride!=0xf0 ||
                        l.scenarioSettings!=8+{{players}}*0xf0 ||
                        l.racedataMenu!=0x20+l.scenarioSize ||
                        l.racedataAwards!=l.racedataMenu+l.scenarioSize ||
                        l.racedataGhosts!=l.racedataAwards+l.scenarioSize) return 1;
                    std::memset(Memory::GetPointer(data, l.racedataSize), 0x7e, l.racedataSize);
                    Memory::Write32(0x809bd728, data); // Harness region is PAL.
                    for (uint32_t i=0;i<16;i++) {
                        Memory::Write8(data-16+i, 0x55); Memory::Write8(data+l.racedataSize+i, 0x55);
                    }
                    if (!BootRace::ParseArgument("--race-cpu")) return 2;
                    if(!BootRace::ParseArgument("--race-players={{activePlayers}}") || !BootRace::ParseArgument("--race-laps=1"))return 18;
                    for(const char* bad:{"--race-players=0","--race-players={{players+1}}","--race-laps=0","--race-laps=10","--race-laps=1x"}){
                        bool rejected=false;try{BootRace::ParseArgument(bad);}catch(const std::invalid_argument&){rejected=true;}
                        if(!rejected)return 19;
                    }
                    CpuContext ctx{}; ArgumentParser_Parse_805b9344(&ctx);
                    if(ctx.gpr[3]!=0x20) return 3;
                    const uint32_t menu=data+l.racedataMenu, settings=menu+l.scenarioSettings;
                    for(uint32_t i=0;i<l.playerCapacity;i++) {
                        const uint32_t p=menu+l.scenarioPlayers+i*l.playerStride;
                        if(Memory::Read32(p+0x10)!=(i>={{activePlayers}}?5:i==0?0:1) || Memory::Read32(p+8)>2 ||
                           Memory::Read32(p+0xc)>0x16 || Memory::Read8(p)!=0x7e) return 4;
                    }
                    if(Memory::Read32(settings)!=8 || Memory::Read32(settings+8)!=1 ||
                       Memory::Read32(settings+0x14)!=2 || Memory::Read32(settings+0x18)!=0 || Memory::Read8(settings+0x25)!=1) return 5;
                    for(uint32_t a=data+l.racedataRace;a<menu;a++) if(Memory::Read8(a)!=0x7e) return 6;
                    for(uint32_t a=menu+l.scenarioMission;a<data+l.racedataSize;a++)
                        if(Memory::Read8(a)!=0x7e) return 7;
                    for(uint32_t i=0;i<16;i++)
                        if(Memory::Read8(data-16+i)!=0x55 || Memory::Read8(data+l.racedataSize+i)!=0x55) return 8;
                    constexpr uint32_t info=0x80050000,array=0x80060000,nodes=0x80070000;
                    Memory::Write8(data+l.racedataRace+4,{{players}});
                    Memory::Write32(data+l.racedataRace+l.scenarioSettings,8);
                    Memory::Write32(data+l.racedataRace+l.scenarioSettings+8,1);
                    Memory::Write32(info+0xc,array);Memory::Write32(info+0x28,2);
                    for(uint32_t i=0;i<{{players}};i++){Memory::Write32(array+i*4,nodes+i*0x80);Memory::Write32(nodes+i*0x80+0x2c,120+i);Memory::Write16(nodes+i*0x80+0x24,i%4);Memory::Write32(nodes+i*0x80+0x38,i%2?3:1);}
                    TranslatedFunctionInfo original{};original.address=0x805331b4;original.name="original update fixture";
                    original.rawCpuInvoker=original_update;original.entryPoint=reinterpret_cast<void*>(original_update);
                    TranslatedFunctionRegistry::Register(original);
                    ctx.gpr[3]=info;Raceinfo_Update_805331b4(&ctx);
                    if(updateCalls!=1 || ctx.gpr[3]!=0x31415926)return 9;
                    const auto progress=BootRace::ReadProgress(info);
                    if(!progress || progress->players!={{players}} || progress->capacity!={{players}} ||
                        progress->observedPlayers!={{players}} || progress->minPlayerFrames!=120 ||
                        progress->finishedPlayers!={{players}}/2 || progress->minLap!=0 || progress->maxLap!=3 ||
                        progress->stage!=2 || progress->frames!=180 || progress->course!=8 || progress->mode!=1)return 10;
                    Memory::Write8(data+l.racedataRace+4,{{players}}+1);
                    if(BootRace::ReadProgress(info)->observedPlayers!=0)return 11;
                    Memory::Write32(0x809bd728,0);if(BootRace::ReadProgress(info))return 12;
                    if(l.inputRealHolders!=4 || l.inputGCNControllers!=0x3ba0+({{players}}-12)*0x180)return 13;
                    register_entry(0x805242d8,original_holders);register_entry(0x80521554,set_controller);
                    Memory::Write32(0x809c1e38,sectionManager);Memory::Write32(sectionManager+0x38,0x124);
                    Memory::Write32(0x809bd70c,inputManager);
                    expectedController=0x800b0000;ctx.gpr[3]=inputManager+l.inputRealHolders;ctx.gpr[4]=expectedController;ctx.gpr[5]=0;
                    RealControllerHolder_SetController_80521554(&ctx);
                    if(holderCalls!=1 || Memory::Read32(inputManager+l.inputRealHolders+4)!=expectedController)return 16;
                    expectedController=0;
                    if(!BootRace::ParseArgument("--race-cpu-menu-input"))return 14;
                    ctx.gpr[3]=inputManager;const CpuContext before=ctx;
                    InputManager_InitControllerHolders_805242d8(&ctx);
                    CpuContext expected=before;expected.gpr[3]=0x31415926;
                    if(std::memcmp(&ctx,&expected,sizeof(ctx)) || holderCalls!=2 ||
                        Memory::Read32(inputManager+l.inputRealHolders+4)!=inputManager+l.inputGCNControllers ||
                        Memory::Read32(sectionManager+0x44)!=inputManager+l.inputRealHolders || Memory::Read32(sectionManager+0x3c)!=0x124)return 15;
                    ctx.gpr[3]=inputManager+l.inputRealHolders;ctx.gpr[4]=0x800b0000;ctx.gpr[5]=0;
                    RealControllerHolder_SetController_80521554(&ctx);
                    if(holderCalls!=3 || Memory::Read32(inputManager+l.inputRealHolders+4)!=inputManager+l.inputGCNControllers)return 17;
                    Memory::Write32(0x809bd728,data);Memory::Write8(data+l.racedataRace+l.scenarioSettings+0x25,3);
                    Memory::Write32(data+l.racedataRace+l.scenarioSettings+0x28,0x100);
                    register_entry(0x80532084,create_raceinfo);Raceinfo_CreateInstance_80532084(&ctx);
                    if(ctx.gpr[3]!=info)return 20;
                    return 0;
                }
                """);
            var sources = new[] {glue, Path.Combine(root, "runtime/src/hle/boot_race.cpp"), Path.Combine(root, "runtime/src/guest_region.cpp")};
            var compileInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX") ?? "clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root, temp, sources, harness, exe, includeDataSections: false))
                { RedirectStandardError = true };
            using(var compile=Process.Start(compileInfo)!)
            {
                var errors=compile.StandardError.ReadToEndAsync();
                var exited=compile.WaitForExit(45000);
                if(!exited) compile.Kill(entireProcessTree:true);
                Assert.True(exited, "Native boot compilation timed out");
                Assert.True(compile.ExitCode==0, errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe) {RedirectStandardError=true, UseShellExecute=false})!;
            var output=run.StandardError.ReadToEndAsync(); var finished=run.WaitForExit(5000);
            if(!finished) run.Kill(entireProcessTree:true);
            Assert.True(finished, "Native boot execution timed out");
            Assert.True(run.ExitCode==0, $"Native boot returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally { Directory.Delete(temp, recursive:true); }
    }
}
