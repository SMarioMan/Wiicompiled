using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class RacedataCopyTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(48)]
    [Trait("Requires", "USA game assets and Clang")]
    public void OriginalAssignmentCopiesEveryPlayerAndRelocatedTailWithoutOverrunning(int players)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "translator/Translator.sln")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var image = new ProgramImageBuilder().Build(Path.Combine(root, "Assets/main.dol"),
            Path.Combine(root, "Assets/StaticR.rel"), 0x8050bf60);
        var path = Path.Combine(root, "projects/mkwii-ntsc-u/layouts/racedata.json");
        var relocations = GuestLayoutRelocations.Read(path, players);
        var map = FunctionMap.Load(Path.Combine(root, "projects/mkwii-ntsc-u/MAP.txt"));
        var translated = new FunctionTranslator(image, layoutRelocations: relocations).Translate(0x8052ba64,
            new TranslationOptions(EnableLeafInlining: false, KnownFunctionEntryPoints: map.Addresses.ToHashSet()));
        // Use the checked layout file as the source of ABI shapes, not host sizeof.
        using var definitions = JsonDocument.Parse(File.ReadAllText(path));
        var types = definitions.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!;
        var layout = new GuestLayouts(types, players);
        var size = layout.Symbol("Scenario.size");
        var settings = layout.Symbol("Scenario.settings.offset");
        var temporary = Path.Combine(Path.GetTempPath(), "mkw-scenario-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var source = Path.Combine(temporary, "scenario.cpp");
            var harness = Path.Combine(temporary, "harness.cpp");
            var executable = Path.Combine(temporary, "test");
            File.WriteAllText(source, translated.CxxCode);
            File.WriteAllText(harness, $$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <cstdio>
                extern "C" void func_8052BA64(CpuContext*);
                int main() {
                    Memory::Init(2 * 1024 * 1024);
                    constexpr uint32_t src = 0x80010000, dst = 0x80040000;
                    constexpr uint32_t size = {{size}}, settings = {{settings}};
                    for (uint32_t i = 0; i < size; i += 4) {
                        Memory::Write32(src+i, 0xa5000000u ^ (i * 0x12345u));
                        Memory::Write32(dst+i, 0xcdcdcdcdu);
                    }
                    Memory::Write8(src+4, {{players}});
                    for (uint32_t i=0; i<16; i++) {
                        Memory::Write8(dst-16+i, 0x7e);
                        Memory::Write8(dst+size+i, 0x7e);
                    }
                    CpuContext ctx{}; ctx.gpr[1] = 0x800f0000;
                    ctx.gpr[3] = dst; ctx.gpr[4] = src;
                    func_8052BA64(&ctx);
                    if (ctx.gpr[3] != dst || Memory::Read32(dst) != 0xcdcdcdcdu) return 1;
                    for (uint32_t i=4; i<8; i++)
                        if (Memory::Read8(dst+i) != Memory::Read8(src+i)) return 2;
                    for (uint32_t player=0; player<{{players}}; player++) {
                        uint32_t offset = 8 + player * 0xf0;
                        if (Memory::Read32(dst+offset) != 0xcdcdcdcdu) return 3;
                        for (uint32_t field : {8u, 12u, 16u, 0xccu})
                            if (Memory::Read32(dst+offset+field) != Memory::Read32(src+offset+field)) return 4;
                        if (Memory::Read16(dst+offset+0xe8) != Memory::Read16(src+offset+0xe8)) return 5;
                        if (Memory::Read8(dst+offset+0xec) != Memory::Read8(src+offset+0xec)) return 6;
                    }
                    for (uint32_t i=settings; i<size; i++) {
                        // The original assignment preserves these two settings padding bytes.
                        if (i == settings+0x26 || i == settings+0x27) {
                            if (Memory::Read8(dst+i) != 0xcd) return 9;
                            continue;
                        }
                        if (Memory::Read8(dst+i) != Memory::Read8(src+i)) {
                            std::fprintf(stderr, "Tail mismatch at +0x%x: copied=%02x expected=%02x\n", i,
                                Memory::Read8(dst+i), Memory::Read8(src+i));
                            return 7;
                        }
                    }
                    for (uint32_t i=0; i<16; i++)
                        if (Memory::Read8(dst-16+i) != 0x7e || Memory::Read8(dst+size+i) != 0x7e) return 8;
                    return 0;
                }
                """);
            var arguments = TranslatorCppTestHarness.BuildCompileArguments(root, temporary, new[] { source },
                harness, executable, includeDataSections: false);
            var compile = new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX") ?? "clang++", arguments)
            { RedirectStandardError = true };
            using (var process = Process.Start(compile)!)
            {
                var errorsTask = process.StandardError.ReadToEndAsync();
                var exited = process.WaitForExit(45000);
                if (!exited) process.Kill(entireProcessTree: true);
                Assert.True(exited, "Scenario copy compilation timed out");
                Assert.True(process.ExitCode == 0, errorsTask.GetAwaiter().GetResult());
            }
            using var run = Process.Start(new ProcessStartInfo(executable)
                { UseShellExecute = false, RedirectStandardError = true })!;
            var runtimeErrors = run.StandardError.ReadToEndAsync();
            var finished = run.WaitForExit(5000);
            if (!finished) run.Kill(entireProcessTree: true);
            Assert.True(finished, "Scenario copy execution timed out");
            Assert.True(run.ExitCode == 0, $"Copy returned {run.ExitCode}: {runtimeErrors.GetAwaiter().GetResult()}");
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }
}
