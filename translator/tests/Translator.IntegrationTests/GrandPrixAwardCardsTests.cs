using System.Diagnostics;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class GrandPrixAwardCardsTests
{
    [Fact]
    [Trait("Requires", "USA game assets and Clang")]
    public void ExpandedGrandPrixUsesTwelveValidCardsAndRetainsTheLocalRacer()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "translator/Translator.sln")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException();
        var project = Path.Combine(root, "projects/mkwii-ntsc-u");
        var layout = GuestLayoutRelocations.Read(Path.Combine(project, "layouts/racedata.json"), 128);
        var image = new ProgramImageBuilder().Build(Path.Combine(root, "Assets/main.dol"),
            Path.Combine(root, "Assets/StaticR.rel"), 0x8050bf60);
        var map = FunctionMap.Load(Path.Combine(project, "MAP.txt"));
        var translator = new FunctionTranslator(image, layoutRelocations: layout);
        var options = new TranslationOptions(EnableLeafInlining: false, KnownFunctionEntryPoints: map.Addresses.ToHashSet());
        var temp = Directory.CreateTempSubdirectory("mkw-award-cards-");
        try
        {
            var original = Path.Combine(temp.FullName, "original.cpp");
            File.WriteAllText(original, translator.Translate(0x80622b40, options).CxxCode);
            var harness = Path.Combine(temp.FullName, "harness.cpp");
            File.WriteAllText(harness, """
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include "mkw_guest_layout.h"
                #include <array>
                #include <cstdio>
                #include <cstdlib>
                extern "C" void func_80622B40(CpuContext*);
                extern "C" void AwardResults_PrepareExpandedGrandPrixItems(CpuContext*);
                constexpr uint32_t page = 0x81000000, data = 0x81010000, awards = 0xf180;
                constexpr uint32_t manager = 0x81040000, section = 0x81041000;
                constexpr uint32_t cards = page + 1532, stride = 372;
                namespace MkwGuestLayout {
                    const Layout kLayout{.playerCapacity=128, .playerStride=240, .scenarioPlayers=8,
                                         .racedataAwards=awards};
                }
                #define CHECK(x) do { if (!(x)) { std::fprintf(stderr, "Award cards line %d: %s\n", __LINE__, #x); std::abort(); } } while (false)
                std::array<uint32_t, 12> selected{};
                unsigned fills, outside, oldCalls;
                void populate(CpuContext* c) {
                    ++fills;
                    if (c->gpr[3] < cards || c->gpr[3] >= cards + stride * selected.size()) {
                        ++outside;
                        return;
                    }
                    const auto offset = c->gpr[3] - cards;
                    CHECK(offset % stride == 0 && c->gpr[5] == 0 && c->gpr[6] == 0);
                    selected[offset / stride] = c->gpr[4];
                }
                void delegate(CpuContext*) { ++oldCalls; }
                void entry(uint32_t pc, void (*f)(CpuContext*)) {
                    TranslatedFunctionInfo info{};
                    info.address=pc; info.name="Grand Prix awards fixture";
                    info.rawCpuInvoker=f; info.entryPoint=reinterpret_cast<void*>(f);
                    TranslatedFunctionRegistry::Register(info);
                }
                uint32_t player(unsigned index) { return data + awards + 8 + 240*index; }
                int main() {
                    Memory::Init(32*1024*1024);
                    CHECK(MKW_GADDR(805BBBD0) == 0x8062194c);
                    Memory::Write32(MKW_GADDR(809BD728), data);
                    Memory::Write32(MKW_GADDR(809C1E38), manager);
                    Memory::Write32(manager, section);
                    Memory::Write32(section, 0x35);
                    Memory::Write8(page + 5998, 1);
                    Memory::Write8(page + 5999, 12);
                    entry(0x8062194c, populate);
                    entry(0x80622b40, delegate);
                    CpuContext c{};
                    c.gpr[1] = 0x81f00000;
                    c.gpr[3] = page;
                    CpuContextScope scope(&c);
                    for (unsigned i=0; i<128; ++i) {
                        Memory::Write32(player(i)+0x10, i==0 ? 0 : 1);
                        Memory::Write8(player(i)+0xe0, 128-i);
                    }
                    func_80622B40(&c);
                    CHECK(outside == 12);
                    fills = outside = 0;
                    c.gpr[3] = page;
                    AwardResults_PrepareExpandedGrandPrixItems(&c);
                    CHECK(!oldCalls && !outside && fills == 12);
                    for (unsigned i=0; i<11; ++i) CHECK(selected[i] == 127-i);
                    CHECK(selected[11] == 0 && Memory::Read8(player(0)+0xe0)==128);

                    for (unsigned i=1; i<127; ++i) Memory::Write32(player(i)+0x10, 5);
                    fills = 0;
                    AwardResults_PrepareExpandedGrandPrixItems(&c);
                    CHECK(fills == 2 && selected[0] == 127 && selected[1] == 0);
                    CHECK(Memory::Read8(page + 5999) == 2);
                    for (unsigned i=2; i<12; ++i) CHECK(Memory::Read8(cards + i*stride + 128) == 1);

                    Memory::Write32(section, 0x36);
                    AwardResults_PrepareExpandedGrandPrixItems(&c);
                    CHECK(oldCalls == 1);
                }
                """);
            var executable = Path.Combine(temp.FullName, "test");
            var sources = new[] { original, Path.Combine(root, "runtime/src/hle/award_results.cpp"),
                Path.Combine(root, "runtime/src/guest_region.cpp") };
            var args = TranslatorCppTestHarness.BuildCompileArguments(root, temp.FullName, sources,
                harness, executable, includeDataSections: false);
            var support = Path.Combine(temp.FullName, "translator_test_support.cpp");
            File.WriteAllText(support, File.ReadAllText(support).Replace("const uint8_t kMkwGuestRegion = 0;",
                "const uint8_t kMkwGuestRegion = 1;", StringComparison.Ordinal));
            Run(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX") ?? "clang++", args), 60000);
            Run(new ProcessStartInfo(executable), 10000);
        }
        finally { temp.Delete(recursive: true); }
    }

    private static void Run(ProcessStartInfo start, int timeout)
    {
        start.RedirectStandardError = true;
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        var done = process.WaitForExit(timeout);
        if (!done) process.Kill(entireProcessTree: true);
        Assert.True(done, $"{start.FileName} timed out");
        Assert.True(process.ExitCode == 0, errors.GetAwaiter().GetResult());
    }
}
