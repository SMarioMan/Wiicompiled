using System.Diagnostics;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class DwcConnectionStorageTests
{
    [Theory]
    [InlineData(12, 32)]
    [InlineData(32, 32)]
    [InlineData(33, 64)]
    [InlineData(128, 128)]
    [Trait("Requires", "USA game assets and Clang")]
    public void OriginalDwcInitializesAndSearchesTheConnectionTables(int capacity, int slots)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "translator/Translator.sln")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException();
        var manifest = Path.Combine(root, "projects/mkwii-ntsc-u/layouts/racedata.json");
        var image = new ProgramImageBuilder().Build(Path.Combine(root, "Assets/main.dol"),
            Path.Combine(root, "Assets/StaticR.rel"), 0x8050bf60);
        var map = FunctionMap.Load(Path.Combine(root, "projects/mkwii-ntsc-u/MAP.txt"));
        var plan = GuestLayoutRelocations.Read(manifest, capacity);
        plan.ValidateImage(image);
        var translator = new FunctionTranslator(image, layoutRelocations: plan);
        var options = new TranslationOptions(EnableLeafInlining: false, KnownFunctionEntryPoints: map.Addresses.ToHashSet());
        var scratch = Directory.CreateTempSubdirectory("mkw-dwc-connections-");
        try
        {
            uint[] entries = [0x800d18e4, 0x800d211c, 0x800d21bc, 0x800d2414, 0x800d2860, 0x800d2930, 0x800d29d4, 0x800d29e8, 0x800d2a70, 0x800d2a84];
            var translated = Path.Combine(scratch.FullName, "connections.cpp");
            File.WriteAllText(translated, string.Join("\n", entries.Select(pc => translator.Translate(pc, options).CxxCode)));
            var header = Path.Combine(scratch.FullName, "layout.h");
            File.WriteAllText(header, plan.EmitNativeConstants());
            var harness = Path.Combine(scratch.FullName, "harness.cpp");
            File.WriteAllText(harness, $$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <cstdio>
                #include <cstdlib>
                #include <cstring>
                #define MKW_TRANSLATED_PLAYER_CAPACITY {{capacity}}
                #define MKW_TRANSLATED_GUEST_LAYOUT_HEADER "{{header}}"
                #include "{{root}}/runtime/game/guest_layout_values.cpp"
                {{string.Join("\n", entries.Select(pc => $"extern \"C\" void func_{pc:X8}(CpuContext*);"))}}
                constexpr uint32_t slots = {{slots}}, high = slots - 1;
                constexpr uint32_t stockBase = 0x802ecfe0, arenaPrefix = 0x90000800;
                constexpr uint32_t pointers = slots > 32 ? arenaPrefix : stockBase;
                constexpr uint32_t metadata = pointers + slots * 4, aids = metadata + slots * 8;
                constexpr uint32_t control = 0x81010000, stack = 0x81f00000;
                constexpr uint32_t handle = 0x81100000, nodes = 0x81200000, out = 0x81300000;
                #define CHECK(x) do { if (!(x)) { std::fprintf(stderr, "DWC connection line %d: %s\n", __LINE__, #x); std::abort(); } } while (false)
                unsigned clears, count = slots;
                struct Cleared {};
                void noOp(CpuContext*) {}
                void noError(CpuContext* c) { c->gpr[3] = 0; }
                void clear(CpuContext* c) {
                    CHECK(c->gpr[4] == 0);
                    const bool first = clears == 0, second = clears == 1;
                    CHECK(c->gpr[3] == (first ? pointers : second ? metadata : aids));
                    CHECK(c->gpr[5] == (first ? slots * 4 : second ? slots * 8 : slots));
                    std::memset(Memory::GetPointer(c->gpr[3], c->gpr[5]), 0, c->gpr[5]);
                    if (++clears == 2) throw Cleared{};
                }
                void getUserData(CpuContext* c) { CHECK(c->gpr[3] == handle); c->gpr[3] = metadata + high * 8; }
                void hostCount(CpuContext* c) { c->gpr[3] = count; }
                void hostByIndex(CpuContext* c) { CHECK(c->gpr[3] < count); c->gpr[3] = nodes + c->gpr[3] * 48; }
                void entry(uint32_t address, void (*function)(CpuContext*)) {
                    TranslatedFunctionInfo info{};
                    info.address = address;
                    info.name = "DWC connection fixture";
                    info.entryPoint = reinterpret_cast<void*>(function);
                    info.rawCpuInvoker = function;
                    TranslatedFunctionRegistry::Register(info);
                }
                int main() {
                    Memory::Config config;
                    config.regions.push_back({.name="MEM1", .baseAddress=0x80000000, .sizeBytes=32*1024*1024});
                    config.regions.push_back({.name="MEM2", .baseAddress=0x90000000, .sizeBytes=32*1024*1024});
                    Memory::Init(config);
                    CHECK(MkwGuestLayout::kDwcConnectionReserve == (slots > 32 ? 13 * slots : 0));
                    entry(0x80020a34, noOp);
                    entry(0x800ccfc8, noOp);
                    entry(0x800ccbb4, noError);
                    entry(0x80006038, clear);
                    entry(0x8010ac6c, getUserData);
                    entry(0x800e7d5c, noError);
                    entry(0x80020a80, noOp);
                    entry(0x800e7b88, hostCount);
                    entry(0x800e7dcc, hostByIndex);
                    CpuContext c{};
                    CpuContextScope scope(&c);
                    c.gpr[1] = stack;
                    c.gpr[13] = 0x80388880;
                    c.gpr[2] = 0x8038ac20;
                    Memory::Write32(0x80381f70, control);
                    Memory::Write32(control + 36, 3);
                    std::memset(Memory::GetPointer(pointers - 4, slots * 13 + 8), 0x5a, slots * 13 + 8);
                    if (slots > 32) Memory::Write32(stockBase, 0xdeadbeef);
                    try { func_800D18E4(&c); CHECK(false); } catch (const Cleared&) {}
                    CHECK(clears == 2 && Memory::Read32(pointers + high * 4) == 0);
                    CHECK(Memory::Read32(metadata + high * 8 + 4) == 0 && Memory::Read8(aids) == 0x5a);
                    c.gpr[1] = stack;
                    for (uint32_t i = 0; i < high; ++i) Memory::Write32(pointers + i * 4, handle);
                    func_800D2930(&c); CHECK(c.gpr[3] == high);
                    for (uint32_t i = 0; i < high; ++i) Memory::Write32(pointers + i * 4, 0);
                    Memory::Write32(pointers + high * 4, handle);
                    Memory::Write8(metadata + high * 8 + 1, high);
                    Memory::Write32(metadata + high * 8 + 4, 0x40123456);
                    c.gpr[3] = high; func_800D29D4(&c); CHECK(c.gpr[3] == pointers + high * 4);
                    c.gpr[3] = high; func_800D2A70(&c); CHECK(c.gpr[3] == metadata + high * 8);
                    c.gpr[3] = high; func_800D2860(&c); CHECK(c.gpr[3] == handle);
                    c.gpr[3] = high; func_800D2A84(&c); CHECK(c.gpr[3] == 1);
                    c.gpr[3] = slots; func_800D2860(&c); CHECK(c.gpr[3] == 0);
                    c.gpr[3] = 0x40123456; func_800D29E8(&c); CHECK(c.gpr[3] == pointers + high * 4);
                    for (uint32_t i = 0; i < slots; ++i) Memory::Write8(nodes + i * 48 + 22, high - i);
                    c.gpr[3] = out; func_800D211C(&c); CHECK(c.gpr[3] == slots && Memory::Read32(out) == aids);
                    for (uint32_t i = 0; i < slots; ++i) CHECK(Memory::Read8(aids + i) == high - i);
                    count = 1;
                    c.gpr[3] = out; func_800D211C(&c); CHECK(c.gpr[3] == 1);
                    CHECK(Memory::Read8(aids) == high && Memory::Read8(aids + high) == 0 && clears == 4);
                    CHECK(Memory::Read32(pointers - 4) == 0x5a5a5a5a && Memory::Read32(aids + slots) == 0x5a5a5a5a);
                    if (slots > 32) CHECK(Memory::Read32(stockBase) == 0xdeadbeef);
                    for (uint32_t i = 0; i < slots; ++i) {
                        Memory::Write8(nodes + 22, i);
                        func_800D2414(&c); CHECK(c.gpr[3] == (i < 32 ? 1u << i : 0));
                    }
                    for (uint32_t i = 0; i < slots; ++i) {
                        Memory::Write8(metadata + high * 8 + 1, i);
                        Memory::Write8(nodes + 22, i);
                        func_800D21BC(&c); CHECK(c.gpr[3] == (i < 32 ? 1u << i : 0));
                    }
                    count = slots;
                    if (slots > 32) {
                        for (uint32_t i = 0; i < count; ++i) Memory::Write8(nodes + i * 48 + 22, 64 + i % 32);
                        Memory::Write8(nodes + (slots - 1) * 48 + 22, 31);
                        func_800D2414(&c); CHECK(c.gpr[3] == 0x80000000u);
                    }
                }
                """);
            var executable = Path.Combine(scratch.FullName, "test");
            var arguments = TranslatorCppTestHarness.BuildCompileArguments(root, scratch.FullName,
                new[] { translated }, harness, executable, includeDataSections: false);
            Run(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX") ?? "clang++", arguments), 60000);
            Run(new ProcessStartInfo(executable), 10000);
        }
        finally { scratch.Delete(recursive: true); }
    }

    private static void Run(ProcessStartInfo start, int timeout)
    {
        start.RedirectStandardError = true;
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        var finished = process.WaitForExit(timeout);
        if (!finished) process.Kill(entireProcessTree: true);
        Assert.True(finished, $"{start.FileName} timed out");
        Assert.True(process.ExitCode == 0, errors.GetAwaiter().GetResult());
    }
}
