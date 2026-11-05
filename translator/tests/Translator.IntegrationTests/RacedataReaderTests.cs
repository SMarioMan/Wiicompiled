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
    [InlineData(24)]
    [InlineData(48)]
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
            foreach (var entry in new uint[] { 0x8052a1d0, 0x8052c3c4, 0x8052d4e8, 0x80529160 })
            {
                var code = translator.Translate(entry, new TranslationOptions(EnableLeafInlining: false,
                    KnownFunctionEntryPoints: map.Addresses.ToHashSet())).CxxCode;
                var source = Path.Combine(temporary, $"reader_{entry:X8}.cpp");
                File.WriteAllText(source, code); sources.Add(source);
            }
            var harness = Path.Combine(temporary, "harness.cpp");
            var executable = Path.Combine(temporary, "test");
            File.WriteAllText(harness, $$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                extern "C" void func_8052A1D0(CpuContext*);
                extern "C" void func_8052C3C4(CpuContext*);
                extern "C" void func_8052D4E8(CpuContext*);
                extern "C" void func_80529160(CpuContext*);
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
                    return ctx.gpr[3] == 0 ? 0 : 6;
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
