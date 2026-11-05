using System.Diagnostics;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class RacedataLayoutTests
{
    [Theory]
    [InlineData(0x80529080u)] // Standalone Scenario constructor
    [InlineData(0x8052b310u)] // CreateInstance and its inlined constructor
    [InlineData(0x8052b4f0u)] // Racedata destructor
    [InlineData(0x8052b5acu)] // Scenario destructor
    [InlineData(0x8052b614u)] // Out-of-line Racedata constructor
    [InlineData(0x8052ba64u)] // Scenario assignment
    [InlineData(0x8052a1d0u)] // Game-type reader
    [InlineData(0x8052c3c4u)] // Team-mode reader
    [InlineData(0x8052d4e8u)] // Replay predicate
    [InlineData(0x8083b8b4u)] // Singleton-based menu player accessor
    [InlineData(0x80529e08u)] // Point updater and its per-player stack array
    [Trait("Requires", "USA game assets and Clang")]
    public void AuditedRoutinesAreUnchangedAtTwelveAndCompileAtTwentyFour(uint entry)
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
        var original = new FunctionTranslator(image).Translate(entry, options);
        var twelve = new FunctionTranslator(image, layoutRelocations: GuestLayoutRelocations.Read(manifest, 12))
            .Translate(entry, options);
        Assert.Equal(original.CxxCode, twelve.CxxCode);
        var expanded = new FunctionTranslator(image, layoutRelocations: GuestLayoutRelocations.Read(manifest, 24))
            .Translate(entry, options);
        Assert.NotEqual(original.CxxCode, expanded.CxxCode);
        if (entry == 0x8052b310)
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
