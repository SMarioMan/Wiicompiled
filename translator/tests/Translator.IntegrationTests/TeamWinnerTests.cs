using System.Diagnostics;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class TeamWinnerTests
{
    [Fact]
    [Trait("Requires", "USA game assets and Clang")]
    public void ExpandedWinnerUsesFullTotalsAndPreservesRetailTies()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "translator/Translator.sln"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException();
        var temp = Path.Combine(Path.GetTempPath(), "mkw-team-winner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var harness = Path.Combine(temp, "harness.cpp");
            var exe = Path.Combine(temp, "test");
            const uint winner = 0x80529160;
            var layout = GuestLayoutRelocations.Read(Path.Combine(root, "projects/mkwii-ntsc-u/layouts/racedata.json"), 128);
            var image = new ProgramImageBuilder().Build(Path.Combine(root, "Assets/main.dol"), Path.Combine(root, "Assets/StaticR.rel"), 0x8050bf60);
            var original = Path.Combine(temp, "original.cpp");
            File.WriteAllText(original, new FunctionTranslator(image, layoutRelocations: layout).Translate(winner).CxxCode);
            File.WriteAllText(harness, """
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include "mkw_guest_layout.h"
                extern "C" void Scenario_ExpandedTeamWinner(CpuContext*);
                extern "C" void func_80529160(CpuContext*);
                namespace MkwGuestLayout { const Layout kLayout{.playerCapacity=128,.playerStride=0xf0,.scenarioPlayers=8,.racedataRace=0x20}; }
                int main(){
                    Memory::Init(32*1024*1024);
                    constexpr uint32_t data=0x81000000, scenario=data+0x20;
                    if(MKW_GADDR(809BD728)!=0x809b8f68 || MKW_GADDR(8052DCA8)!=0x80529160)return 1;
                    Memory::Write32(MKW_GADDR(809BD728),data);
                    TranslatedFunctionInfo info{};
                    info.address=0x80529160; info.name="retail team winner";
                    info.rawCpuInvoker=func_80529160; info.entryPoint=reinterpret_cast<void*>(func_80529160);
                    TranslatedFunctionRegistry::Register(info);
                    CpuContext c{};c.gpr[1]=0x81f00000;CpuContextScope scope(&c);
                    auto result=[&](void(*fn)(CpuContext*)) {c.gpr[3]=scenario;fn(&c);return c.gpr[3];};
                    auto scores=[&](unsigned count, unsigned even, unsigned odd){
                        Memory::Write8(scenario+4,count);
                        for(unsigned i=0;i<count;i++){
                            auto player=scenario+8+i*0xf0;
                            Memory::Write32(player+0xcc,i%2);
                            Memory::Write16(player+0xd8,i%2?odd:even);
                        }
                    };
                    scores(12,10,10);if(result(Scenario_ExpandedTeamWinner)!=0)return 2;
                    scores(12,10,11);if(result(Scenario_ExpandedTeamWinner)!=1)return 3;
                    scores(128,1100,1000);
                    if(result(func_80529160)!=1 || result(Scenario_ExpandedTeamWinner)!=0)return 4;
                    scores(128,1000,1100);
                    if(result(func_80529160)!=0 || result(Scenario_ExpandedTeamWinner)!=1)return 5;
                    scores(128,1100,1100);if(result(Scenario_ExpandedTeamWinner)!=0)return 6;
                    for(unsigned count : {13u,24u,25u,26u,48u,52u,64u,100u,116u,118u,127u,128u}){
                        scores(count,3000,4000);if(result(Scenario_ExpandedTeamWinner)!=1)return 7;
                        scores(count,4000,3000);if(result(Scenario_ExpandedTeamWinner)!=0)return 8;
                    }
                    return 0;
                }
                """);
            var args = TranslatorCppTestHarness.BuildCompileArguments(root, temp,
                new[] { original, Path.Combine(root, "runtime/src/hle/scenario_scores.cpp"), Path.Combine(root, "runtime/src/guest_region.cpp") },
                harness, exe, includeDataSections: false);
            var support = Path.Combine(temp, "translator_test_support.cpp");
            File.WriteAllText(support, File.ReadAllText(support).Replace("const uint8_t kMkwGuestRegion = 0;", "const uint8_t kMkwGuestRegion = 1;", StringComparison.Ordinal));
            using (var compile = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX") ?? "clang++", args) { RedirectStandardError = true })!)
            {
                var errors = compile.StandardError.ReadToEndAsync();
                Assert.True(compile.WaitForExit(60000), "Compilation timed out");
                Assert.True(compile.ExitCode == 0, errors.GetAwaiter().GetResult());
            }
            using var run = Process.Start(new ProcessStartInfo(exe) { RedirectStandardError = true })!;
            var output = run.StandardError.ReadToEndAsync();
            Assert.True(run.WaitForExit(10000), "Execution timed out");
            Assert.True(run.ExitCode == 0, $"Team winner fixture returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally { Directory.Delete(temp, true); }
    }
}
