using System.Diagnostics;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class NativeBootLayoutTests
{
    [Theory]
    [InlineData(12, false)]
    [InlineData(12, true)]
    [InlineData(24, true)]
    [InlineData(48, true)]
    public void RealNativeBootUsesTheTranslatedGuestLayout(int players, bool explicitLayout)
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
                #include "boot_race.h"
                #include "mkw_guest_layout.h"
                #include <cstring>
                extern "C" void ArgumentParser_Parse_805b9344(CpuContext*);
                extern "C" void RacedataScenario_InitRng_8052f924(CpuContext*);
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
                    CpuContext ctx{}; ArgumentParser_Parse_805b9344(&ctx);
                    if(ctx.gpr[3]!=0x20) return 3;
                    const uint32_t menu=data+l.racedataMenu, settings=menu+l.scenarioSettings;
                    for(uint32_t i=0;i<l.playerCapacity;i++) {
                        const uint32_t p=menu+l.scenarioPlayers+i*l.playerStride;
                        if(Memory::Read32(p+0x10)!=(i==0?0:1) || Memory::Read32(p+8)>2 ||
                           Memory::Read32(p+0xc)>0x16 || Memory::Read8(p)!=0x7e) return 4;
                    }
                    if(Memory::Read32(settings)!=8 || Memory::Read32(settings+8)!=1 ||
                       Memory::Read32(settings+0x14)!=2 || Memory::Read32(settings+0x18)!=0) return 5;
                    for(uint32_t a=data+l.racedataRace;a<menu;a++) if(Memory::Read8(a)!=0x7e) return 6;
                    for(uint32_t a=menu+l.scenarioMission;a<data+l.racedataSize;a++)
                        if(Memory::Read8(a)!=0x7e) return 7;
                    for(uint32_t i=0;i<16;i++)
                        if(Memory::Read8(data-16+i)!=0x55 || Memory::Read8(data+l.racedataSize+i)!=0x55) return 8;
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
