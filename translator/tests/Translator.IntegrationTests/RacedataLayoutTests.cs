using System.Diagnostics;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class RacedataLayoutTests
{
    [Fact]
    [Trait("Requires", "USA game assets and Clang")]
    public void OriginalConstructorIsUnchangedAtTwelveAndCompilesAtTwentyFour()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "translator/Translator.sln")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var dol = Path.Combine(root, "Assets/main.dol");
        var rel = Path.Combine(root, "Assets/StaticR.rel");
        Assert.Equal("d2beec1b1645fcd134efe9e7e63774b546667764ed8d431029daccd725995694",
            ChecksumUtilities.Sha256Hex(File.ReadAllBytes(dol)));
        Assert.Equal("1168107f8fdef27a356df76036db55afe4fbf7752606dbac991726701133a617",
            ChecksumUtilities.Sha256Hex(File.ReadAllBytes(rel)));
        var image = new ProgramImageBuilder().Build(dol, rel, 0x8050bf60);
        var manifest = Path.Combine(root, "projects/mkwii-ntsc-u/layouts/racedata.json");
        var map = FunctionMap.Load(Path.Combine(root, "projects/mkwii-ntsc-u/MAP.txt"));
        var options = new TranslationOptions(EnableLeafInlining: false,
            KnownFunctionEntryPoints: map.Addresses.ToHashSet());
        var original = new FunctionTranslator(image).Translate(0x8052b310, options);
        var twelve = new FunctionTranslator(image, layoutRelocations: GuestLayoutRelocations.Read(manifest, 12))
            .Translate(0x8052b310, options);
        Assert.Equal(original.CxxCode, twelve.CxxCode);
        var expanded = new FunctionTranslator(image, layoutRelocations: GuestLayoutRelocations.Read(manifest, 24))
            .Translate(0x8052b310, options);
        Assert.NotEqual(original.CxxCode, expanded.CxxCode);
        Assert.Contains(expanded.LinearIr.Blocks.SelectMany(b => b.Instructions).OfType<Translator.Core.Ir.IrAssign>(),
            node => node.Value.Constant == 0x95b0);

        var temporary = Path.Combine(Path.GetTempPath(), "mkw-racedata-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var source = Path.Combine(temporary, "racedata.cpp");
            File.WriteAllText(source, expanded.CxxCode);
            var compile = new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX") ?? "clang++")
            { RedirectStandardError = true };
            foreach (var argument in new[] { "-std=c++20", "-march=x86-64-v3", "-fsyntax-only",
                "-I", Path.Combine(root, "runtime/include"), source }) compile.ArgumentList.Add(argument);
            using var process = Process.Start(compile)!;
            var errorsTask = process.StandardError.ReadToEndAsync();
            var exited = process.WaitForExit(20000);
            if (!exited) process.Kill(entireProcessTree: true);
            Assert.True(exited, "Expanded constructor compilation timed out");
            var errors = errorsTask.GetAwaiter().GetResult();
            Assert.True(process.ExitCode == 0, errors);
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }
}
