using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Translator.Core.Analysis;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class RacedataConsumerTests
{
    [Fact]
    [Trait("Requires", "USA game assets and Clang")]
    public void DeclaredConsumersReproduceTheirPointerProofsPreserveOriginalCodeAndCompileExpandedCode()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "translator/Translator.sln")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var image = new ProgramImageBuilder().Build(Path.Combine(root, "Assets/main.dol"),
            Path.Combine(root, "Assets/StaticR.rel"), 0x8050bf60);
        var manifest = Path.Combine(root, "projects/mkwii-ntsc-u/layouts/racedata.json");
        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        var definition = document.RootElement;
        var types = definition.GetProperty("Types").Deserialize<GuestLayoutType[]>()!;
        var globals = definition.GetProperty("GlobalPointers").EnumerateObject()
            .ToDictionary(pointer => Hex(pointer.Name), pointer => pointer.Value.GetString()!);
        var sites = definition.GetProperty("References").EnumerateArray().ToDictionary(
            reference => (Hex(reference.GetProperty("Instruction").GetString()!),
                reference.GetProperty("Node").GetInt32(), reference.GetProperty("Operand").GetString()!));
        var copies = definition.GetProperty("ArrayCopies").EnumerateArray().Select(copy => (
            Begin: Hex(copy.GetProperty("Begin").GetString()!),
            End: checked(Hex(copy.GetProperty("Begin").GetString()!)+(uint)copy.GetProperty("ExpectedWords").GetArrayLength()*4))).ToArray();
        var fills = definition.GetProperty("ArrayFills").EnumerateArray().Select(fill => (
            Begin: Hex(fill.GetProperty("Begin").GetString()!),
            End: checked(Hex(fill.GetProperty("Begin").GetString()!)+(uint)fill.GetProperty("ExpectedWords").GetArrayLength()*4))).ToArray();
        var map = FunctionMap.Load(Path.Combine(root, "projects/mkwii-ntsc-u/MAP.txt"));
        var options = new TranslationOptions(EnableLeafInlining: false, KnownFunctionEntryPoints: map.Addresses.ToHashSet());
        var original = new FunctionTranslator(image);
        var twelve = new FunctionTranslator(image, layoutRelocations: GuestLayoutRelocations.Read(manifest, 12));
        var expanded = new FunctionTranslator(image, layoutRelocations: GuestLayoutRelocations.Read(manifest, 24));
        var audit = new GuestLayoutAudit(types, 12, 24, globals, 0x80388880, 0x8038ac20, image);
        var originalLayout = new GuestLayouts(types, 12); var expandedLayout = new GuestLayouts(types, 24);
        var consumers = definition.GetProperty("AuditedConsumers").EnumerateArray().ToArray();
        Assert.True(consumers.Length >= 600, "Consumer verification list unexpectedly lost coverage");
        var covered = new HashSet<uint>();
        var temporary = Path.Combine(Path.GetTempPath(), "mkw-racedata-consumers-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (var group in consumers.Chunk(50))
            {
                var source = new StringBuilder();
                foreach (var consumer in group)
                {
                    var entry = Hex(consumer.GetProperty("Entry").GetString()!);
                    var name = consumer.GetProperty("Name").GetString()!;
                    Assert.Equal(name, map.NameOf(entry) ?? "");
                    var receiver = consumer.GetProperty("Receiver").GetString();
                    var discovery = original.Discover(entry, options);
                    var proof = audit.Analyze(discovery, receiver);
                    foreach (var candidate in proof.Candidates)
                    {
                        var reference = candidate.Reference;
                        if (copies.Concat(fills).Any(span => reference.Instruction >= span.Begin && reference.Instruction < span.End)) continue;
                        Assert.True(sites.TryGetValue((reference.Instruction, reference.Node, reference.Operand), out var declared),
                            $"Missing proven reference at {reference.Instruction:X8} in {entry:X8} {name}");
                        var symbol = declared.GetProperty("Symbol").GetString()!;
                        var addend = declared.TryGetProperty("Addend", out var add) ? add.GetInt32() : 0;
                        var scale = declared.TryGetProperty("Scale", out var factor) ? factor.GetInt32() : 1;
                        Assert.Equal(candidate.Before, checked(originalLayout.Symbol(symbol)*scale+addend));
                        Assert.Equal(candidate.After, checked(expandedLayout.Symbol(symbol)*scale+addend));
                        covered.Add(reference.Instruction);
                    }
                    var baseline = original.Translate(entry, options).CxxCode;
                    var hasVerifiedCopy = discovery.LinearIr.Blocks.SelectMany(block => block.Instructions)
                        .OfType<Translator.Core.Ir.IrTracePpc>().Any(trace => copies.Any(copy => trace.Address == copy.Begin));
                    if (hasVerifiedCopy)
                    {
                        // This routine has full original-capacity memory/register equivalence
                        // coverage in RacedataReaderTests; its verified copy emits new C++.
                        Assert.Equal(0x8052d19cu, entry);
                    }
                    else Assert.True(baseline == twelve.Translate(entry, options).CxxCode,
                        $"Original-capacity emission changed in {entry:X8} {name}");
                    source.AppendLine(expanded.Translate(entry, options).CxxCode);
                }
                var path = Path.Combine(temporary, "consumers.cpp");
                File.WriteAllText(path, source.ToString());
                var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX") ?? "clang++")
                { RedirectStandardError = true };
                foreach (var argument in new[] { "-std=c++20", "-march=x86-64-v3", "-fsyntax-only", "-ferror-limit=5",
                    "-I", Path.Combine(root, "runtime/include"), path }) start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!;
                var errors = process.StandardError.ReadToEndAsync();
                var finished = process.WaitForExit(45000);
                if (!finished) process.Kill(entireProcessTree: true);
                Assert.True(finished, "Consumer aggregate compilation timed out");
                Assert.True(process.ExitCode == 0, errors.GetAwaiter().GetResult());
            }
            Assert.True(covered.Count >= 900, $"Consumer proof coverage unexpectedly fell to {covered.Count}");
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    private static uint Hex(string value) => uint.Parse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
