using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Analysis;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class DwcMatchStorageTests
{
    [Fact]
    [Trait("Requires", "USA game assets")]
    public void MatchTailConsumersUseTheVerifiedLayout()
    {
        var (_, manifest, image, map) = OpenGame();
        using var definition = JsonDocument.Parse(File.ReadAllText(manifest));
        var types = definition.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!;
        var plan = GuestLayoutRelocations.Read(manifest, 128);
        plan.ValidateImage(image);
        var globals = new Dictionary<uint, string> { [0x80381f70] = "DWCControl", [0x80381f8c] = "DWCMatch" };
        var audit = new GuestLayoutAudit(types, 12, 128, globals, 0x80388880, 0x8038ac20, image);
        var stock = new GuestLayouts(types, 12);
        var expanded = new GuestLayouts(types, 128);
        var original = new FunctionTranslator(image);
        var options = Options(map);
        var declared = definition.RootElement.GetProperty("References").EnumerateArray()
            .Where(site => site.GetProperty("Symbol").GetString()!.Contains("DWCMatch.tail.offset") ||
                           site.GetProperty("Symbol").GetString() == "DWCControl.transport.offset")
            .ToDictionary(site => (Convert.ToUInt32(site.GetProperty("Instruction").GetString(), 16),
                site.GetProperty("Node").GetInt32(), site.GetProperty("Operand").GetString()));
        var covered = new HashSet<(uint, int, string?)>();
        foreach (var pc in map.Addresses.Distinct().Where(pc => pc is >= 0x800cc000 and < 0x800e9374))
        {
            var proof = audit.Analyze(original.Discover(pc, options), pc == 0x800d4b1c ? "DWCMatch" : null);
            foreach (var candidate in proof.Candidates)
            {
                var site = candidate.Reference;
                var key = (site.Instruction, site.Node, site.Operand);
                Assert.True(declared.TryGetValue(key, out var relocation), $"Missing match consumer {site.Instruction:X8} in {pc:X8}");
                var symbol = relocation.GetProperty("Symbol").GetString()!;
                var offset = relocation.TryGetProperty("Addend", out var addend) ? addend.GetInt32() : 0;
                Assert.Equal(candidate.Before, stock.Symbol(symbol) + offset);
                Assert.Equal(candidate.After, expanded.Symbol(symbol) + offset);
                covered.Add(key);
            }
        }
        Assert.Equal(1414, covered.Count);
        Assert.Equal(declared.Count, covered.Count);
    }

    [Theory]
    [InlineData(12, 32)]
    [InlineData(32, 32)]
    [InlineData(33, 64)]
    [InlineData(128, 128)]
    [Trait("Requires", "USA game assets and Clang")]
    public void OriginalMatchmakingInitializesAndMaintainsTheLastNode(int capacity, int nodes)
    {
        var (root, manifest, image, map) = OpenGame();
        var translator = new FunctionTranslator(image, layoutRelocations: GuestLayoutRelocations.Read(manifest, capacity));
        var options = Options(map);
        uint[] entries = [0x800d4b1c, 0x800e7b74, 0x800e7b94, 0x800e7dcc, 0x800e7df4, 0x800e7e40, 0x800e7e8c];
        var scratch = Directory.CreateTempSubdirectory("mkw-dwc-match-");
        try
        {
            var translated = Path.Combine(scratch.FullName, "match.cpp");
            File.WriteAllText(translated, string.Join("\n", entries.Select(pc => translator.Translate(pc, options).CxxCode)));
            var harness = Path.Combine(scratch.FullName, "harness.cpp");
            File.WriteAllText(harness, $$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <cstdio>
                #include <cstdlib>
                #include <cstring>
                {{string.Join("\n", entries.Select(pc => $"extern \"C\" void func_{pc:X8}(CpuContext*);"))}}
                constexpr uint32_t match = 0x81010000, stack = 0x81f00000, matchGlobal = 0x80381f8c;
                constexpr uint32_t nodes = {{nodes}}, peers = match + 0x38, tail = peers + nodes * 48, end = tail + 0x288;
                constexpr uint32_t count = match + 0x30, shift = (nodes - 32) * 48;
                #define CHECK(x) do { if (!(x)) { std::fprintf(stderr, "DWC match line %d: %s\n", __LINE__, #x); std::abort(); } } while (false)
                unsigned clears;
                void clear(CpuContext* ctx) {
                    constexpr uint32_t addresses[] = {match + 0x794 + shift, count, tail, 0x802ed180, count};
                    constexpr uint32_t sizes[] = {64, 8 + nodes * 48, 48, 1848, 8 + nodes * 48};
                    CHECK(clears < 5 && ctx->gpr[3] == addresses[clears] && ctx->gpr[5] == sizes[clears]);
                    CHECK(ctx->gpr[4] == 0);
                    std::memset(Memory::GetPointer(ctx->gpr[3], ctx->gpr[5]), 0, ctx->gpr[5]);
                    ++clears;
                }
                void save(CpuContext* ctx) {
                    for (unsigned reg = 23; reg < 32; ++reg)
                        Memory::Write32(ctx->gpr[11] - (32 - reg) * 4, ctx->gpr[reg]);
                }
                void restore(CpuContext* ctx) {
                    for (unsigned reg = 23; reg < 32; ++reg)
                        ctx->gpr[reg] = Memory::Read32(ctx->gpr[11] - (32 - reg) * 4);
                }
                void now(CpuContext* ctx) { ctx->gpr[3] = 0; ctx->gpr[4] = 0; }
                void noOp(CpuContext*) {}
                void entry(uint32_t address, void (*function)(CpuContext*)) {
                    TranslatedFunctionInfo info{};
                    info.address = address;
                    info.name = "DWC matchmaking fixture";
                    info.entryPoint = reinterpret_cast<void*>(function);
                    info.rawCpuInvoker = function;
                    TranslatedFunctionRegistry::Register(info);
                }
                int main() {
                    Memory::Init(32 * 1024 * 1024);
                    entry(0x80006038, clear);
                    entry(0x80020a30, save);
                    entry(0x80020a7c, restore);
                    entry(0x801aacbc, now);
                    entry(0x800ccfc8, noOp);
                    entry(0x800dadb4, noOp);
                    CpuContext ctx{};
                    CpuContextScope scope(&ctx);
                    ctx.gpr[1] = stack;
                    ctx.gpr[13] = 0x80388880;
                    ctx.gpr[3] = match;
                    ctx.gpr[7] = 0x12345678;
                    std::memset(Memory::GetPointer(match - 4, end - match + 8), 0x5a, end - match + 8);
                    Memory::Write32(match + 0x71c + shift, 0);
                    func_800D4B1C(&ctx);
                    CHECK(ctx.gpr[1] == stack && clears == 4 && Memory::Read32(matchGlobal) == match);
                    CHECK(Memory::Read32(count) == 0 && Memory::Read32(tail - 4) == 0);
                    CHECK(Memory::Read32(match + 0x784 + shift) == 0x12345678);
                    CHECK(Memory::Read8(match + 0x8a1 + shift) == 1);
                    for (uint32_t slot = 0; slot < nodes; ++slot) {
                        const auto address = peers + slot * 48;
                        Memory::Write32(address, 0x12340000 + slot);
                        Memory::Write8(address + 22, slot);
                        Memory::Write32(address + 44, 0x56780000 + slot);
                    }
                    Memory::Write32(count, nodes - 1);
                    func_800E7E8C(&ctx); CHECK(ctx.gpr[3] == nodes - 1);
                    Memory::Write32(count, nodes);
                    func_800E7E8C(&ctx); CHECK(ctx.gpr[3] == 0xff);
                    ctx.gpr[3] = nodes - 1; func_800E7DCC(&ctx); CHECK(ctx.gpr[3] == tail - 48);
                    ctx.gpr[3] = 0x12340000 + nodes - 1; func_800E7DF4(&ctx); CHECK(ctx.gpr[3] == tail - 48);
                    ctx.gpr[3] = nodes - 1; func_800E7E40(&ctx); CHECK(ctx.gpr[3] == tail - 48);
                    ctx.gpr[3] = nodes - 2; func_800E7B94(&ctx); CHECK(ctx.gpr[3] == 1);
                    CHECK(Memory::Read32(count) == nodes - 1 && Memory::Read8(tail - 96 + 22) == nodes - 1);
                    CHECK(Memory::Read32(tail - 96 + 44) == 0x56780000 + nodes - 1);
                    func_800E7E8C(&ctx); CHECK(ctx.gpr[3] == nodes - 2);
                    Memory::Write32(tail, 0x12345678);
                    func_800E7B74(&ctx);
                    CHECK(clears == 5 && Memory::Read32(count) == 0 && Memory::Read32(tail - 4) == 0);
                    CHECK(Memory::Read32(tail) == 0x12345678 && Memory::Read32(match + 0x784 + shift) == 0x12345678);
                    CHECK(Memory::Read32(match - 4) == 0x5a5a5a5a && Memory::Read32(end) == 0x5a5a5a5a);
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

    [Theory]
    [InlineData(12, 32)]
    [InlineData(33, 64)]
    [InlineData(128, 128)]
    [Trait("Requires", "USA game assets and Clang")]
    public void OriginalSynCancellationKeepsEveryUnconfirmedPidInsideItsStackFrame(int capacity, int nodes)
    {
        var (root, manifest, image, map) = OpenGame();
        var plan = GuestLayoutRelocations.Read(manifest, capacity);
        var translator = new FunctionTranslator(image, layoutRelocations: plan);
        var scratch = Directory.CreateTempSubdirectory("mkw-dwc-syn-cancel-");
        try
        {
            var translated = Path.Combine(scratch.FullName, "cancel.cpp");
            File.WriteAllText(translated, translator.Translate(0x800e1200, Options(map)).CxxCode);
            var harness = Path.Combine(scratch.FullName, "harness.cpp");
            File.WriteAllText(harness, $$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <cstdio>
                #include <cstdlib>
                #include <cstring>
                extern "C" void func_800E1200(CpuContext*);
                constexpr uint32_t match = 0x81010000, stack = 0x81f00000, nodes = {{nodes}};
                constexpr uint32_t frame = 8 + 2 * nodes * 4 + 24, unconfirmed = stack - frame + 8 + nodes * 4;
                constexpr uint32_t shift = (nodes - 32) * 48;
                #define CHECK(x) do { if (!(x)) { std::fprintf(stderr, "DWC cancel line %d: %s\n", __LINE__, #x); std::abort(); } } while (false)
                uint32_t closed = 0, sent = 0;
                void sendCancel(CpuContext* c) {
                    CHECK(c->gpr[3] == 0x10 && c->gpr[4] == 0x12340001u && c->gpr[8] == 1);
                    CHECK(Memory::Read32(c->gpr[7]) == 0x02003412u);
                    ++sent;
                }
                void sendResult(CpuContext* c) { c->gpr[3] = 0; }
                void closePeer(CpuContext* c) { CHECK(c->gpr[3] == ++closed); }
                void save(CpuContext* c) {
                    CHECK(c->gpr[11] == stack);
                    for (unsigned i = 27; i < 32; ++i) Memory::Write32(stack - (32 - i) * 4, c->gpr[i]);
                }
                void restore(CpuContext* c) {
                    CHECK(c->gpr[11] == stack);
                    for (unsigned i = 27; i < 32; ++i) c->gpr[i] = Memory::Read32(stack - (32 - i) * 4);
                }
                void entry(uint32_t pc, void (*function)(CpuContext*)) {
                    TranslatedFunctionInfo info{};
                    info.address = pc; info.name = "DWC SYN cancellation fixture";
                    info.entryPoint = reinterpret_cast<void*>(function); info.rawCpuInvoker = function;
                    TranslatedFunctionRegistry::Register(info);
                }
                int main() {
                    Memory::Init(32 * 1024 * 1024);
                    entry(0x80020a40, save);
                    entry(0x80020a8c, restore);
                    entry(0x800d1f60, closePeer);
                    entry(0x800dbc98, sendCancel);
                    entry(0x800e270c, sendResult);
                    entry(0x800e28f0, sendResult);
                    Memory::Write32(0x80381f8c, match);
                    Memory::Write32(match + 0x30, nodes);
                    for (unsigned i = 0; i < nodes; ++i) {
                        Memory::Write32(match + 0x38 + i * 48, 0x12340000 + i);
                        Memory::Write8(match + 0x38 + i * 48 + 22, i);
                    }
                    std::memset(Memory::GetPointer(stack - frame - 4, frame + 16), 0xa5, frame + 16);
                    CpuContext c{};
                    CpuContextScope scope(&c);
                    c.gpr[1] = stack; c.gpr[13] = 0x80388880;
                    for (unsigned i = 27; i < 32; ++i) c.gpr[i] = 0x87654300 + i;
                    func_800E1200(&c);
                    CHECK(c.gpr[1] == stack && c.gpr[3] == 1 && closed == nodes - 1);
                    for (unsigned i = 27; i < 32; ++i) CHECK(c.gpr[i] == 0x87654300 + i);
                    CHECK(Memory::Read32(stack - frame - 4) == 0xa5a5a5a5 &&
                          Memory::Read32(stack + 8) == 0xa5a5a5a5 &&
                          Memory::Read32(unconfirmed - 4) == 0xa5a5a5a5 &&
                          Memory::Read32(unconfirmed + (nodes - 1) * 4) == 0xa5a5a5a5);
                    CHECK(Memory::Read8(match + 0x721 + shift) == 0);
                    if (nodes > 32) {
                        // Only the high AID acknowledged. Send it the missed
                        // low peer's PID, then close that low peer.
                        closed = 0;
                        Memory::Write32(match + 0x30, 3);
                        Memory::Write32(match + 0x71c + shift, 17);
                        Memory::Write8(match + 0x38 + 48 + 22, nodes - 1);
                        Memory::Write8(match + 0x38 + 2 * 48 + 22, 1);
                        Memory::Write32(match + 0x8bc + shift + (nodes / 32 - 1) * 4, 0x80000000u);
                        c.gpr[3] = 0;
                        func_800E1200(&c);
                        CHECK(c.gpr[1] == stack && c.gpr[3] == 1 && closed == 1 && sent == 1);
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

    [Theory]
    [InlineData(33, 64)]
    [InlineData(128, 128)]
    [Trait("Requires", "USA game assets and Clang")]
    public void OriginalHostSynchronizationTracksHighAcksInBothPhases(int capacity, int nodes)
    {
        var (root, manifest, image, map) = OpenGame();
        var translator = new FunctionTranslator(image, layoutRelocations: GuestLayoutRelocations.Read(manifest, capacity));
        var scratch = Directory.CreateTempSubdirectory("mkw-dwc-syn-acks-");
        try
        {
            var translated = Path.Combine(scratch.FullName, "syn.cpp");
            File.WriteAllText(translated, translator.Translate(0x800d9648, Options(map)).CxxCode + "\n" +
                translator.Translate(0x800e1c0c, Options(map)).CxxCode);
            var harness = Path.Combine(scratch.FullName, "harness.cpp");
            File.WriteAllText(harness, $$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <cstdio>
                #include <cstdlib>
                extern "C" void func_800D9648(CpuContext*);
                extern "C" void func_800E1C0C(CpuContext*);
                constexpr uint32_t match=0x81010000, packet=0x81020000, stack=0x81f00000, nodes={{nodes}};
                constexpr uint32_t tail=match+0x38+nodes*48, highAid=nodes==128?64:32;
                constexpr uint32_t timer=tail+0x284, low=tail+0x118;
                #define CHECK(x) do { if (!(x)) { std::fprintf(stderr, "DWC SYN line %d: %s\n", __LINE__, #x); std::abort(); } } while (false)
                struct Complete {};
                void onComplete(CpuContext*) { throw Complete{}; }
                void noOp(CpuContext*) {}
                void cancelComplete(CpuContext* c) { CHECK(c->gpr[3]==15); throw Complete{}; }
                void matchState(CpuContext* c) { c->gpr[3]=6; }
                void zeroTime(CpuContext* c) { c->gpr[3]=0; c->gpr[4]=0; }
                void entry(uint32_t pc, void (*function)(CpuContext*)) {
                    TranslatedFunctionInfo info{};
                    info.address=pc; info.name="DWC SYN acknowledgement fixture";
                    info.entryPoint=reinterpret_cast<void*>(function); info.rawCpuInvoker=function;
                    TranslatedFunctionRegistry::Register(info);
                }
                bool acknowledge(CpuContext& c, uint32_t aid) {
                    c.gpr[1]=stack; c.gpr[3]=aid; c.gpr[4]=2; c.gpr[5]=packet;
                    try { func_800D9648(&c); }
                    catch (const Complete&) { return true; }
                    CHECK(c.gpr[1]==stack);
                    return false;
                }
                bool acknowledgeCancel(CpuContext& c, uint32_t pid) {
                    c.gpr[1]=stack; c.gpr[3]=pid; c.gpr[4]=14; c.gpr[5]=packet;
                    try { func_800E1C0C(&c); }
                    catch (const Complete&) { return true; }
                    CHECK(c.gpr[1]==stack);
                    return false;
                }
                int main() {
                    Memory::Init(32*1024*1024);
                    entry(0x80020a3c,noOp); entry(0x80020a88,noOp);
                    entry(0x800ccfc8,noOp); entry(0x800cde38,onComplete);
                    entry(0x80020a38,noOp); entry(0x80020a84,noOp);
                    entry(0x800d262c,matchState); entry(0x801aacbc,zeroTime);
                    entry(0x80020aa4,zeroTime); entry(0x800dbc98,cancelComplete);
                    Memory::Write32(0x800000f8,243000000);
                    Memory::Write32(0x80381f8c,match);
                    Memory::Write32(tail+0xe4,17);
                    Memory::Write32(match+0x30,3);
                    Memory::Write8(match+0x38+48+22,highAid);
                    Memory::Write8(match+0x38+2*48+22,1);
                    Memory::Write8(tail+22,nodes-1);
                    Memory::Write32(timer,0x11223344);
                    CpuContext c{};
                    CpuContextScope scope(&c);
                    c.gpr[13]=0x80388880;
                    CHECK(!acknowledge(c,highAid));
                    CHECK(Memory::Read32(low)==0 && Memory::Read32(timer+highAid/32*4)==1);
                    CHECK(!acknowledge(c,1) && Memory::Read32(low)==2);
                    CHECK(!acknowledge(c,highAid));
                    CHECK(acknowledge(c,nodes-1));
                    CHECK(Memory::Read32(timer)==0x11223344);
                    CHECK(Memory::Read32(timer+(nodes/32-1)*4)&0x80000000u);
                    // The cancellation phase has its own low word and waits
                    // only for the established peers, excluding the joiner.
                    for (unsigned bank=1; bank<nodes/32; ++bank) Memory::Write32(timer+bank*4,0);
                    Memory::Write32(tail+0xe4,15);
                    Memory::Write32(match+0x38+48,0x12001);
                    Memory::Write32(match+0x38+2*48,0x12002);
                    CHECK(!acknowledgeCancel(c,0x12001));
                    CHECK(Memory::Read32(low+4)==0 && Memory::Read32(timer+highAid/32*4)==1);
                    CHECK(acknowledgeCancel(c,0x12002));
                    CHECK(Memory::Read32(low+4)==2 && Memory::Read32(timer)==0x11223344);
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

    private static (string Root, string Manifest, ProgramImage Image, FunctionMap Map) OpenGame()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "translator/Translator.sln")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException();
        return (root, Path.Combine(root, "projects/mkwii-ntsc-u/layouts/racedata.json"),
            new ProgramImageBuilder().Build(Path.Combine(root, "Assets/main.dol"), Path.Combine(root, "Assets/StaticR.rel"), 0x8050bf60),
            FunctionMap.Load(Path.Combine(root, "projects/mkwii-ntsc-u/MAP.txt")));
    }

    private static TranslationOptions Options(FunctionMap map) =>
        new(EnableLeafInlining: false, KnownFunctionEntryPoints: map.Addresses.ToHashSet());

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
