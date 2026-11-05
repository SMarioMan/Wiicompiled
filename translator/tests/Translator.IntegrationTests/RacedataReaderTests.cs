using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class RacedataReaderTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RelocatedModeReadersAndOriginalTeamAggregationUseTheExpandedScenario(int players)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "translator/Translator.sln")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var image = new ProgramImageBuilder().Build(Path.Combine(root, "Assets/main.dol"),
            Path.Combine(root, "Assets/StaticR.rel"), 0x8050bf60);
        var manifest = Path.Combine(root, "projects/mkwii-ntsc-u/layouts/racedata.json");
        var map = FunctionMap.Load(Path.Combine(root, "projects/mkwii-ntsc-u/MAP.txt"));
        var translator = new FunctionTranslator(image, layoutRelocations: GuestLayoutRelocations.Read(manifest, players));
        using var definition = JsonDocument.Parse(File.ReadAllText(manifest));
        var layout = new GuestLayouts(definition.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!, players);
        var temporary = Path.Combine(Path.GetTempPath(), "mkw-scenario-readers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var sources = new List<string>();
            foreach (var entry in new uint[] { 0x8052a1d0, 0x8052c3c4, 0x8052d4e8, 0x80529160, 0x8052ac40, 0x8052d19c })
            {
                var code = translator.Translate(entry, new TranslationOptions(EnableLeafInlining: false,
                    KnownFunctionEntryPoints: map.Addresses.ToHashSet())).CxxCode;
                var source = Path.Combine(temporary, $"reader_{entry:X8}.cpp");
                File.WriteAllText(source, code); sources.Add(source);
            }
            var originalPrevious = Path.Combine(temporary, "original_previous.cpp");
            File.WriteAllText(originalPrevious, "#define func_8052D19C original_previous\n" + new FunctionTranslator(image).Translate(0x8052d19c,
                new TranslationOptions(EnableLeafInlining: false,
                    KnownFunctionEntryPoints: map.Addresses.ToHashSet())).CxxCode);
            sources.Add(originalPrevious);
            var harness = Path.Combine(temporary, "harness.cpp");
            var executable = Path.Combine(temporary, "test");
            File.WriteAllText(harness, $$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <cstring>
                extern "C" void func_8052A1D0(CpuContext*);
                extern "C" void func_8052C3C4(CpuContext*);
                extern "C" void func_8052D4E8(CpuContext*);
                extern "C" void func_80529160(CpuContext*);
                extern "C" void func_8052AC40(CpuContext*);
                extern "C" void func_8052D19C(CpuContext*);
                extern "C" void original_previous(CpuContext*);
                int main() {
                    Memory::Init(16 * 1024 * 1024);
                    constexpr uint32_t data = 0x80010000;
                    constexpr uint32_t race = data + {{layout.Symbol("Racedata.race.offset")}};
                    constexpr uint32_t settings = race + {{layout.Symbol("Scenario.settings.offset")}};
                    constexpr uint32_t array = race + {{layout.Symbol("Scenario.players.offset")}};
                    Memory::Write32(0x809b8f68, data);
                    CpuContext ctx{}; ctx.gpr[1] = 0x800f0000;
                    Memory::Write32(settings+12, 0x12345678);
                    ctx.gpr[3] = race; func_8052A1D0(&ctx);
                    if (ctx.gpr[3] != 0x12345678) return 1;
                    for (uint32_t flags : {0u, 1u, 2u, 3u, 4u}) {
                        Memory::Write32(settings+40, flags);
                        ctx.gpr[3] = race; func_8052C3C4(&ctx);
                        if (ctx.gpr[3] != ((flags>>1)&1)) return 2;
                    }
                    for (uint32_t mode : {0u, 2u, 5u}) {
                        Memory::Write32(settings+8, mode);
                        Memory::Write8(race+4, 1);
                        Memory::Write32(array+16, 3);
                        ctx.gpr[3] = data; func_8052D4E8(&ctx);
                        if (ctx.gpr[3] != (mode==2 || mode==5)) return 3;
                    }
                    Memory::Write8(race+4, 0);
                    ctx.gpr[3] = data; func_8052D4E8(&ctx);
                    if (ctx.gpr[3] != 0) return 4;
                    Memory::Write32(settings+8, 2);
                    Memory::Write8(race+4, 1);
                    Memory::Write32(array+16, 0);
                    ctx.gpr[3] = data; func_8052D4E8(&ctx);
                    if (ctx.gpr[3] != 0) return 7;
                    Memory::Write8(race+4, {{players}});
                    for (uint32_t player=0; player<{{players}}; player++) {
                        Memory::Write32(array+player*0xf0+0xcc, 0);
                        Memory::Write16(array+player*0xf0+0xd8, 1);
                    }
                    uint32_t last = array+({{players}}-1)*0xf0;
                    Memory::Write32(last+0xcc, 1);
                    Memory::Write16(last+0xd8, 100);
                    ctx.gpr[3] = race; func_80529160(&ctx);
                    if (ctx.gpr[3] != 1) return 5;
                    Memory::Write16(last+0xd8, 0);
                    ctx.gpr[3] = race; func_80529160(&ctx);
                    if (ctx.gpr[3] != 0) return 6;
                    constexpr uint32_t counts = 0x80040000;
                    for (uint32_t active=0; active<={{players}}; active++) {
                        Memory::Write32(settings+8, 0);
                        Memory::Write32(settings+12, 0);
                        for (uint32_t player=0; player<{{players}}; player++)
                            Memory::Write32(array+player*0xf0+16,
                                player >= active ? 5 : (player%3 == 0 ? 0 : 1));
                        for (uint32_t i=0; i<6; i++) Memory::Write8(counts+i, 0x7e);
                        ctx.gpr[3] = race; ctx.gpr[4] = counts;
                        ctx.gpr[5] = counts+2; ctx.gpr[6] = counts+4;
                        func_8052AC40(&ctx);
                        uint32_t local = (active+2)/3;
                        uint32_t screens = local == 0 ? 1 : (local >= 3 ? 4 : local);
                        if (Memory::Read8(counts) != active || Memory::Read8(counts+2) != screens ||
                            Memory::Read8(counts+4) != local) return 8;
                        for (uint32_t i=1; i<6; i+=2) if (Memory::Read8(counts+i) != 0x7e) return 9;
                    }
                    // Only the final capacity slot is active, so a twelve-slot scan must fail.
                    for (uint32_t player=0; player<{{players}}; player++)
                        Memory::Write32(array+player*0xf0+16, player=={{players}}-1 ? 0 : 5);
                    ctx.gpr[3] = race; ctx.gpr[4] = counts;
                    ctx.gpr[5] = counts+2; ctx.gpr[6] = counts+4;
                    func_8052AC40(&ctx);
                    if (Memory::Read8(counts) != 1 || Memory::Read8(counts+2) != 1 ||
                        Memory::Read8(counts+4) != 1) return 10;
                    for (uint32_t player=0; player<{{players}}; player++)
                        Memory::Write32(array+player*0xf0+16, 1);
                    for (uint32_t mode=0; mode<=11; mode++) for (uint32_t type : {0u, 2u, 3u, 4u, 7u}) {
                        Memory::Write32(settings+8, mode);
                        Memory::Write32(settings+12, type);
                        ctx.gpr[3] = race; ctx.gpr[4] = counts;
                        ctx.gpr[5] = counts+2; ctx.gpr[6] = counts+4;
                        func_8052AC40(&ctx);
                        uint32_t total = mode == 11 ? (type == 7 ? 3 : 6) : {{players}};
                        uint32_t screens = type == 3 ? 2 : (type == 4 ? 4 : 1);
                        if (Memory::Read8(counts) != total || Memory::Read8(counts+2) != screens ||
                            Memory::Read8(counts+4) != 0) return 11;
                    }
                    constexpr uint32_t menu = data + {{layout.Symbol("Racedata.menu.offset")}};
                    constexpr uint32_t menuSettings = menu + {{layout.Symbol("Scenario.settings.offset")}};
                    constexpr uint32_t menuPlayers = menu + {{layout.Symbol("Scenario.players.offset")}};
                    for (uint32_t counter : {0u, 99u, 100u}) {
                        Memory::Write8(menuSettings+0x24, counter);
                        for (uint32_t player=0; player<{{players}}; player++) {
                            uint32_t address = menuPlayers+player*0xf0;
                            Memory::Write16(address+0xda, 100+player);
                            Memory::Write16(address+0xd8, 0xbeef);
                            Memory::Write8(address+0xe2, player+1);
                            Memory::Write8(address+0xe1, 0xaa);
                            Memory::Write8(address+0xdc, 0x7e);
                        }
                        // Neither settings nor the neighboring race scenario may be used as an extra slot.
                        Memory::Write8(menuSettings, 0x7e);
                        Memory::Write8(menuPlayers-1, 0x7e);
                        CpuContext baseline = ctx;
                        constexpr uint32_t backup = 0x80060000, rootSize = {{layout.Symbol("Racedata.size")}};
                        if ({{players}} == 12) {
                            std::memcpy(Memory::GetPointer(backup, rootSize), Memory::GetPointer(data, rootSize), rootSize);
                            baseline.gpr[3] = backup; original_previous(&baseline);
                        }
                        ctx.gpr[3] = data; func_8052D19C(&ctx);
                        if ({{players}} == 12) {
                            if (std::memcmp(Memory::GetPointer(backup, rootSize), Memory::GetPointer(data, rootSize), rootSize) != 0) return 15;
                            for (uint32_t reg=0; reg<32; reg++) {
                                uint32_t expected = baseline.gpr[reg];
                                if (expected >= backup && expected < backup+rootSize) expected = data+(expected-backup);
                                if (ctx.gpr[reg] != expected) return 16;
                            }
                            if (ctx.cr != baseline.cr || ctx.lr != baseline.lr || ctx.ctr != baseline.ctr ||
                                ctx.xer != baseline.xer || ctx.fpscr != baseline.fpscr) return 17;
                        }
                        if (ctx.gpr[3] != (counter < 100 ? counter+1 : 0)) return 12;
                        for (uint32_t player=0; player<{{players}}; player++) {
                            uint32_t address = menuPlayers+player*0xf0;
                            if (Memory::Read16(address+0xd8) != 100+player ||
                                Memory::Read8(address+0xe1) != player+1 || Memory::Read8(address+0xdc) != 0x7e) return 13;
                        }
                        if (Memory::Read8(menuSettings) != 0x7e || Memory::Read8(menuPlayers-1) != 0x7e) return 14;
                    }
                    return 0;
                }
                """);
            var arguments = TranslatorCppTestHarness.BuildCompileArguments(root, temporary, sources,
                harness, executable, includeDataSections: false);
            var compile = new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX") ?? "clang++", arguments)
            { RedirectStandardError = true };
            using (var process = Process.Start(compile)!)
            {
                var errors = process.StandardError.ReadToEndAsync();
                var exited = process.WaitForExit(45000);
                if (!exited) process.Kill(entireProcessTree: true);
                Assert.True(exited, "Reader compilation timed out");
                Assert.True(process.ExitCode == 0, errors.GetAwaiter().GetResult());
            }
            using var run = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false })!;
            var finished = run.WaitForExit(5000);
            if (!finished) run.Kill(entireProcessTree: true);
            Assert.True(finished, "Reader execution timed out");
            Assert.Equal(0, run.ExitCode);
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }
}
