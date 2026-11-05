using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Translator.Core.Analysis.Ssa;
using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class RacedataInitializerTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void OriginalScenarioResetAndInitializationPrefixCoverExactlyTheExpandedArrays(int players)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "translator/Translator.sln")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var image = new ProgramImageBuilder().Build(Path.Combine(root, "Assets/main.dol"),
            Path.Combine(root, "Assets/StaticR.rel"), 0x8050bf60);
        var manifest = Path.Combine(root, "projects/mkwii-ntsc-u/layouts/racedata.json");
        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        var layout = new GuestLayouts(document.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!, players);
        var map = FunctionMap.Load(Path.Combine(root, "projects/mkwii-ntsc-u/MAP.txt"));
        var translator = new FunctionTranslator(image, layoutRelocations: GuestLayoutRelocations.Read(manifest, players));
        var options = new TranslationOptions(EnableLeafInlining: false, KnownFunctionEntryPoints: map.Addresses.ToHashSet());
        var reset = translator.Translate(0x8052990c, options);
        // Exercise the real initialization prefix through its inlined resets, ending before
        // external parameter-file registration. This is a region test, not a full Init claim.
        const uint cut = 0x805294e4;
        var discovery = translator.Discover(0x805291f8, options);
        var blocks = new List<IrBasicBlock>(); var found = false;
        foreach (var block in discovery.LinearIr.Blocks)
        {
            if (!uint.TryParse(block.Label.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var start) || start > cut) continue;
            var instructions = new List<IrInstruction>();
            foreach (var instruction in block.Instructions)
            {
                instructions.Add(instruction);
                if (instruction is not IrTracePpc trace || trace.Address != cut) continue;
                instructions.Add(new IrReturn(null)); found = true; break;
            }
            blocks.Add(block with { Instructions = instructions });
        }
        Assert.True(found, "Initialization prefix cutoff was not found");
        Assert.DoesNotContain(blocks.SelectMany(block => block.Instructions).OfType<IrCall>(),
            call => call.Target.StartsWith("0x", StringComparison.Ordinal));
        var prefix = discovery.LinearIr with { Name = "init_prefix", Blocks = blocks };
        var translatedPrefix = translator.LowerCanonical(0x805291f8, new SsaTransformer().Convert(prefix).Function,
            options with { PreferredName = "init_prefix" });
        var temporary = Path.Combine(Path.GetTempPath(), "mkw-initializers-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var resetPath = Path.Combine(temporary, "reset.cpp"); var prefixPath = Path.Combine(temporary, "prefix.cpp");
            var harness = Path.Combine(temporary, "harness.cpp"); var executable = Path.Combine(temporary, "test");
            File.WriteAllText(resetPath, reset.CxxCode); File.WriteAllText(prefixPath, translatedPrefix.CxxCode);
            File.WriteAllText(harness, $$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <cstdio>
                #include <cstring>
                extern "C" void func_8052990C(CpuContext*);
                extern "C" void init_prefix(CpuContext*);
                int main() {
                    Memory::Init(1024*1024);
                    constexpr uint32_t data=0x80010000, sp=0x800f0000, size={{layout.Symbol("Racedata.size")}};
                    constexpr uint32_t race=data+{{layout.Symbol("Racedata.race.offset")}};
                    constexpr uint32_t menu=data+{{layout.Symbol("Racedata.menu.offset")}};
                    constexpr uint32_t awards=data+{{layout.Symbol("Racedata.awards.offset")}};
                    constexpr uint32_t ghost=data+{{layout.Symbol("Racedata.ghosts.offset")}};
                    constexpr uint32_t settings={{layout.Symbol("Scenario.settings.offset")}};
                    constexpr uint32_t array={{layout.Symbol("Scenario.players.offset")}};
                    for (uint32_t prefix=0; prefix<2; prefix++) {
                        std::memset(Memory::GetPointer(data, size), 0x7e, size);
                        for (uint32_t i=0; i<16; i++) {
                            Memory::Write8(data-16+i, 0x55); Memory::Write8(data+size+i, 0x55);
                            Memory::Write8(sp-0x30-16+i, 0x55); Memory::Write8(sp+8+i, 0x55);
                        }
                        CpuContext ctx{}; ctx.gpr[1]=sp; ctx.gpr[3]=data;
                        for (uint32_t reg=14; reg<=31; reg++) ctx.gpr[reg]=0x12340000+reg;
                        CpuContextScope scope(&ctx);
                        if (prefix) init_prefix(&ctx); else func_8052990C(&ctx);
                        if (ctx.gpr[1] != sp-(prefix ? 0x30 : 0)) return 1;
                        if (!prefix) for (uint32_t reg=14; reg<=31; reg++)
                            if (ctx.gpr[reg] != 0x12340000+reg) return 2;
                        for (uint32_t scenario : {menu, awards}) {
                            for (uint32_t player=0; player<{{players}}; player++) {
                                uint32_t address=scenario+array+player*0xf0;
                                if (Memory::Read16(address+0xd8) != 0 || Memory::Read16(address+0xde) != 0 ||
                                    Memory::Read8(address+0xe0) != player+1 || Memory::Read8(address+0xe1) != player+1) {
                                    std::fprintf(stderr, "prefix %u scenario %x player %u reset mismatch\n", prefix, scenario, player);
                                    return 3;
                                }
                                if (Memory::Read16(address+0xda) != 0x7e7e || Memory::Read8(address+0xdc) != 0x7e ||
                                    Memory::Read8(address+0xe2) != 0x7e) return 4;
                                if (prefix && scenario==menu && (Memory::Read32(address+8) != 1 ||
                                    Memory::Read32(address+12) != 0 || Memory::Read32(address+16) != (player==0 ? 0u : 1u) ||
                                    Memory::Read32(address+0xcc) != 2)) return 5;
                            }
                            if (Memory::Read32(scenario+settings) != (prefix && scenario==menu ? 17u : 0x7e7e7e7eu)) return 6;
                            if (Memory::Read8(scenario+settings+0x24) != 0 || Memory::Read8(scenario+settings+0x25) != 3 ||
                                (Memory::Read32(scenario+settings+0x28)&7) != 0) return 7;
                        }
                        for (uint32_t address=ghost; address<data+size; address++)
                            if (Memory::Read8(address) != 0x7e) return 8;
                        for (uint32_t address=race; address<menu; address++)
                            if (Memory::Read8(address) != 0x7e) return 9;
                        for (uint32_t i=0; i<16; i++)
                            if (Memory::Read8(data-16+i)!=0x55 || Memory::Read8(data+size+i)!=0x55 ||
                                Memory::Read8(sp-0x30-16+i)!=0x55 || Memory::Read8(sp+8+i)!=0x55) return 10;
                    }
                    return 0;
                }
                """);
            var arguments = TranslatorCppTestHarness.BuildCompileArguments(root, temporary, new[] { resetPath, prefixPath },
                harness, executable, includeDataSections: false);
            using (var compile = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX") ?? "clang++", arguments)
                { RedirectStandardError = true })!)
            {
                var errors = compile.StandardError.ReadToEndAsync(); var done = compile.WaitForExit(45000);
                if (!done) compile.Kill(entireProcessTree: true);
                Assert.True(done, "Initializer compilation timed out");
                Assert.True(compile.ExitCode == 0, errors.GetAwaiter().GetResult());
            }
            using var run = Process.Start(new ProcessStartInfo(executable) { RedirectStandardError = true, UseShellExecute = false })!;
            var output = run.StandardError.ReadToEndAsync(); var finished = run.WaitForExit(10000);
            if (!finished) run.Kill(entireProcessTree: true);
            Assert.True(finished, "Initializer execution timed out");
            Assert.True(run.ExitCode == 0, $"Initializer returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }
}
