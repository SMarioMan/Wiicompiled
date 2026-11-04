using System.Buffers.Binary;
using System.Text;
using Translator.Core.Mods;
using Translator.Core.Parsing.BrainSlug;
using Translator.Core.Parsing.Kamek;
using Xunit;

namespace Translator.Tests;

public class BrainSlugElfFileTests
{
    private const uint Original = 0x80001000;

    [Theory]
    [InlineData(0x81200000u)]
    [InlineData(0x81800100u)]
    public void LinksHooksAndMutableDataThroughTheExistingOverlayPipelineAtDifferentBases(uint moduleBase)
    {
        var elf = BrainSlugElfFile.Parse(Fixture());
        Assert.Equal(new[] { "original" }, elf.Imports);
        var result = elf.Link(name => name == "original" ? Original : null, _ => 0x38600007);
        result.ValidatePlacement(moduleBase);
        Assert.Equal(16u, result.RequiredAlignment);
        var chunk = KamekPulFile.Parse(result.ToCodePul('E')).SelectRegion("E");
        var root = Path.Combine(Path.GetTempPath(), "brainslug_link_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var originalBytes = Words(0x38600007, 0x4E800020, 0x60000000, 0x60000000);
            File.WriteAllBytes(Path.Combine(root, "text.bin"), originalBytes);
            var manifest = new BaseManifest("test", 1, "RMCE01", "E", "", 0,
                [new BaseSectionMetadata(".text", "fixture", Original, Original + 16, true, false, "text.bin", 0)],
                [new BaseFunctionRangeMetadata(Original, Original + 16, "original", ".text", 0, "fixture", [])], "");
            var plan = KamekPatchPlanner.Build(chunk, manifest, moduleBase);
            var built = OverlayFunctionBuilder.BuildAndWrite(chunk, manifest, plan, root, root);
            Assert.Empty(built.Diagnostics);
            var image = File.ReadAllBytes(Path.Combine(root, "overlay_images", built.ModuleImageFile));
            var hook = Assert.Single(result.Hooks);
            Assert.Equal(moduleBase + result.SymbolOffsets["entry"], Read(image, result.SymbolOffsets["pointer"]));
            Assert.Equal(KamekPpcEncoding.EncodeBranch(moduleBase + 4, moduleBase + hook.OriginalTrampolineOffset, true), Read(image, 4));
            var state = moduleBase + result.SymbolOffsets["state"];
            Assert.Equal((ushort)((state + 0x8000) >> 16), BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(10, 2)));
            Assert.Equal((ushort)state, BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(14, 2)));
            Assert.All(image.AsSpan((int)result.SymbolOffsets["state"], 16).ToArray(), value => Assert.Equal((byte)0, value));
            Assert.Equal(0x38600007u, Read(image, hook.OriginalTrampolineOffset));
            Assert.Equal(KamekPpcEncoding.EncodeBranch(moduleBase + hook.OriginalTrampolineOffset + 4, Original + 4, false),
                Read(image, hook.OriginalTrampolineOffset + 4));
            var overlay = Assert.Single(built.OverlayFunctions);
            var patched = File.ReadAllBytes(Path.Combine(root, "overlay_images", overlay.ImageFile));
            Assert.Equal(KamekPpcEncoding.EncodeBranch(Original, moduleBase + hook.ReplacementOffset, false), Read(patched, 0));
            var continuations = ContinuationPlanner.Build(chunk, manifest, moduleBase);
            Assert.Contains(continuations.Entries, entry => entry.Address == Original + 4);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(0x41820010u, 0x80001010u)]
    [InlineData(0x4182FFF0u, 0x80000FF0u)]
    [InlineData(0x48000010u, 0x80001010u)]
    [InlineData(0x4BFFFFF0u, 0x80000FF0u)]
    public void OriginalFunctionTrampolinesKeepRelativeBranchTargets(uint instruction, uint target)
    {
        var result = BrainSlugElfFile.Parse(Fixture()).Link(_ => Original, _ => instruction);
        var hook = Assert.Single(result.Hooks);
        var jumps = result.Chunk.Commands.Where(c => c.Id == KamekCommandId.Rel24 &&
            c.Address >= hook.OriginalTrampolineOffset).ToArray();
        Assert.Contains(jumps, jump => jump.Arguments[0] == target);
        if (instruction >> 26 == 16)
        {
            Assert.Equal(0x41820008u, Read(result.Chunk.CodeBlob, hook.OriginalTrampolineOffset));
            Assert.Contains(jumps, jump => jump.Arguments[0] == Original + 4);
        }
    }

    [Theory]
    [InlineData(0x48000011u, 0x80001010u)]
    [InlineData(0x48000005u, 0x80001004u)] // bl-to-next used to materialize the PC
    [InlineData(0x41820011u, 0x80001010u)]
    public void LinkingEntryPreservesOriginalLrAndRegistersAndRegistersItsReturnContinuation(uint instruction, uint target)
    {
        var result = BrainSlugElfFile.Parse(Fixture()).Link(_ => Original, _ => instruction);
        var stub = Assert.Single(result.Hooks).OriginalTrampolineOffset;
        Assert.Equal(Words(0x9421FFF0, 0x90010008, 0x3C000000, 0x60000000,
            0x7C0803A6, 0x80010008, 0x38210010), result.Chunk.CodeBlob.AsSpan((int)stub, 28).ToArray());
        Assert.Contains(result.Chunk.Commands, c => c.Address == stub + 10 &&
            c.Id == KamekCommandId.Addr16Hi && c.Arguments[0] == Original + 4);
        Assert.Contains(result.Chunk.Commands, c => c.Address == stub + 14 &&
            c.Id == KamekCommandId.Addr16Lo && c.Arguments[0] == Original + 4);
        Assert.Contains(result.Chunk.Commands, c => c.Address >= stub + 28 &&
            c.Id == KamekCommandId.Rel24 && c.Arguments[0] == target);
        Assert.Equal(0u, Read(result.Chunk.CodeBlob, stub + 28) & 1); // tail branch leaves LR intact
        var manifest = new BaseManifest("test", 1, "RMCE01", "E", "", 0,
            [new BaseSectionMetadata(".text", "fixture", Original, Original + 32, true, false, "text.bin", 0)],
            [new BaseFunctionRangeMetadata(Original, Original + 32, "original", ".text", 0, "fixture", [])], "");
        var continuation = Assert.Single(ContinuationPlanner.Build(result.Chunk, manifest, 0x81800000).Entries,
            entry => entry.Address == Original + 4);
        if (instruction >> 26 == 18 && target != Original + 4)
        {
            Assert.Equal(KamekCommandId.Addr16Hi, continuation.SourceCommandId);
            Assert.Equal(0x81800000 + stub + 10, continuation.SourceCommandAddress);
            Assert.Contains("LR return PC", continuation.Reason);
        }
    }

    [Fact]
    public void RejectsMissingImportsAndUnsafeEntryInstructions()
    {
        var elf = BrainSlugElfFile.Parse(Fixture());
        Assert.Throws<InvalidDataException>(() => elf.Link(_ => null, _ => 0x60000000));
        Assert.Throws<InvalidDataException>(() => elf.Link(_ => Original, _ => 0x4E800021));
        var result = elf.Link(_ => Original, _ => 0x60000000);
        Assert.Throws<ArgumentOutOfRangeException>(() => result.ValidatePlacement(0x81800004));
        Assert.Throws<ArgumentOutOfRangeException>(() => result.ValidatePlacement(0x84000000));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(51)]
    [InlineData(52)]
    public void RejectsTruncatedElf(int size) => Assert.Throws<InvalidDataException>(() => BrainSlugElfFile.Parse(Fixture().AsSpan(0, size)));

    [Fact]
    public void RejectsWrongMachineUnsupportedRelocationsAndOversizedBss()
    {
        var wrong = Fixture();
        BinaryPrimitives.WriteUInt16BigEndian(wrong.AsSpan(18, 2), 62);
        Assert.Throws<InvalidDataException>(() => BrainSlugElfFile.Parse(wrong));
        Assert.Throws<InvalidDataException>(() => BrainSlugElfFile.Parse(Fixture(relocation: 26)).Link(_ => Original, _ => 0));
        var large = Fixture();
        var table = (int)Read(large, 32);
        Write(large, table + 4 * 40 + 20, 0x1000000);
        Assert.Throws<InvalidDataException>(() => BrainSlugElfFile.Parse(large).Link(_ => Original, _ => 0));
    }

    [Fact]
    public void AbsoluteScalarRelocationsDoNotAcquireTheModuleBase()
    {
        var data = Fixture();
        var table = (int)Read(data, 32);
        var symbols = (int)Read(data, (uint)(table + 7 * 40 + 16));
        Write(data, symbols + 5 * 16 + 4, 0x12345678);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(symbols + 5 * 16 + 14, 2), 0xFFF1);
        var dataRelocations = (int)Read(data, (uint)(table + 9 * 40 + 16));
        Write(data, dataRelocations + 4, (5u << 8) | 1);
        var result = BrainSlugElfFile.Parse(data).Link(_ => Original, _ => 0x60000000);
        Assert.Contains(result.Chunk.Commands, c => c.Address == 10 && c.Id == KamekCommandId.Write16 && c.Arguments[0] == 0x1234);
        Assert.Contains(result.Chunk.Commands, c => c.Address == 14 && c.Id == KamekCommandId.Write16 && c.Arguments[0] == 0x5678);
        Assert.Contains(result.Chunk.Commands, c => c.Address == result.SymbolOffsets["pointer"] && c.Id == KamekCommandId.Write32 && c.Arguments[0] == 0x12345678);
    }

    private static byte[] Fixture(byte relocation = 10)
    {
        var strings = Encoding.UTF8.GetBytes("\0original\0entry\0state\0pointer\0");
        var symbols = new byte[7 * 16];
        void Symbol(int index, uint name, uint value, uint size, byte info, ushort section)
        {
            var p = index * 16;
            Write(symbols, p, name); Write(symbols, p + 4, value); Write(symbols, p + 8, size);
            symbols[p + 12] = info;
            BinaryPrimitives.WriteUInt16BigEndian(symbols.AsSpan(p + 14, 2), section);
        }
        Symbol(1, 0, 0, 0, 3, 1); Symbol(2, 0, 0, 0, 3, 2);
        Symbol(3, 1, 0, 0, 0x10, 0);
        Symbol(4, 10, 0, 4, 0x12, 1); Symbol(5, 16, 0, 16, 0x11, 4); Symbol(6, 22, 0, 4, 0x11, 3);
        var names = new[] { "", ".text", ".rodata", ".data", ".bss", ".bslug.load", ".strtab", ".symtab", ".rela.text", ".rela.data", ".rela.bslug.load", ".shstrtab" };
        var shStrings = new List<byte> { 0 };
        var shNames = new uint[names.Length];
        for (var i = 1; i < names.Length; i++) { shNames[i] = (uint)shStrings.Count; shStrings.AddRange(Encoding.UTF8.GetBytes(names[i] + "\0")); }
        byte[][] payloads = [[], Words(0x4E800020, 0x48000001, 0x3C600000, 0x38630000, 0x60000000),
            Encoding.UTF8.GetBytes("original\0"), new byte[4], [], Words(1, 0, 0), strings, symbols,
            Words(4, (3u << 8) | relocation, 0, 10, (5u << 8) | 6, 0, 14, (5u << 8) | 4, 0),
            Words(0, (4u << 8) | 1, 0), Words(4, (2u << 8) | 1, 0, 8, (4u << 8) | 1, 0), shStrings.ToArray()];
        uint[] types = [0, 1, 1, 1, 8, 1, 3, 2, 4, 4, 4, 3];
        uint[] flags = [0, 6, 2, 3, 3, 2, 0, 0, 0, 0, 0, 0];
        uint[] alignments = [0, 4, 1, 4, 16, 4, 1, 4, 4, 4, 4, 1];
        var offsets = new int[names.Length];
        var cursor = 52;
        for (var i = 1; i < names.Length; i++) { cursor = (cursor + 3) & ~3; offsets[i] = cursor; cursor += payloads[i].Length; }
        var sectionTable = (cursor + 3) & ~3;
        var data = new byte[sectionTable + names.Length * 40];
        new byte[] { 0x7F, 69, 76, 70, 1, 2, 1 }.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(16, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(18, 2), 20);
        Write(data, 20, 1); Write(data, 32, (uint)sectionTable);
        foreach (var (offset, value) in new[] { (40, 52), (46, 40), (48, names.Length), (50, 11) })
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset, 2), (ushort)value);
        for (var i = 1; i < names.Length; i++)
        {
            payloads[i].CopyTo(data, offsets[i]);
            var h = sectionTable + i * 40;
            Write(data, h, shNames[i]); Write(data, h + 4, types[i]); Write(data, h + 8, flags[i]);
            Write(data, h + 16, (uint)offsets[i]); Write(data, h + 20, i == 4 ? 16u : (uint)payloads[i].Length);
            Write(data, h + 24, i == 7 ? 6u : i is >= 8 and <= 10 ? 7u : 0);
            Write(data, h + 28, i switch { 7 => 3u, 8 => 1u, 9 => 3u, 10 => 5u, _ => 0u });
            Write(data, h + 32, alignments[i]); Write(data, h + 36, i == 7 ? 16u : i is >= 8 and <= 10 ? 12u : 0);
        }
        return data;
    }

    private static byte[] Words(params uint[] words)
    {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) Write(bytes, i * 4, words[i]);
        return bytes;
    }
    private static uint Read(byte[] data, uint offset) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)offset, 4));
    private static void Write(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), value);
}
