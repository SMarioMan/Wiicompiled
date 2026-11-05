using System.Globalization;
using System.Text.Json;
using Translator.Core.Analysis;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class GuestLayoutAuditGameTests
{
    [Fact]
    [Trait("Requires", "USA game assets")]
    public void SuggestionsAgreeWithThePreviouslyAuditedLifecycleReferences()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "translator/Translator.sln")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var image = new ProgramImageBuilder().Build(Path.Combine(root, "Assets/main.dol"),
            Path.Combine(root, "Assets/StaticR.rel"), 0x8050bf60);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,
            "projects/mkwii-ntsc-u/layouts/racedata.json")));
        var types = document.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!;
        var expanded = new GuestLayouts(types, 24);
        var references = document.RootElement.GetProperty("References").EnumerateArray().ToDictionary(
            row => (uint.Parse(row.GetProperty("Instruction").GetString()![2..], NumberStyles.HexNumber),
                row.GetProperty("Node").GetInt32(), row.GetProperty("Operand").GetString()!));
        var map = FunctionMap.Load(Path.Combine(root, "projects/mkwii-ntsc-u/MAP.txt"));
        var translator = new FunctionTranslator(image);
        var auditor = new GuestLayoutAudit(types, 12, 24,
            new Dictionary<uint, string> { [0x809b8f68] = "Racedata" });
        var count = 0;
        foreach (var (entry, receiver) in new[]
        {
            (0x80529080u, "Scenario"), (0x8052b4f0u, "Racedata"),
            (0x8052b5acu, "Scenario"), (0x8052b614u, "Racedata"), (0x8052ba64u, "Scenario")
        })
        {
            var function = translator.Discover(entry, new TranslationOptions(EnableLeafInlining: false,
                KnownFunctionEntryPoints: map.Addresses.ToHashSet()));
            foreach (var candidate in auditor.Analyze(function, receiver).Candidates)
            {
                var site = candidate.Reference;
                Assert.True(references.TryGetValue((site.Instruction, site.Node, site.Operand), out var known),
                    $"Unreviewed audit suggestion at 0x{site.Instruction:X8}");
                var expected = expanded.Symbol(known.GetProperty("Symbol").GetString()!) +
                    (known.TryGetProperty("Addend", out var addend) ? addend.GetInt32() : 0);
                Assert.Equal(expected, candidate.After);
                count++;
            }
        }
        Assert.True(count > 30, $"Expected substantive field coverage; only found {count} candidates");
    }
}
