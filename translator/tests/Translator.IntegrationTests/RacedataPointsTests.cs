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
    [InlineData(12, 3)]
    [InlineData(24, 3)]
    [InlineData(48, 3)]
    [InlineData(12, 9)]
    [InlineData(24, 9)]
    [InlineData(48, 9)]
    [InlineData(12, 10)]
    [InlineData(24, 10)]
    [InlineData(48, 10)]
    [Trait("Requires", "USA game assets and Clang")]
    public void OriginalBattlePointUpdaterUsesExpandedStackWithoutClobberingSavedRegisters(int players, int mode)
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
            var helpers = new[] { 0x80528a84u, 0x80528ca4u, 0x80528d4cu, 0x80528dfcu };
            var generated = new List<string> { source };
            File.WriteAllText(source, translated.CxxCode);
            foreach (var helper in helpers)
            {
                var path = Path.Combine(temporary, $"helper-{helper:x8}.cpp");
                generated.Add(path);
                File.WriteAllText(path, new FunctionTranslator(image).Translate(helper,
                    new TranslationOptions(KnownFunctionEntryPoints: map.Addresses.ToHashSet())).CxxCode);
            }
            File.WriteAllBytes(binary, image.Memory);
            File.WriteAllText(harness, $$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <algorithm>
                #include <array>
                #include <cstdio>
                extern "C" void func_80529E08(CpuContext*);
                extern "C" void func_80528A84(CpuContext*);
                extern "C" void func_80528CA4(CpuContext*);
                extern "C" void func_80528D4C(CpuContext*);
                extern "C" void func_80528DFC(CpuContext*);
                // This fixture is pinned to USA addresses and has no regional bootstrap.
                static AbiTrampoline<decltype(func_80528A84)> spline(0x80528a84, "spline", func_80528A84);
                static AbiTrampoline<decltype(func_80528CA4)> gain(0x80528ca4, "gain", func_80528CA4);
                static AbiTrampoline<decltype(func_80528D4C)> loss(0x80528d4c, "loss", func_80528D4C);
                static AbiTrampoline<decltype(func_80528DFC)> add(0x80528dfc, "add", func_80528DFC);
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
                    Memory::Write32(settings+8, {{mode}}); // Offline/online battle use battle scores, not the GP points table.
                    Memory::Write32(info+12, info+0x100);
                    for (uint32_t player=0; player<{{players}}; player++) {
                        uint32_t state = 0x80030000+player*0x54;
                        Memory::Write32(info+0x100+player*4, state);
                        Memory::Write8(state+0x20, player+1);
                        Memory::Write16(state+0x22, player+5);
                        Memory::Write32(array+player*0xf0+16, 1);
                        Memory::Write16(array+player*0xf0+0xd8, 3);
                        Memory::Write16(array+player*0xf0+0xda, 0);
                        Memory::Write16(array+player*0xf0+0xe8, 1000+player*10);
                    }
                    // Compute each original pair's rating contribution separately. The updater
                    // must aggregate all opponents before applying any player's final rating.
                    std::array<int, {{players}}> expected{};
                    CpuContext oracle{}; oracle.gpr[1] = 0x800e0000;
                    {
                        CpuContextScope scope(&oracle);
                        for (uint32_t player=0; player<{{players}}; player++) {
                            int delta = 0;
                            if ({{mode}} >= 9) for (uint32_t other=0; other<{{players}}; other++) {
                                if (player == other) continue;
                                oracle.gpr[3] = array+player*0xf0+0xe4;
                                oracle.gpr[4] = array+other*0xf0+0xe4;
                                if (player > other) func_80528CA4(&oracle);
                                else func_80528D4C(&oracle);
                                delta += static_cast<int16_t>(oracle.gpr[3]);
                            }
                            expected[player] = std::clamp(1000+int(player)*10+delta, 1, 9999);
                        }
                    }
                    for (uint32_t i=0; i<16; i++) {
                        // Allow normal stack space for the nested rating/B-spline calls.
                        Memory::Write8(sp-frame-0x80-16+i, 0x7e);
                        Memory::Write8(sp+8+i, 0x7e);
                    }
                    CpuContext ctx{}; ctx.gpr[1] = sp; ctx.gpr[3] = race;
                    for (uint32_t reg=14; reg<=31; reg++) ctx.gpr[reg] = 0x12340000+reg;
                    ctx.fpr[30].d = 30.25; ctx.fpr[31].d = 31.5;
                    CpuContextScope scope(&ctx);
                    func_80529E08(&ctx);
                    if (ctx.gpr[1] != sp) return 4;
                    if (ctx.fpr[30].d != 30.25 || ctx.fpr[31].d != 31.5) return 9;
                    for (uint32_t reg=14; reg<=31; reg++)
                        if (ctx.gpr[reg] != 0x12340000+reg) return 5;
                    for (uint32_t player=0; player<{{players}}; player++) {
                        if (Memory::Read16(array+player*0xf0+0xda) != player+8) return 6;
                        if (Memory::Read16(array+player*0xf0+0xe8) != expected[player]) {
                            std::fprintf(stderr, "player %u rating %u, expected %d\n", player,
                                Memory::Read16(array+player*0xf0+0xe8), expected[player]);
                            return 7;
                        }
                    }
                    for (uint32_t i=0; i<16; i++)
                        if (Memory::Read8(sp-frame-0x80-16+i) != 0x7e || Memory::Read8(sp+8+i) != 0x7e) return 8;
                    return 0;
                }
                """);
            var arguments = TranslatorCppTestHarness.BuildCompileArguments(root, temporary, generated,
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
