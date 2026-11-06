using System.Text.Json;
using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class GuestLayoutGridTableTests
{
    private const uint Start = 0x80001000, Table = 0x80003000, Word = 0x7c1fe0ae;
    private static readonly GuestLayoutType[] Types = { new("Word", 4, 4) };
    private static GuestGridLookup Site() => new(Start, Word, "r31", "r28", "r0", "r28", "r26");
    private static byte[] Values(bool rows)
    {
        var values = new byte[144];
        var lateral = new[] { -10, -2, 6, -6, 2, 10 };
        for (var count = 1; count <= 12; count++)
            for (var i = 0; i < count; i++)
                values[(count - 1) * 12 + i] = unchecked((byte)(rows ? i / 3 : lateral[i % 6]));
        return values;
    }
    private static GuestGridTable Curve(bool rows = false) => new(Table, Values(rows), rows, new[] { Site() });
    private static GuestLayoutRelocations Layout(int players, GuestGridTable? table = null) => new(Types, 12, players,
        Array.Empty<GuestLayoutReference>(), gridTables: new[] { table ?? Curve() });
    private static ProgramImage Image(uint word = Word, byte[]? values = null)
    {
        var bytes = values ?? Values(false);
        var data = Enumerable.Range(0, 36).Select(i => (Table + (uint)i * 4,
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i * 4, 4))));
        return TranslatorCppTestHarness.CreateImage(new[] { (Start, word), (Start + 4, 0x4e800020u) }.Concat(data).ToArray());
    }

    [Fact]
    public void EveryAuthoredCellIsPreservedAndTerminalLanesAndRowsExtendUniformly()
    {
        foreach (var rows in new[] { false, true })
        {
            var values = Values(rows); var layout = Layout(100, Curve(rows));
            for (var count = 1; count <= 12; count++)
                for (var position = 0; position < count; position++)
                    Assert.Equal((uint)values[(count - 1) * 12 + position], layout.GridCoordinate(0, count, position));
            foreach (var count in new[] { 13, 24, 48, 100 })
                for (var position = 0; position < count; position++)
                    Assert.Equal(rows ? (uint)(position / 3) : (uint)values[132 + position % 12], layout.GridCoordinate(0, count, position));
            Assert.Equal(0u, layout.GridCoordinate(0, 0, 0));
            Assert.Equal(0u, layout.GridCoordinate(0, 101, 0));
            Assert.Equal(0u, layout.GridCoordinate(0, 24, -1));
            Assert.Equal(0u, layout.GridCoordinate(0, 24, 24));
        }
    }

    [Fact]
    public void OriginalCodeIsIdenticalAndExpandedLoadsBecomeCheckedPureHelpers()
    {
        var image = Image();
        Assert.Equal(new FunctionTranslator(image).Translate(Start, TranslationOptions.Default).CxxCode,
            new FunctionTranslator(image, layoutRelocations: Layout(12)).Translate(Start, TranslationOptions.Default).CxxCode);
        var result = new FunctionTranslator(image, layoutRelocations: Layout(24)).Translate(Start, TranslationOptions.Default);
        Assert.Contains("layout_grid_80001000", result.CxxCode);
        Assert.Contains(result.LinearIr.Blocks.SelectMany(block => block.Instructions), node => node is IrCall { Target: "layout_grid_80001000", Destination: "r0" });
        var first = new FunctionTranslator(image, layoutRelocations: Layout(24, Curve() with {
            Lookups = new[] { Site() with { Position = null } } })).Translate(Start, TranslationOptions.Default);
        Assert.Contains(first.LinearIr.Blocks.SelectMany(block => block.Instructions), node => node is IrCall call && call.Arguments[1] == IrValue.Imm(0));
    }

    [Fact]
    public void StaleDataWrongLoadsRegistersDimensionsInactiveCellsAndOverlapAreRejected()
    {
        var values = Values(false); values[0] ^= 1;
        Assert.Throws<InvalidDataException>(() => new FunctionTranslator(Image(values: values), layoutRelocations: Layout(24)));
        Assert.Throws<InvalidDataException>(() => new FunctionTranslator(Image(Word ^ 1), layoutRelocations: Layout(24)));
        Assert.Throws<InvalidDataException>(() => new FunctionTranslator(Image(), layoutRelocations: Layout(24,
            Curve() with { Lookups = new[] { Site() with { Destination = "r5" } } })));
        Assert.Throws<InvalidDataException>(() => Layout(24, Curve() with { ExpectedValues = new byte[12] }));
        Assert.Throws<InvalidDataException>(() => Layout(24, Curve() with { Lookups = new[] { Site() with { CountOffset = "r32" } } }));
        values = Values(false); values[1] = 1;
        Assert.Throws<InvalidDataException>(() => Layout(24, Curve() with { ExpectedValues = values }));
        Assert.Throws<InvalidDataException>(() => Layout(24, Curve() with { AdvanceRows = true }));
        Assert.Throws<InvalidDataException>(() => Layout(1000, Curve(rows: true)));
        Assert.Throws<InvalidDataException>(() => new GuestLayoutRelocations(Types, 12, 24,
            new[] { new GuestLayoutReference(Start, Word, 0, "right", "Word.size") }, gridTables: new[] { Curve() }));
    }

    [Fact]
    public void ReadersRequireVersionEleven()
    {
        var path = Path.GetTempFileName();
        try
        {
            foreach (var version in new[] { 10, 11, 12 })
            {
                File.WriteAllText(path, JsonSerializer.Serialize(new { Version = version, OriginalPlayers = 12, Types,
                    References = Array.Empty<object>(), GridTables = new[] { new { Address = "0x80003000", ExpectedHex = Convert.ToHexString(Values(false)),
                        AdvanceRows = false, Lookups = new[] { new { Instruction = "0x80001000", ExpectedWord = "0x7C1FE0AE",
                            Base = "r31", Index = "r28", Destination = "r0", CountOffset = "r28", Position = "r26" } } } } }));
                if (version is 11 or 12) Assert.Equal(24, GuestLayoutRelocations.Read(path, 24).Players);
                else Assert.Throws<InvalidDataException>(() => GuestLayoutRelocations.Read(path, 24));
            }
        }
        finally { File.Delete(path); }
    }
}
