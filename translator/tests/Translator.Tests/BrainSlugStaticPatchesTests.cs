using System.Buffers.Binary;
using Translator.Core.Mods;
using Translator.Core.Parsing.BrainSlug;
using Translator.Core.Parsing.Kamek;
using Xunit;

namespace Translator.Tests;

public class BrainSlugStaticPatchesTests
{
    private const uint Original = 0x80001000;
    private static BrainSlugLinkResult Module() => new(
        new KamekChunk(0, 0, 0, 32, 0, 0, 64, new byte[32], []), [],
        new Dictionary<string, uint> { ["stub"] = 0, ["state"] = 16 }, 4);

    [Theory]
    [InlineData(0x81200000u)]
    [InlineData(0x81800100u)]
    public void CapturesCleanGameStateAndRelocatesStaticHooksThroughTheOverlayBuilder(uint moduleBase)
    {
        var words = new uint[] { 0x9421FFF0, 0x48000009, 0x3860FFFF, 0x4E800020 };
        uint ReadWord(uint address) => words[(address - Original) / 4];
        var linked = BrainSlugStaticPatches.Apply(Module(),
        [
            new("Copy32", "module:stub", "game:original"),
            new("Branch", "module:stub+0x4", "game:original+0x4"),
            new("BranchLink", "game:original", "module:stub"),
            new("CaptureBranchTarget", "module:state", "game:original+0x4"),
            new("Copy16", "module:state+0x4", "game:original+0x8+0x2"),
            new("Write32", "module:state+0x8", "0x3C600000"),
            new("Write32", "module:state+0xC", "0x38630000"),
            new("Addr16Ha", "module:state+0x8+0x2", "module:state"),
            new("Addr16Lo", "module:state+0xC+0x2", "module:state")
        ], name => name == "original" ? Original : null, ReadWord);
        linked.ValidatePlacement(moduleBase);
        var chunk = KamekPulFile.Parse(linked.ToCodePul('E')).SelectRegion("E");
        var root = Path.Combine(Path.GetTempPath(), "lan_static_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = new byte[words.Length * 4];
            for (var i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4, 4), words[i]);
            File.WriteAllBytes(Path.Combine(root, "text.bin"), bytes);
            var manifest = new BaseManifest("test", 1, "RMCE01", "E", "", 0,
                [new BaseSectionMetadata(".text", "fixture", Original, Original + 16, true, false, "text.bin", 0)],
                [new BaseFunctionRangeMetadata(Original, Original + 16, "original", ".text", 0, "fixture", [])], "");
            var built = OverlayFunctionBuilder.BuildAndWrite(chunk, manifest,
                KamekPatchPlanner.Build(chunk, manifest, moduleBase), root, root);
            Assert.Empty(built.Diagnostics);
            var image = File.ReadAllBytes(Path.Combine(root, "overlay_images", built.ModuleImageFile));
            uint Word(int offset) => BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(offset, 4));
            Assert.Equal(words[0], Word(0));
            Assert.Equal(KamekPpcEncoding.EncodeBranch(moduleBase + 4, Original + 4, false), Word(4));
            Assert.Equal(Original + 12, Word(16));
            Assert.Equal(0xFFFFu, Word(20) >> 16);
            Assert.Equal(0x3C600000u | ((moduleBase + 16 + 0x8000) >> 16), Word(24));
            Assert.Equal(0x38630000u | ((moduleBase + 16) & 0xFFFF), Word(28));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("Write32", "module:missing", "0x0")]
    [InlineData("Write32", "game:missing", "0x0")]
    [InlineData("Write32", "module:stub+0x1", "0x0")]
    [InlineData("Write32", "module:stub+0x20", "0x0")]
    [InlineData("Write32", "module:stub", "module:state")]
    [InlineData("Write16", "module:stub", "0x10000")]
    [InlineData("Branch", "module:stub", "0x0")]
    [InlineData("Copy32", "module:stub", "module:state")]
    [InlineData("CaptureBranchTarget", "module:state", "0x80001000")]
    [InlineData("Execute", "module:stub", "0x0")]
    public void RejectsUnresolvedUnsafeOrMalformedPatches(string operation, string address, string value)
    {
        Assert.Throws<InvalidDataException>(() => BrainSlugStaticPatches.Apply(Module(),
            [new(operation, address, value)], _ => null, _ => 0x60000000));
    }

    [Theory]
    [InlineData(16u, 0x41820010u)]
    [InlineData(0x120u, 0x41820120u)]
    [InlineData(0x7FFCu, 0x41827FFCu)]
    public void ResolvesMovedConditionalBranchTargetsInBothDirections(uint distance, uint expected)
    {
        var linked = BrainSlugStaticPatches.Apply(Module(),
        [
            new("BranchConditional", "game:start", "game:finish", "0x41820000"),
            new("BranchConditional", "game:finish", "game:start", "0x40820001"),
            new("BranchConditional", "module:stub", "module:state", "0x40820000")
        ], name => name == "start" ? Original : Original + distance, _ => 0);
        var chunk = KamekPulFile.Parse(linked.ToCodePul('E')).SelectRegion("E");
        Assert.All(chunk.Commands, command => Assert.Equal(KamekCommandId.Write32, command.Id));
        Assert.Equal(expected, Assert.Single(chunk.Commands[0].Arguments));
        Assert.Equal(0x40820001u | (unchecked(0u - distance) & 0xFFFCu), Assert.Single(chunk.Commands[1].Arguments));
        Assert.False(chunk.Commands[2].AddressIsAbsolute);
        Assert.Equal(0x40820010u, Assert.Single(chunk.Commands[2].Arguments));
    }

    [Theory]
    [InlineData("0x80009000", "0x41820000")]
    [InlineData("0x80001002", "0x41820000")]
    [InlineData("module:state", "0x41820000")]
    [InlineData("0x80001004", "0x41820004")]
    [InlineData("0x80001004", "0x41820002")]
    [InlineData("0x80001004", "0x48000000")]
    [InlineData("0x80001004", null)]
    public void RejectsInvalidConditionalBranchOperands(string target, string? condition)
    {
        Assert.Throws<InvalidDataException>(() => BrainSlugStaticPatches.Apply(Module(),
            [new("BranchConditional", "0x80001000", target, condition)], _ => null, _ => 0));
    }
}
