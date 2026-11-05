using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Analysis;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

[CollectionDefinition("Game thunk translation", DisableParallelization = true)]
public class GameThunkTranslationCollection { }

[Collection("Game thunk translation")]
public class RacedataPointsTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(48)]
    [Trait("Requires", "USA game assets and Clang")]
    public void OriginalBattlePointUpdaterUsesExpandedStackWithoutClobberingSavedRegisters(int players)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "translator/Translator.sln")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var image = new ProgramImageBuilder().Build(Path.Combine(root, "Assets/main.dol"),
            Path.Combine(root, "Assets/StaticR.rel"), 0x8050bf60);
        var manifest = Path.Combine(root, "projects/mkwii-ntsc-u/layouts/racedata.json");
        var map = FunctionMap.Load(Path.Combine(root, "projects/mkwii-ntsc-u/MAP.txt"));
        using var definition = JsonDocument.Parse(File.ReadAllText(manifest));
        var layout = new GuestLayouts(definition.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!, players);
        var previousThunks = GuestSaveRestoreThunks.Current;
        FunctionTranslationResult translated;
        try
        {
            GuestSaveRestoreThunks.Current = GuestSaveRestoreThunks.FromFunctionMap(map);
            translated = new FunctionTranslator(image, layoutRelocations: GuestLayoutRelocations.Read(manifest, players))
                .Translate(0x80529e08, new TranslationOptions(KnownFunctionEntryPoints: map.Addresses.ToHashSet()));
        }
        finally { GuestSaveRestoreThunks.Current = previousThunks; }
        var temporary = Path.Combine(Path.GetTempPath(), "mkw-battle-points-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var source = Path.Combine(temporary, "points.cpp");
            var harness = Path.Combine(temporary, "harness.cpp");
            var binary = Path.Combine(temporary, "original-memory.bin");
            var executable = Path.Combine(temporary, "test");
            File.WriteAllText(source, translated.CxxCode);
            File.WriteAllBytes(binary, image.Memory);
            File.WriteAllText(harness, $$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <cstdio>
                extern "C" void func_80529E08(CpuContext*);
                int main(int argc, char** argv) {
                    if (argc != 2) return 1;
                    Memory::Init(24 * 1024 * 1024);
                    FILE* input = std::fopen(argv[1], "rb");
                    if (!input) return 2;
                    size_t copied = std::fread(Memory::GetPointer(0x80000000, 24*1024*1024), 1, 24*1024*1024, input);
                    std::fclose(input);
                    if (copied != 24*1024*1024) return 3;
                    constexpr uint32_t data = 0x80010000, info = 0x80020000;
                    constexpr uint32_t race = data + {{layout.Symbol("Racedata.race.offset")}};
                    constexpr uint32_t settings = race + {{layout.Symbol("Scenario.settings.offset")}};
                    constexpr uint32_t array = race + {{layout.Symbol("Scenario.players.offset")}};
                    constexpr uint32_t sp = 0x800f0000, frame = {{layout.Symbol("PointsFrame.size")}};
                    Memory::Write32(0x809b8f68, data);
                    Memory::Write32(0x809b8f70, info);
                    Memory::Write8(race+4, {{players}});
                    Memory::Write32(settings+12, 0); // Ordinary race, not a replay.
                    Memory::Write32(settings+8, 3); // Offline battle: uses battle scores, not the unmigrated GP points table.
                    Memory::Write32(info+12, info+0x100);
                    for (uint32_t player=0; player<{{players}}; player++) {
                        uint32_t state = 0x80030000+player*0x54;
                        Memory::Write32(info+0x100+player*4, state);
                        Memory::Write8(state+0x20, player+1);
                        Memory::Write16(state+0x22, player+5);
                        Memory::Write32(array+player*0xf0+16, 1);
                        Memory::Write16(array+player*0xf0+0xd8, 3);
                        Memory::Write16(array+player*0xf0+0xda, 0);
                        Memory::Write16(array+player*0xf0+0xe8, 1000);
                    }
                    for (uint32_t i=0; i<16; i++) {
                        Memory::Write8(sp-frame-16+i, 0x7e);
                        Memory::Write8(sp+8+i, 0x7e);
                    }
                    CpuContext ctx{}; ctx.gpr[1] = sp; ctx.gpr[3] = race;
                    for (uint32_t reg=14; reg<=31; reg++) ctx.gpr[reg] = 0x12340000+reg;
                    CpuContextScope scope(&ctx);
                    func_80529E08(&ctx);
                    if (ctx.gpr[1] != sp) return 4;
                    for (uint32_t reg=14; reg<=31; reg++)
                        if (ctx.gpr[reg] != 0x12340000+reg) return 5;
                    for (uint32_t player=0; player<{{players}}; player++) {
                        if (Memory::Read16(array+player*0xf0+0xda) != player+8) return 6;
                        if (Memory::Read16(array+player*0xf0+0xe8) != 1000) return 7;
                    }
                    for (uint32_t i=0; i<16; i++)
                        if (Memory::Read8(sp-frame-16+i) != 0x7e || Memory::Read8(sp+8+i) != 0x7e) return 8;
                    return 0;
                }
                """);
            var arguments = TranslatorCppTestHarness.BuildCompileArguments(root, temporary, new[] { source },
                harness, executable, includeDataSections: false);
            var compile = new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX") ?? "clang++", arguments)
            { RedirectStandardError = true };
            using (var process = Process.Start(compile)!)
            {
                var errors = process.StandardError.ReadToEndAsync();
                var exited = process.WaitForExit(45000);
                if (!exited) process.Kill(entireProcessTree: true);
                Assert.True(exited, "Point updater compilation timed out");
                Assert.True(process.ExitCode == 0, errors.GetAwaiter().GetResult());
            }
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true };
            start.ArgumentList.Add(binary);
            using var run = Process.Start(start)!;
            var output = run.StandardError.ReadToEndAsync();
            var finished = run.WaitForExit(10000);
            if (!finished) run.Kill(entireProcessTree: true);
            Assert.True(finished, "Point updater execution timed out");
            Assert.True(run.ExitCode == 0, $"Point updater returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }
}
