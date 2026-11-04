using Translator.Core.Parsing.BrainSlug;
using Translator.Core.Parsing.Dol;
using Xunit;

namespace Translator.Tests;

public class BrainSlugSymbolPatternsTests
{
    private const string Pattern = """
        <symbols><symbol name="sdk_alias" offset="0x4"><data>12?4 AB?? CDEF</data></symbol></symbols>
        """;

    [Fact]
    public void ResolvesMaskedSignaturesToTheFunctionEntryAndIgnoresDataSections()
    {
        byte[] bytes = [0, 0, 0, 0, 0x12, 0x34, 0xAB, 0x99, 0xCD, 0xEF, 0, 0];
        var patterns = BrainSlugSymbolPatterns.Parse(Pattern);
        Assert.Equal(0x80001000u, patterns.Resolve("sdk_alias", [Section(SectionKind.Text, 0x80001000, bytes), Section(SectionKind.Data, 0x80002000, bytes)]));
        Assert.Null(patterns.Resolve("absent", [Section(SectionKind.Text, 0x80001000, bytes)]));
    }

    [Fact]
    public void RejectsAmbiguousAndMissingSignatures()
    {
        byte[] bytes = [0, 0, 0, 0, 0x12, 0x34, 0xAB, 0x99, 0xCD, 0xEF, 0, 0];
        var patterns = BrainSlugSymbolPatterns.Parse(Pattern);
        Assert.Throws<InvalidDataException>(() => patterns.Resolve("sdk_alias", [Section(SectionKind.Text, 0x80001000, bytes), Section(SectionKind.Text, 0x80002000, bytes)]));
        bytes[5] = 0x35;
        Assert.Throws<InvalidDataException>(() => patterns.Resolve("sdk_alias", [Section(SectionKind.Text, 0x80001000, bytes)]));
    }

    [Fact]
    public void SupportsSignaturesBeforeTheFunctionEntry()
    {
        var patterns = BrainSlugSymbolPatterns.Parse("<symbols><symbol name='next' offset='-0x4'><data>11223344</data></symbol></symbols>");
        byte[] bytes = [0, 0, 0, 0, 0x11, 0x22, 0x33, 0x44, 0, 0, 0, 0];
        Assert.Equal(0x80001008u, patterns.Resolve("next", [Section(SectionKind.Text, 0x80001000, bytes)]));
    }

    [Fact]
    public void RejectsExternalEntitiesAndPatternsWithoutAnAnchor()
    {
        Assert.Throws<System.Xml.XmlException>(() => BrainSlugSymbolPatterns.Parse("<!DOCTYPE symbols SYSTEM 'file:///unreadable'><symbols/>"));
        Assert.Throws<InvalidDataException>(() => BrainSlugSymbolPatterns.Parse("<symbols><symbol name='bad'><data>????</data></symbol></symbols>"));
    }

    private static DolSection Section(SectionKind kind, uint address, byte[] bytes) => new(0, "fixture", kind, 0, address, (uint)bytes.Length, bytes);
}
