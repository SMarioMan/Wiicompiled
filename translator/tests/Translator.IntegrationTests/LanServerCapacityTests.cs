using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class LanServerCapacityTests
{
    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(32)]
    [Trait("Requires", "USA game assets and Clang")]
    public void ActualServerSetupAndAdmissionCallbacksUseTheFullBuildCapacity(int players)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "translator/Translator.sln"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException();
        var manifest = Path.Combine(root, "projects/mkwii-ntsc-u/layouts/racedata.json");
        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        var layout = new GuestLayouts(document.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!, players);
        var image = new ProgramImageBuilder().Build(Path.Combine(root, "Assets/main.dol"), Path.Combine(root, "Assets/StaticR.rel"), 0x8050bf60);
        var map = FunctionMap.Load(Path.Combine(root, "projects/mkwii-ntsc-u/MAP.txt"));
        var translator = new FunctionTranslator(image, layoutRelocations: GuestLayoutRelocations.Read(manifest, players));
        var temp = Path.Combine(Path.GetTempPath(), "mkw-lan-server-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            uint[] entries = { 0x80655130, 0x80654290, 0x806542e0, 0x806532e4 };
            var sources = new List<string>();
            foreach (var pc in entries)
            {
                var source = Path.Combine(temp, $"{pc:X8}.cpp");
                File.WriteAllText(source, translator.Translate(pc,
                    new TranslationOptions(EnableLeafInlining: false, KnownFunctionEntryPoints: map.Addresses.ToHashSet())).CxxCode);
                sources.Add(source);
            }
            var harness = Path.Combine(temp, "harness.cpp");
            var executable = Path.Combine(temp, "test");
            File.WriteAllText(harness, $$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <cstdio>
                #include <cstdlib>
                extern "C" void func_80655130(CpuContext*);
                extern "C" void func_80654290(CpuContext*);
                extern "C" void func_806542E0(CpuContext*);
                extern "C" void func_806532E4(CpuContext*);
                constexpr uint32_t root=0x81010000, stack=0x81f00000, capacity={{players}},
                    current={{layout.Symbol("RKNetController.currentSub.offset")}},
                    subs={{layout.Symbol("RKNetController.subs.offset")}}, stride={{layout.Symbol("RKNetSub.size")}};
                uint32_t calls=0, expectedLocal=0, active=0, queries[capacity]{};
                bool stopAtSetup=false;
                void userData(CpuContext* c) {
                    const auto aid=c->gpr[3]; if(aid>=capacity) std::abort(); ++queries[aid];
                    const bool present=active && (aid==capacity-1 || aid<active-1);
                    Memory::Write32(c->gpr[4],present ? 0x01000000 : 0);
                    c->gpr[3]=present;
                }
                void setup(CpuContext* c) {
                    if(c->gpr[3]!=capacity || c->gpr[4]!=0x80654290 || c->gpr[5]!=root ||
                       c->gpr[6] || c->gpr[7] || c->gpr[8]!=0x806542e0 || c->gpr[10]!=root ||
                       Memory::Read8(c->gpr[9])!=expectedLocal) std::abort();
                    ++calls; if(stopAtSetup) throw 42; c->gpr[3]=1;
                }
                int main() {
                    Memory::Init(32*1024*1024);
                    TranslatedFunctionInfo info{}; info.address=0x800d18e4; info.name="DWC server setup argument fixture";
                    info.rawCpuInvoker=setup; info.entryPoint=reinterpret_cast<void*>(setup); TranslatedFunctionRegistry::Register(info);
                    info.address=0x800d4a28; info.rawCpuInvoker=userData; info.entryPoint=reinterpret_cast<void*>(userData);
                    TranslatedFunctionRegistry::Register(info);
                    CpuContext c{}; CpuContextScope scope(&c); c.gpr[1]=stack;
                    for(uint32_t r=14;r<32;r++) c.gpr[r]=0x12340000+r;
                    for(uint32_t sub=0;sub<2;sub++) for(expectedLocal=1;expectedLocal<=2;expectedLocal++) {
                        Memory::Write32(root+current,sub);
                        Memory::Write8(root+subs+sub*stride+0x20,expectedLocal);
                        c.gpr[3]=root; func_80655130(&c);
                        if(c.gpr[3]!=1 || c.gpr[1]!=stack) return 1;
                        for(uint32_t r=14;r<32;r++) if(c.gpr[r]!=0x12340000+r) return 2;
                    }
                    if(calls!=4) return 3;
                    for(active=0;active<=capacity;active++) for(uint32_t requested=1;requested<=2;requested++) {
                        for(auto& value:queries) value=0;
                        Memory::Write8(root,requested); c.gpr[3]=root; func_806542E0(&c);
                        if(c.gpr[3]!=(active+requested<=capacity)) return 4;
                        for(auto value:queries) if(value!=2) return 5;
                        if(c.gpr[1]!=stack) return 6;
                        for(uint32_t r=14;r<32;r++) if(c.gpr[r]!=0x12340000+r) return 7;
                    }
                    for(auto& value:queries) value=0;
                    func_80654290(&c);
                    for(auto value:queries) if(value!=1) return 8;
                    // The main loop has its own setup call instead of invoking the
                    // standalone setup method. Enter its exact setup block and
                    // stop at the SDK boundary before the infinite service loop.
                    stopAtSetup=true;
                    for(uint32_t sub=0;sub<2;sub++) for(expectedLocal=1;expectedLocal<=2;expectedLocal++) {
                        Memory::Write32(root+current,sub); Memory::Write8(root+subs+sub*stride+0x20,expectedLocal);
                        c.gpr[1]=stack; c.gpr[15]=root; c.gpr[24]=0; c.gpr[28]=c.gpr[29]=0x80650000;
                        try { func_806532E4(&c); return 9; } catch(int marker) { if(marker!=42) return 10; }
                    }
                    return calls==8 ? 0 : 11;
                }
                """);
            using (var compile = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX") ?? "clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root, temp, sources, harness, executable, includeDataSections: false)) { RedirectStandardError = true })!)
            {
                var errors = compile.StandardError.ReadToEndAsync();
                var done = compile.WaitForExit(45000);
                if (!done) compile.Kill(entireProcessTree: true);
                Assert.True(done, "Server setup compilation timed out");
                Assert.True(compile.ExitCode == 0, errors.GetAwaiter().GetResult());
            }
            using var run = Process.Start(new ProcessStartInfo(executable) { RedirectStandardError = true })!;
            var output = run.StandardError.ReadToEndAsync();
            var finished = run.WaitForExit(10000);
            if (!finished) run.Kill(entireProcessTree: true);
            Assert.True(finished, "Server setup execution timed out");
            Assert.True(run.ExitCode == 0, $"Server setup returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally { Directory.Delete(temp, recursive: true); }
    }
}
