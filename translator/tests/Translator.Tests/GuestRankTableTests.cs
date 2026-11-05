using Translator.Core.Ir;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class GuestRankTableTests
{
    private static readonly GuestLayoutType[] Types = { new("Byte", 1, 1) };
    private static GuestRankTable Table() => new(0x80002000, new byte[] { 5, 0, 5, 0 }, new[] { 5, 0 },
        new[] { new GuestRankLookup(0x80001000, 0x8863ffff, 0, "r3", -1, "r3", "r22", "r23") });

    [Fact]
    public void RequiredRankLookupsCannotBeSilentlyReadAsAnOlderManifest()
    {
        var path = Path.GetTempFileName();
        try
        {
            foreach (var version in new[] { 2, 3 })
            {
                File.WriteAllText(path, JsonSerializer.Serialize(new {
                    Version = version, OriginalPlayers = 2, Types, References = Array.Empty<object>(),
                    RankTables = new[] { new {
                        Address = "0x80002000", ExpectedHex = "05000500", Curve = new[] { 5, 0 },
                        Lookups = new[] { new {
                            Instruction = "0x80001000", ExpectedWord = "0x8863FFFF", Node = 0,
                            AddressBase = "r3", AddressOffset = -1, Destination = "r3", Players = "r22", Position = "r23"
                        } }
                    } }
                }));
                if (version == 2) Assert.Throws<InvalidDataException>(() => GuestLayoutRelocations.Read(path, 4));
                else Assert.Equal(10u, GuestLayoutRelocations.Read(path, 4).RankPoints(0, 4, 1));
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OriginalRowsStayExactAndLargerFieldsInterpolateEveryRank()
    {
        var layout = new GuestLayoutRelocations(Types, 2, 4, Array.Empty<GuestLayoutReference>(), rankTables: new[] { Table() });
        Assert.Equal(5u, layout.RankPoints(0, 1, 1));
        Assert.Equal(new uint[] { 5, 0 }, Enumerable.Range(1, 2).Select(position => layout.RankPoints(0, 2, position)));
        Assert.Equal(new uint[] { 8, 4, 0 }, Enumerable.Range(1, 3).Select(position => layout.RankPoints(0, 3, position)));
        Assert.Equal(new uint[] { 10, 7, 3, 0 }, Enumerable.Range(1, 4).Select(position => layout.RankPoints(0, 4, position)));
        foreach (var (players, position) in new[] { (0, 1), (5, 1), (4, 0), (4, 5) })
            Assert.Equal(0u, layout.RankPoints(0, players, position));
        Assert.Throws<InvalidDataException>(() => new GuestLayoutRelocations(Types, 2, 4,
            Array.Empty<GuestLayoutReference>(), rankTables: new[] { Table() with { Curve = new[] { 0, 5 } } }));
    }

    [Fact]
    public void LookupsRequireTheOriginalDataAndLoadOperandsAndKeepTwelveStyleTranslationUnchanged()
    {
        var image = TranslatorCppTestHarness.CreateImage((0x80001000, 0x8863ffff), (0x80001004, 0x4e800020),
            (0x80002000, 0x05000500));
        var original = new FunctionTranslator(image).Translate(0x80001000);
        var unchanged = new GuestLayoutRelocations(Types, 2, 2, Array.Empty<GuestLayoutReference>(), rankTables: new[] { Table() });
        Assert.Equal(original.CxxCode, new FunctionTranslator(image, layoutRelocations: unchanged).Translate(0x80001000).CxxCode);
        var expanded = new GuestLayoutRelocations(Types, 2, 4, Array.Empty<GuestLayoutReference>(), rankTables: new[] { Table() });
        var translated = new FunctionTranslator(image, layoutRelocations: expanded).Translate(0x80001000);
        Assert.Contains(translated.LinearIr.Blocks.SelectMany(block => block.Instructions).OfType<IrCall>(),
            call => call.Target == "layout_rank_80001000" && call.Arguments[0].RegisterName == "r22" && call.Arguments[1].RegisterName == "r23");
        Assert.Contains("static inline uint32_t layout_rank_80001000", translated.CxxCode);
        var changed = TranslatorCppTestHarness.CreateImage((0x80001000, 0x8863ffff), (0x80001004, 0x4e800020),
            (0x80002000, 0x05000501));
        Assert.Throws<InvalidDataException>(() => new FunctionTranslator(changed, layoutRelocations: expanded));
        var wrong = Table() with { Lookups = new[] { Table().Lookups[0] with { AddressOffset = 0 } } };
        Assert.Throws<InvalidDataException>(() => new FunctionTranslator(image, layoutRelocations:
            new GuestLayoutRelocations(Types, 2, 4, Array.Empty<GuestLayoutReference>(), rankTables: new[] { wrong })));
    }
}
