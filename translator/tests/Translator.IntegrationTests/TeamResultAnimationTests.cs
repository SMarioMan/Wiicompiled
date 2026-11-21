using System.Diagnostics;
using Xunit;

namespace Translator.Tests;

public class TeamResultAnimationTests
{
    [Fact]
    public void ExpandedRowsKeepResultsOpenUntilTheirScoresFinish()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "translator/Translator.sln"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException();
        var temp = Path.Combine(Path.GetTempPath(), "mkw-team-animation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var harness = Path.Combine(temp, "harness.cpp");
            var exe = Path.Combine(temp, "test");
            File.WriteAllText(harness, """
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include "mkw_guest_layout.h"
                #include "guest_function_imports.h"
                #include <limits>
                extern "C" void TeamResult_IsAnimating(CpuContext*);
                namespace MkwGuestLayout { const TeamResultLayout kTeamResults{0x174,64,20}; }
                bool originalPending=false;
                unsigned calls=0;
                void original(CpuContext* c){++calls;c->gpr[3]=originalPending;}

                int main(){
                    Memory::Init(32*1024*1024);
                    TranslatedFunctionInfo info{};info.address=0x807F7224;info.name="original team completion";
                    info.rawCpuInvoker=original;info.entryPoint=reinterpret_cast<void*>(original);TranslatedFunctionRegistry::Register(info);
                    constexpr uint32_t base=0x80020000,rows=base+0x174;
                    CpuContext c{};
                    auto pending=[&](){c.gpr[3]=base;TeamResult_IsAnimating(&c);return c.gpr[3];};
                    for(unsigned i=0;i<64;i++){
                        Memory::Write32(rows+i*20,100);
                        Memory::WriteFloat32(rows+i*20+4,100.0f);
                    }
                    if(pending())return 1;
                    for(unsigned i=6;i<64;i++){
                        Memory::WriteFloat32(rows+i*20+4,99.5f);
                        if(!pending())return 2;
                        Memory::WriteFloat32(rows+i*20+4,100.0f);
                        if(pending())return 3;
                    }
                    originalPending=true;if(!pending())return 4;originalPending=false;
                    Memory::Write32(rows+63*20,0xfffffffe);
                    Memory::WriteFloat32(rows+63*20+4,-2.5f);if(!pending())return 5;
                    Memory::WriteFloat32(rows+63*20+4,-1.5f);if(pending())return 6;
                    Memory::WriteFloat32(rows+63*20+4,std::numeric_limits<float>::quiet_NaN());
                    if(pending())return 7;
                    return calls==121?0:8;
                }
                """);
            var args = TranslatorCppTestHarness.BuildCompileArguments(root, temp,
                new[] { Path.Combine(root, "runtime/src/hle/team_results.cpp"), Path.Combine(root, "runtime/src/guest_region.cpp") }, harness, exe, includeDataSections: false);
            using (var compile = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX") ?? "clang++", args) { RedirectStandardError = true })!)
            {
                var errors = compile.StandardError.ReadToEndAsync();
                Assert.True(compile.WaitForExit(60000), "Compilation timed out");
                Assert.True(compile.ExitCode == 0, errors.GetAwaiter().GetResult());
            }
            using var run = Process.Start(new ProcessStartInfo(exe) { RedirectStandardError = true })!;
            var output = run.StandardError.ReadToEndAsync();
            Assert.True(run.WaitForExit(10000), "Execution timed out");
            Assert.True(run.ExitCode == 0, $"Animation fixture returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally { Directory.Delete(temp, true); }
    }
}
