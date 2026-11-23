using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class DwcTransportStorageTests
{
    [Theory]
    [InlineData(12, 32)]
    [InlineData(32, 32)]
    [InlineData(33, 64)]
    [InlineData(65, 96)]
    [InlineData(97, 128)]
    [InlineData(128, 128)]
    [Trait("Requires", "USA game assets and Clang")]
    public void OriginalTransportUsesTheExpandedAllocationTailAndHighAidRecords(int players, int records)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "translator/Translator.sln")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException();
        var manifest = Path.Combine(root, "projects/mkwii-ntsc-u/layouts/racedata.json");
        using var definition = JsonDocument.Parse(File.ReadAllText(manifest));
        var layout = new GuestLayouts(definition.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!, players);
        var image = new ProgramImageBuilder().Build(Path.Combine(root, "Assets/main.dol"),
            Path.Combine(root, "Assets/StaticR.rel"), 0x8050bf60);
        var map = FunctionMap.Load(Path.Combine(root, "projects/mkwii-ntsc-u/MAP.txt"));
        var relocations = GuestLayoutRelocations.Read(manifest, players);
        var translator = new FunctionTranslator(image, layoutRelocations: relocations);
        var options = new TranslationOptions(EnableLeafInlining: false, KnownFunctionEntryPoints: map.Addresses.ToHashSet());
        var scratch = Directory.CreateTempSubdirectory("mkw-dwc-transport-");
        try
        {
            uint[] entries = [0x800d0cc8, 0x800e86b0, 0x800e86e0, 0x800e8700, 0x800e8728,
                0x800e8774, 0x800e8c4c, 0x800e8cc8];
            var translated = Path.Combine(scratch.FullName, "transport.cpp");
            File.WriteAllText(translated, string.Join("\n", entries.Select(pc => translator.Translate(pc, options).CxxCode)));
            var harness = Path.Combine(scratch.FullName, "harness.cpp");
            File.WriteAllText(harness, $$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <cstdio>
                #include <cstdlib>
                #include <cstring>
                {{string.Join("\n", entries.Select(pc => $"extern \"C\" void func_{pc:X8}(CpuContext*);"))}}
                constexpr uint32_t root = 0x81010000, stack = 0x81f00000;
                constexpr uint32_t transport = root + {{layout.Symbol("DWCControl.transport.offset")}};
                constexpr uint32_t records = {{records}}, stride = 64, end = transport + records * stride;
                constexpr uint32_t controlSize = {{layout.Symbol("DWCControl.size")}};
                constexpr uint32_t transportSize = {{layout.Symbol("DWCTransport.size")}};
                constexpr uint32_t controlGlobal = 0x80381f70, transportGlobal = 0x80381f98;
                constexpr uint32_t match = root + 0x360, matchSize = 0x8c0 + (records - 32) * 48 + (records / 32 - 1) * 8;
                static_assert(transport == match + matchSize);
                static_assert(controlSize == 0x360 + matchSize + records * 64 + 24);
                static_assert(transportSize == records * 64 + 24);
                #define CHECK(condition) do { if (!(condition)) { std::fprintf(stderr, "DWC line %d: %s\n", __LINE__, #condition); std::abort(); } } while (false)
                struct Initialized {};
                unsigned clearing, remoteAid, handlerCalls;
                void noOp(CpuContext*) {}
                void allocate(CpuContext* c) {
                    CHECK(c->gpr[3] == 4 && c->gpr[4] == controlSize);
                    c->gpr[3] = root;
                }
                void clear(CpuContext* c) {
                    constexpr uint32_t connections = records == 32 ? 0x802ecfe0 : 0x90000800;
                    constexpr uint32_t bases[] = {root, connections, connections + records * 4, transport};
                    constexpr uint32_t sizes[] = {controlSize, records * 4, records * 8, transportSize};
                    CHECK(clearing < 4 && c->gpr[3] == bases[clearing]);
                    const auto size = sizes[clearing];
                    CHECK(c->gpr[5] == size && c->gpr[4] == 0);
                    if (clearing == 0 || clearing == 3)
                        std::memset(Memory::GetPointer(c->gpr[3], size), 0, size);
                    ++clearing;
                }
                void initMatch(CpuContext* c) { CHECK(c->gpr[3] == match); }
                void initTransport(CpuContext* c) { func_800E8728(c); throw Initialized{}; }
                void aid(CpuContext* c) { c->gpr[3] = remoteAid; }
                void localAid(CpuContext* c) { c->gpr[3] = 0; }
                void now(CpuContext* c) { c->gpr[3] = 0x11223344; c->gpr[4] = 0x55667788; }
                void handle(CpuContext*) { ++handlerCalls; }
                template<unsigned first>
                void save(CpuContext* c) {
                    for (unsigned reg = first; reg < 32; ++reg)
                        Memory::Write32(c->gpr[11] - (32 - reg) * 4, c->gpr[reg]);
                }
                void restore(CpuContext* c) {
                    for (unsigned reg = 27; reg < 32; ++reg)
                        c->gpr[reg] = Memory::Read32(c->gpr[11] - (32 - reg) * 4);
                }
                void entry(uint32_t address, void (*function)(CpuContext*)) {
                    TranslatedFunctionInfo info{};
                    info.address = address;
                    info.name = "DWC transport fixture";
                    info.entryPoint = reinterpret_cast<void*>(function);
                    info.rawCpuInvoker = function;
                    TranslatedFunctionRegistry::Register(info);
                }
                int main() {
                    Memory::Init(32 * 1024 * 1024);
                    entry(0x800ccfc8, noOp);
                    entry(0x800ccb98, noOp);
                    entry(0x800cfeb8, noOp);
                    entry(0x800ce374, noOp);
                    entry(0x800d4b1c, initMatch);
                    entry(0x800e8728, initTransport);
                    entry(0x800ccd20, allocate);
                    entry(0x80006038, clear);
                    entry(0x80020a34, save<24>);
                    entry(0x80020a40, save<27>);
                    entry(0x80020a8c, restore);
                    entry(0x800d28fc, aid);
                    entry(0x800d20d0, localAid);
                    entry(0x801aacbc, now);
                    entry(0x800e8ed0, handle);
                    entry(0x800e912c, handle);
                    CpuContext c{};
                    CpuContextScope scope(&c);
                    c.gpr[1] = stack;
                    c.gpr[13] = 0x80388880;
                    c.gpr[2] = 0x8038ac20;
                    c.gpr[4] = root + 0x100;
                    std::memset(Memory::GetPointer(root - 4, controlSize + 68), 0x5a, controlSize + 68);
                    try { func_800D0CC8(&c); CHECK(false); } catch (const Initialized&) {}
                    CHECK(clearing == 4 && Memory::Read32(controlGlobal) == root);
                    c.gpr[1] = stack;
                    CHECK(Memory::Read32(transportGlobal) == transport);
                    CHECK(Memory::Read16(end + 16) == 1465 && Memory::Read32(end + 20) == 20000);
                    c.gpr[3] = 0x80005000;
                    func_800E86E0(&c);
                    c.gpr[3] = 7000;
                    func_800E8700(&c);
                    CHECK(Memory::Read32(end + 4) == 0x80005000 && Memory::Read32(end + 20) == 7000);
                    remoteAid = records - 1;
                    const auto record = transport + remoteAid * stride;
                    c.gpr[3] = remoteAid;
                    c.gpr[4] = 0x81230000;
                    c.gpr[5] = 736;
                    func_800E86B0(&c);
                    CHECK(Memory::Read32(record + 4) == 0x81230000 && Memory::Read32(record + 8) == 736);
                    c.gpr[3] = 1;
                    c.gpr[4] = 0x81230000;
                    c.gpr[5] = 8;
                    c.gpr[6] = 1;
                    func_800E8774(&c);
                    CHECK(handlerCalls == 1 && Memory::Read32(record + 56) == 0x11223344 &&
                        Memory::Read32(record + 60) == 0x55667788);
                    remoteAid = records;
                    c.gpr[3] = 1;
                    c.gpr[4] = 0x81230000;
                    c.gpr[5] = 8;
                    c.gpr[6] = 1;
                    func_800E8774(&c);
                    c.gpr[3] = records - 1;
                    func_800E8C4C(&c);
                    CHECK(Memory::Read32(record + 4) == 0x81230000 &&
                        Memory::Read32(record + 56) == 0x11223344);
                    CHECK(Memory::Read32(end + 4) == 0x80005000 &&
                        Memory::Read32(end + 20) == 7000 && handlerCalls == 2);
                    func_800E8CC8(&c);
                    CHECK(Memory::Read32(transportGlobal) == 0 && c.gpr[1] == stack);
                    CHECK(Memory::Read32(root - 4) == 0x5a5a5a5a &&
                        Memory::Read32(root + controlSize) == 0x5a5a5a5a &&
                        Memory::Read32(end + 56) == 0x5a5a5a5a &&
                        Memory::Read32(end + 60) == 0x5a5a5a5a);
                }
                """);
            var executable = Path.Combine(scratch.FullName, "test");
            var compileArgs = TranslatorCppTestHarness.BuildCompileArguments(root, scratch.FullName,
                new[] { translated }, harness, executable, includeDataSections: false);
            Run(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX") ?? "clang++", compileArgs), 45000);
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
