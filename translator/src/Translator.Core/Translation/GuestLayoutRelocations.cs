using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using Translator.Core.Disassembly;
using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Lifting;

namespace Translator.Core.Translation;

public sealed record GuestLayoutReference(uint Instruction, uint ExpectedWord, int Node,
    string Operand, string Symbol, int Addend = 0, int Scale = 1);

/// <summary>
/// Checked semantic relocations applied before SSA, inlining, ABI analysis and C++ emission.
/// Expanded values need not fit the original instruction's immediate encoding. Guest PCs remain
/// stable identities; C++ labels and symbols determine the expanded native control flow.
/// </summary>
public sealed partial class GuestLayoutRelocations
{
    private readonly GuestLayouts _original;
    private readonly GuestLayouts _expanded;
    private readonly IReadOnlyDictionary<uint, GuestLayoutReference[]> _references;
    public int OriginalPlayers => _original.Players;
    public int Players => _expanded.Players;
    public bool Complete { get; }
    public string? ManifestSha256 { get; private set; }

    public GuestLayoutRelocations(IEnumerable<GuestLayoutType> types, int originalPlayers, int players,
        IEnumerable<GuestLayoutReference> references, bool complete = false,
        IEnumerable<GuestArrayCopies>? arrayCopies = null,
        IEnumerable<GuestRankTable>? rankTables = null,
        IEnumerable<GuestBoundedAccess>? boundedAccesses = null,
        IEnumerable<GuestArrayFill>? arrayFills = null,
        IEnumerable<GuestResampledCursor>? resampledCursors = null,
        IEnumerable<GuestTraversalMarker>? traversalMarkers = null,
        IEnumerable<GuestCapacityTable>? capacityTables = null,
        IEnumerable<GuestGridTable>? gridTables = null,
        IEnumerable<GuestNormalizedRank>? normalizedRanks = null,
        IEnumerable<GuestBitsetStore>? bitsetStores = null,
        IEnumerable<GuestHeapRoute>? heapRoutes = null)
    {
        var definitions = types.ToArray();
        _original = new GuestLayouts(definitions, originalPlayers);
        _expanded = new GuestLayouts(definitions, players);
        Complete = complete;
        var sites = references.ToArray();
        if (sites.Select(r => (r.Instruction, r.Node, r.Operand)).Distinct().Count() != sites.Length)
            throw new InvalidDataException("Duplicate guest layout relocation operand.");
        foreach (var site in sites)
        {
            if (site.Instruction % 4 != 0 || site.Node < 0)
                throw new InvalidDataException("Invalid guest layout relocation site.");
            _ = checked(_original.Symbol(site.Symbol) * site.Scale + site.Addend);
            _ = checked(_expanded.Symbol(site.Symbol) * site.Scale + site.Addend);
        }
        _references = sites.GroupBy(r => r.Instruction).ToDictionary(g => g.Key, g => g.ToArray());
        _arrayCopies = (arrayCopies ?? Array.Empty<GuestArrayCopies>()).ToArray();
        ValidateCopyDefinitions(sites);
        _rankTables = (rankTables ?? Array.Empty<GuestRankTable>()).ToArray();
        ValidateRankDefinitions(sites);
        _boundedAccesses = (boundedAccesses ?? Array.Empty<GuestBoundedAccess>()).ToArray();
        ValidateBoundDefinitions();
        _arrayFills = (arrayFills ?? Array.Empty<GuestArrayFill>()).ToArray();
        ValidateFillDefinitions(sites);
        _resampledCursors=(resampledCursors ?? Array.Empty<GuestResampledCursor>()).ToArray();
        ValidateCursorDefinitions(sites);
        _traversalMarkers=(traversalMarkers ?? Array.Empty<GuestTraversalMarker>()).ToArray();
        ValidateMarkerDefinitions(sites);
        _capacityTables=(capacityTables ?? Array.Empty<GuestCapacityTable>()).ToArray();
        ValidateCapacityDefinitions(sites);
        _gridTables = (gridTables ?? Array.Empty<GuestGridTable>()).ToArray();
        ValidateGridDefinitions(sites);
        _normalizedRanks = (normalizedRanks ?? Array.Empty<GuestNormalizedRank>()).ToArray();
        ValidateNormalizedRankDefinitions(sites);
        _bitsetStores = (bitsetStores ?? Array.Empty<GuestBitsetStore>()).ToArray();
        ValidateBitsetDefinitions(sites);
        _heapRoutes = (heapRoutes ?? Array.Empty<GuestHeapRoute>()).ToArray();
        ValidateHeapRouteDefinitions(sites);
    }

    public void ValidateImage(ProgramImage image)
    {
        ValidateCopies(image);
        ValidateFills(image);
        ValidateCursors(image);
        ValidateMarkers(image);
        ValidateCapacityTables(image);
        ValidateGridTables(image);
        ValidateNormalizedRanks(image);
        ValidateBitsets(image);
        ValidateHeapRoutes(image);
        ValidateRankTables(image);
        ValidateBoundedAccesses(image);
        foreach (var (address, sites) in _references)
        {
            var word = BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(address, 4), 4));
            if (sites.Any(site => site.ExpectedWord != word))
                throw new InvalidDataException($"Guest layout instruction mismatch at 0x{address:X8} (got 0x{word:X8}).");
            var origin = PpcDecoder.Decode(address, word);
            var lifted = new PpcLifter().Lift(new[] { origin });
            _ = Apply(origin, lifted[0].Ir);
        }
    }

    public object Describe() => new
    {
        Format = "guest-layout-plan-v4", OriginalPlayers, Players, Complete,
        Types = _expanded.Types,
        BoundedAccesses = _boundedAccesses.Select(site => new {
            Instruction = $"0x{site.Instruction:X8}", ExpectedWord = $"0x{site.ExpectedWord:X8}",
            site.Node, site.Index, site.IndexBias, site.GroupWidth, site.Fallback, site.IndexBase, site.IndexStride,
            Enabled = Players % site.GroupWidth != 0
        }),
        RankTables = _rankTables.Select(table => new {
            Address = $"0x{table.Address:X8}", OriginalCapacity = OriginalPlayers, Capacity = Players,
            Extension = "scaled-linear-rank-curve-with-strict-rank-envelope",
            table.Curve,
            Lookups = table.Lookups.Select(site => new { Instruction = $"0x{site.Instruction:X8}", site.Players, site.Position })
        }),
        GridTables = _gridTables.Select(table => new {
            Address = $"0x{table.Address:X8}", ExpectedHex = Convert.ToHexString(table.ExpectedValues), table.AdvanceRows,
            Policy = "repeat terminal lateral pattern; advance terminal row pattern", table.Lookups }),
        HeapRoutes = _heapRoutes.Select(site => new { Begin=$"0x{site.Begin:X8}",
            ExpectedWords=site.ExpectedWords.Select(word=>$"0x{word:X8}"), site.Scene, site.PrimaryRegister, site.SecondaryRegister,
            site.ZeroRegister, site.PrimaryHeap, site.SecondaryHeap, site.PrimarySlot, site.SecondarySlot }),
        BitsetStores = _bitsetStores.Select(site => new { Begin=$"0x{site.Begin:X8}",
            ExpectedWords=site.ExpectedWords.Select(word=>$"0x{word:X8}"), site.Base, site.Index, site.Unit, site.Shift, site.Loaded, site.Offset, site.Count }),
        NormalizedRanks = _normalizedRanks.Select(site => new {
            Instruction = $"0x{site.Instruction:X8}", ExpectedWord = $"0x{site.ExpectedWord:X8}",
            site.Register, site.Count, site.SourceCount, TargetCount = _expanded.Symbol(site.Count),
            Policy = "floor of zero-based runtime rank scaled to the authored axis"
        }),
        CapacityTables = _capacityTables.Select(table=>new {
            Address=$"0x{table.Address:X8}",table.ExpectedValues,Extension="floor-scaled-terminal-threshold",
            Lookups=table.Lookups.Select(site=>new {Instruction=$"0x{site.Instruction:X8}",ExpectedWord=$"0x{site.ExpectedWord:X8}",
                site.Base,site.Index,site.Destination,site.Count})
        }),
        TraversalMarkers = _traversalMarkers.Select(site => new {
            Begin=$"0x{site.Begin:X8}",ExpectedWords=site.ExpectedWords.Select(word=>$"0x{word:X8}"),
            site.Source,site.Player,site.Destination,Lifetime="cleared-at-each-traversal-entry"
        }),
        ResampledCursors = _resampledCursors.Select(site => new {
            Instruction=$"0x{site.Instruction:X8}",ExpectedWord=$"0x{site.ExpectedWord:X8}",site.Cursor,site.NextIndex,
            site.IndexStride,site.SourceStride,site.Count,site.SourceCount,TargetCount=_expanded.Symbol(site.Count),
            Policy="closest-normalized-authored-rank"
        }),
        ArrayFills = _arrayFills.Select(fill => new {
            Begin = $"0x{fill.Begin:X8}", End = $"0x{FillEnd(fill):X8}", fill.BaseRegister, fill.ValueRegister,
            ExpectedWords = fill.ExpectedWords.Select(word => $"0x{word:X8}"), fill.Arrays, fill.Addresses, fill.Constants, fill.Copies
        }),
        ArrayCopies = _arrayCopies.Select(copy => new {
            Begin = $"0x{copy.Begin:X8}", End = $"0x{CopyEnd(copy):X8}", copy.BaseRegister,
            ExpectedWords = copy.ExpectedWords.Select(word => $"0x{word:X8}"),
            copy.Array, copy.Stride, copy.Count, copy.Fields,
            BeforeCount = _original.Symbol(copy.Count), AfterCount = _expanded.Symbol(copy.Count)
        }),
        References = _references.OrderBy(p => p.Key).SelectMany(p => p.Value).Select(site => new
        {
            Instruction = $"0x{site.Instruction:X8}", ExpectedWord = $"0x{site.ExpectedWord:X8}",
            site.Node, site.Operand, site.Symbol, site.Addend, site.Scale,
            Before = checked(_original.Symbol(site.Symbol) * site.Scale + site.Addend),
            After = checked(_expanded.Symbol(site.Symbol) * site.Scale + site.Addend)
        })
    };

    public IReadOnlyList<IrInstruction> Apply(PpcInstruction origin, IReadOnlyList<IrInstruction> instructions)
    {
        var heapRouteReplacement = ApplyHeapRoute(origin, instructions);
        if (heapRouteReplacement is not null) return heapRouteReplacement;
        var bitsetReplacement = ApplyBitsetStore(origin, instructions);
        if (bitsetReplacement is not null) return bitsetReplacement;
        var normalizedRankReplacement = ApplyNormalizedRank(origin, instructions);
        if (normalizedRankReplacement is not null) return normalizedRankReplacement;
        var gridReplacement = ApplyGridLookup(origin, instructions);
        if (gridReplacement is not null) return gridReplacement;
        var capacityReplacement = ApplyCapacityLookup(origin, instructions);
        if(capacityReplacement is not null)return capacityReplacement;
        var markerReplacement = ApplyMarker(origin, instructions);
        if (markerReplacement is not null) return markerReplacement;
        var cursorReplacement = ApplyCursor(origin, instructions);
        if (cursorReplacement is not null) return cursorReplacement;
        var fillReplacement = ApplyFill(origin, instructions);
        if (fillReplacement is not null) return fillReplacement;
        var rankReplacement = ApplyRankLookup(origin, instructions);
        if (rankReplacement is not null) return rankReplacement;
        foreach (var copy in _arrayCopies)
            if (origin.Address >= copy.Begin && origin.Address < CopyEnd(copy))
            {
                if (origin.Word != copy.ExpectedWords[checked((int)(origin.Address-copy.Begin)/4)])
                    throw new InvalidDataException($"Guest array-copy instruction mismatch at 0x{origin.Address:X8}.");
                return origin.Address == copy.Begin ? ExpandCopies(copy) : Array.Empty<IrInstruction>();
            }
        if (!_references.TryGetValue(origin.Address, out var sites)) return ApplyBoundedAccess(origin, instructions);
        var result = instructions.ToArray();
        foreach (var site in sites)
        {
            if (origin.Word != site.ExpectedWord || site.Node >= result.Length)
                throw new InvalidDataException($"Guest layout instruction mismatch at 0x{origin.Address:X8}.");
            var expected = checked(_original.Symbol(site.Symbol) * site.Scale + site.Addend);
            var replacement = checked(_expanded.Symbol(site.Symbol) * site.Scale + site.Addend);
            IrValue Replace(IrValue value)
            {
                if (value.Kind != "const" || value.Constant != expected) throw Mismatch();
                return IrValue.Imm(replacement);
            }
            int ReplaceOffset(int value)
            {
                if (value != expected) throw Mismatch();
                return replacement;
            }
            InvalidDataException Mismatch() => new($"Guest layout operand mismatch at 0x{origin.Address:X8}: {site.Operand}, {site.Symbol}.");
            if (site.Operand.StartsWith("argument:", StringComparison.Ordinal) &&
                result[site.Node] is IrCall call &&
                int.TryParse(site.Operand.AsSpan(9), NumberStyles.None, CultureInfo.InvariantCulture, out var argument) &&
                argument < call.Arguments.Count)
            {
                var arguments = call.Arguments.ToArray();
                arguments[argument] = Replace(arguments[argument]);
                result[site.Node] = call with { Arguments = arguments };
                continue;
            }
            result[site.Node] = (site.Operand, result[site.Node]) switch
            {
                ("value", IrAssign node) => node with { Value = Replace(node.Value) },
                ("left", IrBinary node) => node with { Left = Replace(node.Left) },
                ("right", IrBinary node) => node with { Right = Replace(node.Right) },
                ("right", IrSetCrField node) => node with { Right = Replace(node.Right) },
                ("offset", IrLoad node) => node with { Address = node.Address with { Offset = ReplaceOffset(node.Address.Offset) } },
                ("offset", IrStore node) => node with { Address = node.Address with { Offset = ReplaceOffset(node.Address.Offset) } },
                _ => throw Mismatch()
            };
        }
        return ApplyBoundedAccess(origin, result);
    }

    public static GuestLayoutRelocations Read(string path, int players)
    {
        var bytes = File.ReadAllBytes(path);
        ReadOnlySpan<byte> json = bytes;
        if (json.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) json = json[3..];
        var file = JsonSerializer.Deserialize<LayoutFile>(json)
            ?? throw new InvalidDataException("Empty guest layout file.");
        if (file.Version is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15 or 16)) throw new InvalidDataException("Unsupported guest layout format.");
        if (file.Version < 16 && file.References.Any(reference => reference.Symbol.Contains("floor(", StringComparison.Ordinal)))
            throw new InvalidDataException("Floor layout quotients require layout format version 16.");
        if (file.Version == 1 && file.ArrayCopies is { Length: > 0 })
            throw new InvalidDataException("Guest array-copy rewrites require layout format version 2.");
        if (file.Version < 3 && file.RankTables is { Length: > 0 })
            throw new InvalidDataException("Expandable rank tables require layout format version 3.");
        if (file.Version < 4 && file.BoundedAccesses is { Length: > 0 })
            throw new InvalidDataException("Bounded unrolled accesses require layout format version 4.");
        if (file.Version < 5 && file.ArrayFills is { Length: > 0 })
            throw new InvalidDataException("Unrolled array fills require layout format version 5.");
        if (file.Version < 6 && file.ResampledCursors is { Length: > 0 })
            throw new InvalidDataException("Resampled authored-table cursors require layout format version 6.");
        if (file.Version < 7 && file.ArrayFills?.Any(fill => fill.Addresses is { Length: > 0 }) == true)
            throw new InvalidDataException("Array-fill address setup requires layout format version 7.");
        if (file.Version < 8 && file.TraversalMarkers is { Length: > 0 })
            throw new InvalidDataException("Per-traversal markers require layout format version 8.");
        if (file.Version < 9 && file.ArrayFills is { Length: > 0 })
        {
            var original=new GuestLayouts(file.Types,file.OriginalPlayers);
            if(file.ArrayFills.Any(fill=>IsFpr(fill.ValueRegister) || fill.Arrays.Any(array=>original.Symbol(array.Stride)!=array.Width)))
                throw new InvalidDataException("Floating and strided scalar fills require layout format version 9.");
        }
        if(file.Version < 10 && file.ArrayFills?.Any(fill=>fill.Constants is {Length:>0})==true)
            throw new InvalidDataException("Array-fill constant setup requires layout format version 10.");
        if(file.Version < 10 && file.CapacityTables is {Length:>0})
            throw new InvalidDataException("Capacity threshold tables require layout format version 10.");
        if (file.Version < 11 && file.GridTables is { Length: > 0 })
            throw new InvalidDataException("Expandable starting grids require layout format version 11.");
        if (file.Version < 12 && file.NormalizedRanks is { Length: > 0 })
            throw new InvalidDataException("Normalized rank indices require layout format version 12.");
        if (file.Version < 12 && file.ArrayFills?.Any(fill => fill.Copies is { Length: > 0 }) == true)
            throw new InvalidDataException("Array-fill register copies require layout format version 12.");
        if (file.Version < 13 && (file.BitsetStores is { Length: > 0 } ||
            file.Types.Any(type => type.Fields?.Any(field => field.PlayersPerElement != 1) == true)))
            throw new InvalidDataException("Grouped player storage and bitset stores require layout format version 13.");
        if (file.Version < 14 && file.HeapRoutes is { Length: > 0 })
            throw new InvalidDataException("Heap routes require layout format version 14.");
        if (file.Version < 15 && file.ArrayFills?.Any(fill => fill.Arrays.Any(array => array.ValueRegister is not null)) == true)
            throw new InvalidDataException("Per-array fill sources require layout format version 15.");
        return new GuestLayoutRelocations(file.Types, file.OriginalPlayers, players,
            file.References.Select(r => new GuestLayoutReference(Hex(r.Instruction), Hex(r.ExpectedWord),
                r.Node, r.Operand, r.Symbol, r.Addend, r.Scale)), file.Complete,
            file.ArrayCopies?.Select(copy => new GuestArrayCopies(Hex(copy.Begin),
                copy.ExpectedWords.Select(Hex).ToArray(), copy.BaseRegister, copy.Array,
                copy.Stride, copy.Count, copy.Fields)),
            file.RankTables?.Select(table => new GuestRankTable(Hex(table.Address),
                Convert.FromHexString(table.ExpectedHex), table.Curve,
                table.Lookups.Select(site => new GuestRankLookup(Hex(site.Instruction), Hex(site.ExpectedWord),
                    site.Node, site.AddressBase, site.AddressOffset, site.Destination, site.Players, site.Position)).ToArray())),
            file.BoundedAccesses?.Select(site => new GuestBoundedAccess(Hex(site.Instruction), Hex(site.ExpectedWord),
                site.Node, site.Index, site.IndexBias, site.GroupWidth, site.Fallback, site.IndexBase, site.IndexStride)),
            file.ArrayFills?.Select(fill => new GuestArrayFill(Hex(fill.Begin), fill.ExpectedWords.Select(Hex).ToArray(),
                fill.BaseRegister, fill.ValueRegister, fill.Arrays, fill.Addresses, fill.Constants, fill.Copies)),
            file.ResampledCursors?.Select(site => new GuestResampledCursor(Hex(site.Instruction),Hex(site.ExpectedWord),
                site.Cursor,site.NextIndex,site.IndexStride,site.SourceStride,site.Count,site.SourceCount)),
            file.TraversalMarkers?.Select(site => new GuestTraversalMarker(Hex(site.Begin),site.ExpectedWords.Select(Hex).ToArray(),
                site.Source,site.Player,site.Destination)),
            file.CapacityTables?.Select(table=>new GuestCapacityTable(Hex(table.Address),table.ExpectedValues,
                table.Lookups.Select(site=>new GuestCapacityLookup(Hex(site.Instruction),Hex(site.ExpectedWord),
                    site.Base,site.Index,site.Destination,site.Count)).ToArray())),
            file.GridTables?.Select(table => new GuestGridTable(Hex(table.Address), Convert.FromHexString(table.ExpectedHex), table.AdvanceRows,
                table.Lookups.Select(site => new GuestGridLookup(Hex(site.Instruction), Hex(site.ExpectedWord),
                    site.Base, site.Index, site.Destination, site.CountOffset, site.Position)).ToArray())),
            file.NormalizedRanks?.Select(site => new GuestNormalizedRank(Hex(site.Instruction), Hex(site.ExpectedWord), site.Register, site.Count, site.SourceCount)),
            file.BitsetStores?.Select(site => new GuestBitsetStore(Hex(site.Begin), site.ExpectedWords.Select(Hex).ToArray(),
                site.Base, site.Index, site.Unit, site.Shift, site.Loaded, site.Offset, site.Count)),
            file.HeapRoutes?.Select(site => new GuestHeapRoute(Hex(site.Begin), site.ExpectedWords.Select(Hex).ToArray(), site.Scene,
                site.PrimaryRegister, site.SecondaryRegister, site.ZeroRegister, site.PrimaryHeap, site.SecondaryHeap, site.PrimarySlot, site.SecondarySlot)))
            { ManifestSha256 = ChecksumUtilities.Sha256Hex(bytes) };
    }

    private static uint Hex(string value) => uint.Parse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? value.AsSpan(2) : value.AsSpan(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    private sealed record LayoutFile(int Version, int OriginalPlayers, GuestLayoutType[] Types,
        ReferenceFile[] References, bool Complete = false, ArrayCopyFile[]? ArrayCopies = null, RankTableFile[]? RankTables = null,
        BoundAccessFile[]? BoundedAccesses = null, ArrayFillFile[]? ArrayFills = null, ResampledCursorFile[]? ResampledCursors = null, TraversalMarkerFile[]? TraversalMarkers = null, CapacityTableFile[]? CapacityTables = null, GridTableFile[]? GridTables = null, NormalizedRankFile[]? NormalizedRanks = null, BitsetStoreFile[]? BitsetStores = null, HeapRouteFile[]? HeapRoutes = null);
    private sealed record HeapRouteFile(string Begin,string[] ExpectedWords,string Scene,string PrimaryRegister,string SecondaryRegister,
        string ZeroRegister,int PrimaryHeap,int SecondaryHeap,int PrimarySlot,int SecondarySlot);
    private sealed record BitsetStoreFile(string Begin, string[] ExpectedWords, string Base, string Index, string Unit, string Shift, string Loaded, string Offset, string Count);
    private sealed record NormalizedRankFile(string Instruction, string ExpectedWord, string Register, string Count, int SourceCount);
    private sealed record GridTableFile(string Address, string ExpectedHex, bool AdvanceRows, GridLookupFile[] Lookups);
    private sealed record GridLookupFile(string Instruction, string ExpectedWord, string Base, string Index, string Destination, string CountOffset, string? Position = null);
    private sealed record CapacityTableFile(string Address,uint[] ExpectedValues,CapacityLookupFile[] Lookups);
    private sealed record CapacityLookupFile(string Instruction,string ExpectedWord,string Base,string Index,string Destination,string Count);
    private sealed record TraversalMarkerFile(string Begin,string[] ExpectedWords,string Source,string Player,string Destination);
    private sealed record ResampledCursorFile(string Instruction,string ExpectedWord,string Cursor,string NextIndex,
        string IndexStride,string SourceStride,string Count,int SourceCount);
    private sealed record ArrayFillFile(string Begin, string[] ExpectedWords, string BaseRegister, string ValueRegister, GuestFillArray[] Arrays, GuestFillAddress[]? Addresses = null, GuestFillConstant[]? Constants = null, GuestFillCopy[]? Copies = null);
    private sealed record BoundAccessFile(string Instruction, string ExpectedWord, int Node, string Index,
        int IndexBias, int GroupWidth, uint Fallback = 0, string? IndexBase = null, string? IndexStride = null);
    private sealed record RankTableFile(string Address, string ExpectedHex, int[] Curve, RankLookupFile[] Lookups);
    private sealed record RankLookupFile(string Instruction, string ExpectedWord, int Node, string AddressBase,
        int AddressOffset, string Destination, string Players, string Position);
    private sealed record ArrayCopyFile(string Begin, string[] ExpectedWords, string BaseRegister,
        string Array, string Stride, string Count, GuestArrayFieldCopy[] Fields);
    private sealed record ReferenceFile(string Instruction, string ExpectedWord, int Node,
        string Operand, string Symbol, int Addend = 0, int Scale = 1);
}
