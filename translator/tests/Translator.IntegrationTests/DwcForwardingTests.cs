using System.Diagnostics;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class DwcForwardingTests
{
    [Theory]
    [InlineData(32, 31)]
    [InlineData(33, 32)]
    [InlineData(128, 127)]
    [Trait("Requires", "USA game assets and Clang")]
    public void OriginalTransportRelaysAndDeliversTheExactRecipient(int capacity, int recipient)
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
        var scratch = Directory.CreateTempSubdirectory("mkw-dwc-forwarding-");
        try
        {
            var translated = Path.Combine(scratch.FullName, "forwarding.cpp");
            File.WriteAllText(translated, translator.Translate(0x800e8d14, options).CxxCode + "\n" +
                translator.Translate(0x800e912c, options).CxxCode);
            var harness = Path.Combine(scratch.FullName, "harness.cpp");
            File.WriteAllText(harness, $$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <cstdio>
                #include <cstdlib>
                #include <cstring>
                #include <cstdint>
                extern "C" void func_800E8D14(CpuContext*);
                extern "C" void func_800E912C(CpuContext*);
                constexpr bool directAid = {{capacity}} > 32;
                constexpr uint32_t target = {{recipient}}, origin = 7, relay = 5;
                constexpr uint32_t stack = 0x81f00000, data = 0x81100000, buffer = data + 32;
                constexpr uint32_t packet = data + 128, node = data + 256, transport = 0x81200000;
                constexpr uint32_t callback = 0x81300000, payloadSize = 4;
                constexpr uint32_t relayConnection = 0x81400000, targetConnection = relayConnection + 4;
                constexpr uint32_t record = transport + origin * 64;
                constexpr uint32_t callbackOffset = (({{capacity}}+31)/32)*32*64+4;
                #define CHECK(x) do { if (!(x)) { std::fprintf(stderr, "DWC forwarding line %d: %s\n", __LINE__, #x); std::abort(); } } while (false)
                enum Role { Sender, Relay, Client };
                Role role = Sender;
                unsigned received, sends, local = origin, sender = origin;
                void noop(CpuContext*) {}
                void compare(CpuContext* c) {
                    const auto length = c->gpr[5];
                    c->gpr[3] = std::memcmp(Memory::GetPointer(c->gpr[3], length), Memory::GetPointer(c->gpr[4], length), length);
                }
                void copy(CpuContext* c) {
                    const auto length = c->gpr[5];
                    std::memcpy(Memory::GetPointer(c->gpr[3], length), Memory::GetPointer(c->gpr[4], length), length);
                }
                void allocate(CpuContext* c) { CHECK(c->gpr[4] == payloadSize + 8); c->gpr[3] = packet; }
                void isRelay(CpuContext* c) { c->gpr[3] = role == Relay; }
                void hostNode(CpuContext* c) { CHECK(c->gpr[3] == 0); c->gpr[3] = node; }
                void myAid(CpuContext* c) { c->gpr[3] = local; }
                void incomingAid(CpuContext* c) { c->gpr[3] = sender; }
                void connection(CpuContext* c) {
                    const auto aid = c->gpr[3];
                    if (role == Sender && aid == relay) c->gpr[3] = relayConnection;
                    else if (role == Relay && aid == target) c->gpr[3] = targetConnection;
                    else c->gpr[3] = 0;
                }
                void send(CpuContext* c) {
                    CHECK(c->gpr[3] == (role == Sender ? relayConnection : targetConnection));
                    CHECK(c->gpr[4] == packet && c->gpr[5] == payloadSize + 8 && c->gpr[6] == 0);
                    ++sends;
                }
                void deliver(CpuContext* c) {
                    CHECK(c->gpr[3] == origin && c->gpr[4] == buffer && c->gpr[5] == payloadSize);
                    CHECK(Memory::Read32(buffer) == 0x01234567);
                    ++received;
                }
                template<unsigned first> void save(CpuContext* c) {
                    for (unsigned reg = first; reg < 32; ++reg)
                        Memory::Write32(c->gpr[11] - (32-reg)*4, c->gpr[reg]);
                }
                template<unsigned first> void restore(CpuContext* c) {
                    for (unsigned reg = first; reg < 32; ++reg)
                        c->gpr[reg] = Memory::Read32(c->gpr[11] - (32-reg)*4);
                }
                void entry(uint32_t address, void (*function)(CpuContext*)) {
                    TranslatedFunctionInfo info{};
                    info.address = address;
                    info.name = "DWC forwarding fixture";
                    info.entryPoint = reinterpret_cast<void*>(function);
                    info.rawCpuInvoker = function;
                    TranslatedFunctionRegistry::Register(info);
                }
                int main() {
                    Memory::Init(32 * 1024 * 1024);
                    entry(0x80020a30, save<23>);
                    entry(0x80020a34, save<24>);
                    entry(0x80020a7c, restore<23>);
                    entry(0x80020a80, restore<24>);
                    entry(0x800d2860, connection);
                    entry(0x800d26c0, isRelay);
                    entry(0x800e7dcc, hostNode);
                    entry(0x800ccfc8, noop);
                    entry(0x800ccde0, noop);
                    entry(0x800ccd20, allocate);
                    entry(0x80012680, copy);
                    entry(0x800e937c, copy);
                    entry(0x8010aac4, send);
                    entry(0x800d28fc, incomingAid);
                    entry(0x8000e7b4, compare);
                    entry(0x800d20d0, myAid);
                    entry(0x800e8d14, func_800E8D14);
                    entry(callback, deliver);
                    CpuContext c{};
                    CpuContextScope scope(&c);
                    c.gpr[1] = stack;
                    c.gpr[13] = 0x80388880;
                    c.gpr[2] = 0x8038ac20;
                    Memory::Write32(c.gpr[13] - 26856, transport);
                    Memory::Write32(c.gpr[13] - 31940, 0x44574300);
                    Memory::Write32(transport + callbackOffset, callback);
                    Memory::Write32(record + 4, buffer);
                    Memory::Write32(record + 8, payloadSize);
                    Memory::Write8(node + 22, relay);
                    Memory::Write32(data, 0x01234567);
                    c.gpr[3] = origin; c.gpr[4] = target; c.gpr[5] = data;
                    c.gpr[6] = payloadSize; c.gpr[7] = 1;
                    func_800E8D14(&c);
                    CHECK(sends == 1 && Memory::Read8(packet + 3) == origin);
                    const uint32_t encoded = directAid ? target : uint32_t{1} << (target & 31);
                    CHECK(Memory::Read8(packet + 4) == (encoded & 255));
                    CHECK(Memory::Read8(packet + 7) == (encoded >> 24));
                    CHECK(Memory::Read32(packet + 8) == 0x01234567);
                    auto incoming = [&] {
                        c.gpr[3] = relayConnection; c.gpr[4] = packet; c.gpr[5] = payloadSize + 8;
                        func_800E912C(&c);
                        CHECK(c.gpr[1] == stack);
                    };
                    role = Relay; local = relay; incoming();
                    CHECK(sends == 2 && received == 0);
                    role = Client; sender = relay; local = target ^ 32u;
                    if (!directAid) local = target - 1;
                    incoming(); CHECK(received == 0 && sends == 2);
                    local = target; incoming(); CHECK(received == 1 && sends == 2);
                    role = Relay; local = target; sender = origin; incoming();
                    CHECK(received == 2 && sends == 2);
                    if (directAid) {
                        Memory::Write32(packet + 4, 0xff000000);
                        incoming(); CHECK(received == 2 && sends == 2);
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
