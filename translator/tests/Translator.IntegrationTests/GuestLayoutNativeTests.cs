using System.Diagnostics;
using Translator.Core.Analysis.Ssa;
using Translator.Core.Ir;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class GuestLayoutNativeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(96)]
    public void ExpandedAllocationExecutesBothNativeBranchPaths(int addedInstructions)
    {
        var translated = GuestLayoutRelocationTests.ExpandedAllocationFunction();
        if (addedInstructions != 0)
        {
            var blocks = translated.LinearIr.Blocks.Select(block =>
            {
                var instructions = new List<IrInstruction>();
                foreach (var instruction in block.Instructions)
                {
                    instructions.Add(instruction);
                    if (instruction is IrAssign { Value.Constant: 0x95b0 })
                        for (var i = 0; i < addedInstructions; i++)
                            instructions.Add(new IrBinary("r3", IrValue.Register("r3"), IrValue.Register("r6"), "add"));
                }
                return new IrBasicBlock(block.Label, instructions);
            }).ToArray();
            var grown = translated.LinearIr with { Blocks = blocks };
            var ssa = new SsaTransformer().Convert(grown);
            var lowered = new FunctionTranslator(TranslatorCppTestHarness.CreateImage((0x80001000, 0x4e800020)))
                .LowerCanonical(0x80001000, ssa.Function, TranslationOptions.Default);
            Assert.True(lowered.CxxCode.Length > translated.CxxCode.Length);
            translated = lowered;
        }
        var directoryInfo = new DirectoryInfo(AppContext.BaseDirectory);
        while (directoryInfo is not null && !File.Exists(Path.Combine(directoryInfo.FullName, "translator/Translator.sln")))
            directoryInfo = directoryInfo.Parent;
        var root = directoryInfo?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var directory = Path.Combine(Path.GetTempPath(), "mkw-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var generated = Path.Combine(directory, "generated.cpp");
            var harness = Path.Combine(directory, "harness.cpp");
            var executable = Path.Combine(directory, "test");
            File.WriteAllText(generated, translated.CxxCode);
            File.WriteAllText(harness, $$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                void TranslatedFunctionRegistry::Register(TranslatedFunctionInfo) {}
                CpuContext& GetPersistentCpuContext() { static CpuContext ctx; return ctx; }
                extern "C" void func_80001000(CpuContext*);
                int main() {
                    CpuContext ctx{};
                    ctx.gpr[6] = 3;
                    ctx.gpr[4] = 0; func_80001000(&ctx);
                    if (ctx.gpr[3] != 0x95b2 + {{addedInstructions}} * 3) return 1;
                    ctx.gpr[4] = 1; func_80001000(&ctx);
                    return ctx.gpr[3] == 0x95b1 + {{addedInstructions}} * 3 ? 0 : 2;
                }
                """);
            var compile = new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX") ?? "clang++") { RedirectStandardError = true };
            foreach (var argument in new[] { "-std=c++20", "-I", Path.Combine(root, "runtime/include"),
                generated, harness, "-o", executable }) compile.ArgumentList.Add(argument);
            using (var process = Process.Start(compile)!)
            {
                var errorsTask = process.StandardError.ReadToEndAsync();
                var exited = process.WaitForExit(20000);
                if (!exited) process.Kill(entireProcessTree: true);
                Assert.True(exited, "Native layout compilation timed out");
                var errors = errorsTask.GetAwaiter().GetResult();
                Assert.True(process.ExitCode == 0, errors);
            }
            using var run = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false })!;
            var finished = run.WaitForExit(5000);
            if (!finished) run.Kill(entireProcessTree: true);
            Assert.True(finished, "Native layout execution timed out");
            Assert.Equal(0, run.ExitCode);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
