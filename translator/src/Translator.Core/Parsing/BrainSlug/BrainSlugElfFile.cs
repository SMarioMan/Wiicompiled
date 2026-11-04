using System.Buffers.Binary;
using System.Text;
using Translator.Core.Parsing.Kamek;

namespace Translator.Core.Parsing.BrainSlug;

public sealed record BrainSlugHook(string Name, uint OriginalAddress, uint ReplacementOffset, uint OriginalTrampolineOffset);

public sealed record BrainSlugLinkResult(
    KamekChunk Chunk,
    IReadOnlyList<BrainSlugHook> Hooks,
    IReadOnlyDictionary<string, uint> SymbolOffsets,
    uint RequiredAlignment)
{
    public void ValidatePlacement(uint moduleBase)
    {
        if (moduleBase < 0x80000000 || moduleBase % RequiredAlignment != 0 ||
            (ulong)moduleBase + Chunk.CodeSize > 0x100000000UL)
            throw new ArgumentOutOfRangeException(nameof(moduleBase), "Module placement violates guest range or section alignment.");
        foreach (var command in Chunk.Commands.Where(c => c.Id is KamekCommandId.Rel24 or KamekCommandId.Branch or KamekCommandId.BranchLink))
            KamekPpcEncoding.EncodeBranch(command.AddressIsAbsolute ? command.Address : checked(moduleBase + command.Address),
                KamekAddress.Resolve(command.Arguments[0], moduleBase), command.Id == KamekCommandId.BranchLink);
    }

    public byte[] ToCodePul(char region)
    {
        var index = "PEJK".IndexOf(region);
        if (index < 0) throw new ArgumentException("Expected region P, E, J or K.", nameof(region));
        using var stream = new MemoryStream();
        void Word(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            stream.Write(bytes);
        }
        for (var i = 0; i < 4; i++) Word(i == index ? Chunk.ChunkSize : 0);
        Word(KamekChunk.Magic0); Word(KamekChunk.Magic1);
        Word(Chunk.BssSize); Word(Chunk.CodeSize); Word(Chunk.CtorStart); Word(Chunk.CtorEnd);
        Word(Chunk.ChunkSize); Word(0);
        stream.Write(Chunk.CodeBlob);
        foreach (var command in Chunk.Commands)
        {
            Word(((uint)command.Id << 24) | (command.AddressIsAbsolute ? 0xFFFFFEu : command.Address));
            if (command.AddressIsAbsolute) Word(command.Address);
            foreach (var argument in command.Arguments) Word(argument);
        }
        return stream.ToArray();
    }
}

/// <summary>
/// Reads BrainSlug ELF32 PowerPC modules without executing their loader. Declared hooks and
/// ELF relocations become ordinary Kamek commands, so module addresses remain relocatable.
/// Instructions a module writes at runtime still need separate static patch lowering.
/// </summary>
public sealed class BrainSlugElfFile
{
    private const uint MaxImageSize = 0xFFFFFC; // Kamek's relative command address is 24 bits.
    private sealed record Section(string Name, uint Type, uint Flags, uint Offset, uint Size,
        uint Link, uint Info, uint Alignment, uint EntrySize);
    private sealed record Symbol(string Name, uint Value, uint Size, byte Info, ushort Section);
    private sealed record Relocation(uint Place, byte Kind, int Symbol, int Addend);
    private readonly byte[] _data;
    private readonly Section[] _sections;
    private readonly Symbol[] _symbols;
    private readonly int _symbolSection;

    private BrainSlugElfFile(byte[] data)
    {
        _data = data;
        Require(data.Length >= 52 && data.AsSpan(0, 7).SequenceEqual(new byte[] { 0x7F, 69, 76, 70, 1, 2, 1 }),
            "Expected a big-endian ELF32 module.");
        Require(U16(16) == 1 && U16(18) == 20 && U32(20) == 1, "Expected relocatable PowerPC ELF.");
        Require(U16(40) == 52 && U16(46) == 40 && U16(44) == 0, "Unsupported ELF header layout.");
        var count = U16(48);
        var namesIndex = U16(50);
        Require(count > 0 && namesIndex < count, "Invalid ELF section table.");
        var table = CheckedRange(U32(32), checked((uint)count * 40));
        var namesHeader = table + namesIndex * 40;
        Require(U32(namesHeader + 4) == 3, "Section names must reference a string table.");
        var names = Slice(U32(namesHeader + 16), U32(namesHeader + 20));
        _sections = new Section[count];
        for (var i = 0; i < count; i++)
        {
            var h = table + i * 40;
            var section = new Section(StringAt(names, U32(h)), U32(h + 4), U32(h + 8),
                U32(h + 16), U32(h + 20), U32(h + 24), U32(h + 28), U32(h + 32), U32(h + 36));
            Require(section.Alignment == 0 || (section.Alignment & (section.Alignment - 1)) == 0,
                $"Invalid section alignment: {section.Name}.");
            if (section.Type != 8) CheckedRange(section.Offset, section.Size);
            _sections[i] = section;
        }
        var tables = Enumerable.Range(0, count).Where(i => _sections[i].Type == 2).ToArray();
        Require(tables.Length == 1, "Expected exactly one ELF symbol table.");
        _symbolSection = tables[0];
        var symbols = _sections[_symbolSection];
        Require(symbols.EntrySize == 16 && symbols.Size % 16 == 0 && symbols.Link < count &&
            _sections[symbols.Link].Type == 3, "Invalid ELF symbol table.");
        var strings = Slice(_sections[symbols.Link].Offset, _sections[symbols.Link].Size);
        _symbols = new Symbol[symbols.Size / 16];
        for (var i = 0; i < _symbols.Length; i++)
        {
            var offset = checked((int)symbols.Offset + i * 16);
            var symbol = new Symbol(StringAt(strings, U32(offset)), U32(offset + 4), U32(offset + 8),
                _data[offset + 12], U16(offset + 14));
            Require(symbol.Section == 0 || symbol.Section == 0xFFF1 || symbol.Section < count,
                $"Unsupported section index for symbol '{symbol.Name}'.");
            if (symbol.Section is > 0 and < 0xFF00)
                Require((ulong)symbol.Value + symbol.Size <= _sections[symbol.Section].Size,
                    $"Symbol '{symbol.Name}' extends beyond its section.");
            _symbols[i] = symbol;
        }
    }

    public static BrainSlugElfFile Load(string path) => Parse(File.ReadAllBytes(path));
    public static BrainSlugElfFile Parse(ReadOnlySpan<byte> data) => new(data.ToArray());
    public IReadOnlyList<string> Imports => _symbols.Where(s => s.Section == 0 && s.Name.Length != 0)
        .Select(s => s.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    public BrainSlugLinkResult Link(Func<string, uint?> resolveSymbol, Func<uint, uint> readInstruction)
    {
        ArgumentNullException.ThrowIfNull(resolveSymbol);
        ArgumentNullException.ThrowIfNull(readInstruction);
        var offsets = new Dictionary<int, uint>();
        var image = new List<byte>();
        // Put executable sections first, but keep each section's original alignment.
        foreach (var i in Enumerable.Range(0, _sections.Length).Where(i => (_sections[i].Flags & 2) != 0)
                     .OrderByDescending(i => (_sections[i].Flags & 4) != 0).ThenBy(i => i))
        {
            var s = _sections[i];
            Require(s.Type is 1 or 8, $"Unsupported allocated section '{s.Name}'.");
            Pad(image, s.Alignment);
            offsets.Add(i, (uint)image.Count);
            Require((ulong)image.Count + s.Size <= MaxImageSize, "Module image is too large.");
            image.AddRange(s.Type == 8 ? new byte[s.Size] : Slice(s.Offset, s.Size).ToArray());
        }
        Pad(image, 4);
        var initialImage = image.ToArray();
        var relocations = new List<Relocation>();
        foreach (var s in _sections.Where(s => s.Type is 4 or 9))
        {
            Require(s.Info < _sections.Length, "Invalid relocation target section.");
            if (!offsets.TryGetValue((int)s.Info, out var target)) continue;
            Require(s.Type == 4 && s.EntrySize == 12 && s.Size % 12 == 0 && s.Link == _symbolSection,
                "Expected PowerPC RELA relocations linked to the module symbol table.");
            for (var pos = 0u; pos < s.Size; pos += 12)
            {
                var p = checked((int)(s.Offset + pos));
                var info = U32(p + 4);
                var kind = (byte)info;
                Require(kind is 0 or 1 or 4 or 5 or 6 or 10, $"Unsupported PowerPC relocation {kind}.");
                if (kind == 0) continue;
                var width = kind is 4 or 5 or 6 ? 2u : 4u;
                var offset = U32(p);
                Require((ulong)offset + width <= _sections[s.Info].Size && offset % width == 0,
                    "Relocation extends beyond its target section or is misaligned.");
                Require(info >> 8 < _symbols.Length, "Invalid relocation symbol index.");
                relocations.Add(new Relocation(target + offset, kind, (int)(info >> 8), unchecked((int)U32(p + 8))));
            }
        }
        var byPlace = relocations.GroupBy(r => r.Place).ToDictionary(g => g.Key, g => g.ToArray());
        Require(byPlace.Values.All(r => r.Length == 1), "Overlapping relocations are not supported.");
        uint previousEnd = 0;
        foreach (var r in relocations.OrderBy(r => r.Place))
        {
            Require(r.Place >= previousEnd, "Overlapping relocation fields are not supported.");
            previousEnd = r.Place + (r.Kind is 4 or 5 or 6 ? 2u : 4u);
        }
        uint Internal(Symbol symbol, int addend)
        {
            Require(offsets.TryGetValue(symbol.Section, out var start), $"Symbol '{symbol.Name}' is not allocated.");
            var value = (long)start + symbol.Value + addend;
            Require(value >= 0 && value <= MaxImageSize, "Module-relative symbol address is out of range.");
            return (uint)value;
        }
        uint Pointer(uint place)
        {
            Require(byPlace.TryGetValue(place, out var entries) && entries.Length == 1 && entries[0].Kind == 1,
                "BrainSlug hook pointers must use ADDR32 relocations.");
            var r = entries![0];
            return Internal(_symbols[r.Symbol], r.Addend);
        }
        var commands = new List<KamekCommand>();
        void Command(KamekCommandId kind, uint place, uint value, bool absolute = false) =>
            commands.Add(new KamekCommand(0, 0, kind, absolute, place, [value]));
        uint StubWord(uint instruction)
        {
            var offset = (uint)image.Count;
            Require(offset + 4 <= MaxImageSize, "Module trampoline image is too large.");
            image.Add((byte)(instruction >> 24)); image.Add((byte)(instruction >> 16));
            image.Add((byte)(instruction >> 8)); image.Add((byte)instruction);
            return offset;
        }
        void Jump(uint target) => Command(KamekCommandId.Rel24, StubWord(0x48000000), target);
        var hooks = new List<BrainSlugHook>();
        var originals = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var i in offsets.Keys.Where(i => _sections[i].Name == ".bslug.load"))
        {
            Require(_sections[i].Size % 12 == 0, "Invalid BrainSlug hook table length.");
            for (var n = 0u; n < _sections[i].Size; n += 12)
            {
                var p = offsets[i] + n;
                var type = BinaryPrimitives.ReadUInt32BigEndian(initialImage.AsSpan((int)p, 4));
                Require(type is 0 or 1, "BrainSlug exports need an explicit multi-module export resolver.");
                var name = StringAt(initialImage, Pointer(p + 4));
                Require(name.Length != 0, "Empty BrainSlug hook name.");
                var replacement = Pointer(p + 8);
                Require((replacement & 3) == 0 && offsets.Any(section =>
                    (_sections[section.Key].Flags & 4) != 0 && replacement >= section.Value &&
                    replacement < (ulong)section.Value + _sections[section.Key].Size), "Hook target is not executable.");
                var original = resolveSymbol(name);
                if (original is null && type == 0) continue;
                Require(original is >= 0x80000000 && (original.Value & 3) == 0, $"Missing or invalid hook symbol '{name}'.");
                Require(!originals.ContainsKey(name), $"Duplicate hook '{name}'.");
                var address = original!.Value;
                var first = readInstruction(address);
                var stub = (uint)image.Count;
                var op = first >> 26;
                Require(!(op == 19 && (((first >> 1) & 1023) is 16 or 528) && (first & 1) != 0),
                    $"Cannot safely move an indirect linking branch at the entry of '{name}'.");
                if (op is 16 or 18 && (first & 1) != 0)
                {
                    // The relocated instruction must expose its ORIGINAL return PC, including
                    // bl-to-next/mflr PC materialization. Preserve r0 and r1 using an EABI frame.
                    StubWord(0x9421FFF0); // stwu r1,-16(r1)
                    StubWord(0x90010008); // stw r0,8(r1)
                    Command(KamekCommandId.Addr16Hi, StubWord(0x3C000000) + 2, checked(address + 4));
                    Command(KamekCommandId.Addr16Lo, StubWord(0x60000000) + 2, checked(address + 4));
                    StubWord(0x7C0803A6); // mtlr r0
                    StubWord(0x80010008); // lwz r0,8(r1)
                    StubWord(0x38210010); // addi r1,r1,16
                    first &= ~1u;
                }
                uint OriginalTarget(uint target) => target == address ? stub : target;
                if (op == 16 && (first & 2) == 0)
                {
                    StubWord((first & 0xFFFF0000) | 8);
                    Jump(checked(address + 4));
                    Jump(OriginalTarget(unchecked(address + (uint)(short)(first & 0xFFFC))));
                }
                else if (op == 18 && (first & 2) == 0)
                {
                    var displacement = (int)(first << 6) >> 6;
                    Jump(OriginalTarget(unchecked(address + (uint)displacement)));
                }
                else
                {
                    StubWord(first);
                    if (first is not (0x4E800020 or 0x4E800420)) Jump(checked(address + 4));
                }
                originals.Add(name, stub);
                hooks.Add(new BrainSlugHook(name, address, replacement, stub));
                Command(KamekCommandId.Branch, address, replacement, absolute: true);
            }
        }
        foreach (var r in relocations)
        {
            var symbol = _symbols[r.Symbol];
            uint value;
            var relative = symbol.Section is not (0 or 0xFFF1);
            if (relative) value = Internal(symbol, r.Addend);
            else if (symbol.Section == 0xFFF1 || symbol.Name.Length == 0) value = unchecked(symbol.Value + (uint)r.Addend);
            else if (originals.TryGetValue(symbol.Name, out var stub))
            {
                var adjusted = (long)stub + r.Addend;
                Require(adjusted >= 0 && adjusted <= MaxImageSize, "Original-function reference is out of range.");
                value = (uint)adjusted;
                relative = true;
            }
            else
            {
                var resolved = resolveSymbol(symbol.Name);
                Require(resolved is >= 0x80000000, $"Missing import '{symbol.Name}'.");
                value = unchecked(resolved!.Value + (uint)r.Addend);
                Require(value >= 0x80000000, $"Import '{symbol.Name}' addend leaves guest address space.");
            }
            var kind = (KamekCommandId)r.Kind;
            if (!relative && value < 0x80000000)
            {
                Require(r.Kind != 10, "Cannot encode a branch to a low absolute address in Kamek.");
                if (r.Kind == 1) kind = KamekCommandId.Write32;
                else
                {
                    value = r.Kind switch { 4 => value & 0xFFFF, 5 => value >> 16, 6 => unchecked(value + 0x8000) >> 16, _ => value };
                    kind = KamekCommandId.Write16;
                }
            }
            if (r.Kind == 10)
            {
                var instruction = BinaryPrimitives.ReadUInt32BigEndian(initialImage.AsSpan((int)r.Place, 4));
                Require((instruction & 0xFC000002) == 0x48000000, "REL24 does not reference a relative branch.");
                Require((value & 3) == 0, "REL24 target is not instruction aligned.");
            }
            Command(kind, r.Place, value);
        }
        var names = _symbols.Where(s => s.Name.Length != 0 && offsets.ContainsKey(s.Section))
            .GroupBy(s => s.Name, StringComparer.Ordinal).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => Internal(g.Single(), 0), StringComparer.Ordinal);
        var bytes = image.ToArray();
        var length = checked((uint)(KamekChunk.HeaderSize + bytes.Length + commands.Sum(c => c.AddressIsAbsolute ? 12 : 8)));
        var alignment = Math.Max(4u, offsets.Keys.Select(i => _sections[i].Alignment).DefaultIfEmpty(1u).Max());
        return new BrainSlugLinkResult(new KamekChunk(0, 0, 0, (uint)bytes.Length, 0, 0, length, bytes, commands), hooks, names, alignment);
    }

    private static void Pad(List<byte> image, uint alignment)
    {
        alignment = Math.Max(alignment, 1);
        var end = ((ulong)image.Count + alignment - 1) & ~((ulong)alignment - 1);
        Require(end <= MaxImageSize, "Module alignment exceeds Kamek image limits.");
        while ((ulong)image.Count < end) image.Add(0);
    }
    private int CheckedRange(uint offset, uint size)
    {
        Require((ulong)offset + size <= (ulong)_data.Length, "ELF range extends beyond the file.");
        return (int)offset;
    }
    private ReadOnlySpan<byte> Slice(uint offset, uint size) => _data.AsSpan(CheckedRange(offset, size), (int)size);
    private ushort U16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(offset, 2));
    private uint U32(int offset) => BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(offset, 4));
    private static string StringAt(ReadOnlySpan<byte> bytes, uint offset)
    {
        Require(offset < bytes.Length, "String offset is outside its table.");
        var value = bytes[(int)offset..];
        var end = value.IndexOf((byte)0);
        Require(end >= 0, "Unterminated ELF string.");
        try { return new UTF8Encoding(false, true).GetString(value[..end]); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid UTF-8 in ELF string table.", error); }
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
