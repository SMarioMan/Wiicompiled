using System.Buffers.Binary;
using System.Text;
using Translator.Core.Analysis.BasicBlocks;
using Translator.Core.Disassembly;
using Translator.Core.Ir;
using Translator.Core.Lifting;
using Translator.Core.Loading;

namespace Translator.Core.Translation;

public sealed record GuestWordComparison(uint Begin, IReadOnlyList<uint> ExpectedWords,
    string LeftBase, string LeftOffset, string LeftValue, string Count, int Field = 0, bool IsUnsigned = true,
    uint? RightLoad = null, string? RightBase = null, string? RightOffset = null, string? RightValue = null);
public sealed record GuestWordTableEntry(int Index, string Symbol, int Addend = 0, int Scale = 1);
public sealed record GuestWordTableLoad(uint Instruction, uint ExpectedWord, string Base, string Destination,
    int Offset = 0, string? Index = null);
public sealed record GuestWordTable(uint Address, IReadOnlyList<uint> ExpectedValues,
    IReadOnlyList<GuestWordTableEntry> Entries, IReadOnlyList<GuestWordTableLoad> Loads);

public sealed partial class GuestLayoutRelocations
{
    private readonly GuestWordComparison[] _wordComparisons;
    private readonly GuestWordTable[] _wordTables;
    private static uint WordComparePc(GuestWordComparison site) => checked(site.Begin + (uint)(site.ExpectedWords.Count - 1) * 4);
    private static string WordCompareHelper(GuestWordComparison site) => $"layout_word_compare_{WordComparePc(site):X8}";
    private static string WordTableHelper(GuestWordTableLoad site) => $"layout_word_table_{site.Instruction:X8}";

    private bool HasOtherWordRewrite(uint pc, bool includeReferences = true) => (includeReferences && _references.ContainsKey(pc)) ||
        _arrayCopies.Any(x => pc >= x.Begin && pc < CopyEnd(x)) || _arrayFills.Any(x => pc >= x.Begin && pc < FillEnd(x)) ||
        _arrayTransfers.Any(x => pc >= x.Begin && pc < TransferEnd(x)) || _bitsetStores.Any(x => pc >= x.Begin && pc < BitsetEnd(x)) ||
        _bitsetLoads.Any(x => x.Instruction == pc) || _rankTables.Any(x => x.Lookups.Any(y => y.Instruction == pc)) ||
        _capacityTables.Any(x => x.Lookups.Any(y => y.Instruction == pc)) || _gridTables.Any(x => x.Lookups.Any(y => y.Instruction == pc)) ||
        _boundedAccesses.Any(x => x.Instruction == pc) || _normalizedRanks.Any(x => x.Instruction == pc) ||
        _resampledCursors.Any(x => x.Instruction == pc) || _authoredCountIndices.Any(x => x.Instruction == pc) ||
        _traversalMarkers.Any(x => pc >= x.Begin && pc < MarkerEnd(x)) || _heapRoutes.Any(x => pc >= x.Begin && pc < HeapRouteEnd(x));

    private void ValidateWordArrays()
    {
        var seen = new HashSet<uint>();
        foreach (var site in _wordComparisons)
        {
            if (site.Begin % 4 != 0 || site.ExpectedWords.Count is < 2 or > 16 || site.Field is < 0 or > 7 ||
                !IsGpr(site.LeftBase) || site.LeftBase == "r0" || !IsGpr(site.LeftValue) || site.LeftValue is "r1" or "r2" ||
                site.LeftBase == site.LeftValue || _original.Symbol(site.Count) != 1 || _expanded.Symbol(site.Count) < 1 || Players < OriginalPlayers ||
                !seen.Add(WordComparePc(site)) || HasOtherWordRewrite(WordComparePc(site)) ||
                HasOtherWordRewrite(site.Begin,false) || (site.RightLoad is {} loadPc && HasOtherWordRewrite(loadPc,false)))
                throw new InvalidDataException("Invalid or overlapping word-array comparison.");
            if (site.RightLoad is { } right)
            {
                if (right <= site.Begin || right >= WordComparePc(site) || right % 4 != 0 ||
                    site.RightBase is null || !IsGpr(site.RightBase) || site.RightBase == "r0" ||
                    site.RightValue is null || !IsGpr(site.RightValue) || site.RightValue is "r1" or "r2" || site.RightOffset is null ||
                    site.RightValue == site.LeftValue || site.RightBase == site.LeftValue ||
                    site.LeftBase == site.RightValue || site.RightBase == site.RightValue)
                    throw new InvalidDataException("Invalid word-array comparison operands.");
            }
            else if (site.RightBase is not null || site.RightOffset is not null || site.RightValue is not null)
                throw new InvalidDataException("Zero-array comparisons cannot declare a second load.");
            foreach (var layout in new[] { _original, _expanded })
                foreach (var offset in new[] { site.LeftOffset, site.RightOffset }.OfType<string>())
                    if (layout.Symbol(offset) < 0 || layout.Symbol(offset) % 4 != 0)
                        throw new InvalidDataException("Word-array comparisons need aligned offsets.");
        }
        foreach (var table in _wordTables)
        {
            if (table.Address % 4 != 0 || table.ExpectedValues.Count is < 1 or > 65535 || table.Loads.Count == 0 || table.Entries.Count == 0 || (ulong)table.Address+(ulong)table.ExpectedValues.Count*4>0x100000000ul ||
                table.Entries.Select(x => x.Index).Distinct().Count() != table.Entries.Count)
                throw new InvalidDataException("Invalid symbolic word table.");
            foreach (var entry in table.Entries)
            {
                if (entry.Index < 0 || entry.Index >= table.ExpectedValues.Count || entry.Scale == 0 ||
                    (long)_original.Symbol(entry.Symbol) * entry.Scale + entry.Addend != table.ExpectedValues[entry.Index] ||
                    (long)_expanded.Symbol(entry.Symbol) * entry.Scale + entry.Addend is < 0 or > uint.MaxValue)
                    throw new InvalidDataException("Symbolic word-table entry does not match its authored value.");
            }
            foreach (var load in table.Loads)
                if (load.Instruction % 4 != 0 || !IsGpr(load.Base) || load.Base == "r0" || !IsGpr(load.Destination) ||
                    (load.Index is not null && (!IsGpr(load.Index) || load.Offset != 0)) ||
                    !seen.Add(load.Instruction) || HasOtherWordRewrite(load.Instruction) ||
                    _wordComparisons.Any(site => load.Instruction>=site.Begin && load.Instruction<=WordComparePc(site)))
                    throw new InvalidDataException("Invalid or overlapping symbolic word-table load.");
        }
    }

    private static void CheckWordCompare(GuestWordComparison site, IReadOnlyList<IrInstruction> nodes)
    {
        var right = site.RightLoad is null ? IrValue.Imm(0) : IrValue.Register(site.RightValue!);
        if (nodes.Count != 1 || nodes[0] != new IrSetCrField(site.Field, IrValue.Register(site.LeftValue), right, site.IsUnsigned))
            throw new InvalidDataException($"Unproven word-array compare at 0x{WordComparePc(site):X8}.");
    }

    private void ValidateWordArrayImage(ProgramImage image)
    {
        foreach (var site in _wordComparisons)
            for (var i = 0; i < site.ExpectedWords.Count; i++)
            {
                var pc = checked(site.Begin + (uint)i * 4);
                var word = BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(pc, 4), 4));
                var instruction = PpcDecoder.Decode(pc, word);
                var nodes = new PpcLifter().Lift(new[] { instruction })[0].Ir;
                if (word != site.ExpectedWords[i]) throw new InvalidDataException($"Word-array comparison word mismatch at 0x{pc:X8}.");
                if (pc == WordComparePc(site)) { CheckWordCompare(site, nodes); continue; }
                if (pc == site.Begin || pc == site.RightLoad)
                {
                    bool left = pc == site.Begin;
                    var expected = new IrLoad(left ? site.LeftValue : site.RightValue!,
                        new(left ? site.LeftBase : site.RightBase!, _original.Symbol(left ? site.LeftOffset : site.RightOffset!)), 4);
                    if (nodes.Count != 1 || nodes[0] != expected) throw new InvalidDataException($"Unproven word-array source load at 0x{pc:X8}.");
                    continue;
                }
                // Retained CR tests and CR-only skip branches cannot change a source base or value.
                var bo = (word >> 21) & 31;
                bool branch = instruction.IsConditionalBranch && !instruction.IsCall && (word & 3) == 0 &&
                    (bo & 4) != 0 && (bo & 16) == 0 && instruction.BranchTargets.All(target => target < site.Begin || target > WordComparePc(site));
                if (nodes.Count != 1 || !(nodes[0] is IrSetCrField || branch && nodes[0] is IrBranch))
                    throw new InvalidDataException($"Word-array compare span changes protected state at 0x{pc:X8}.");
            }
        foreach (var table in _wordTables)
        {
            for (var i = 0; i < table.ExpectedValues.Count; i++)
            {
                var pc = checked(table.Address + (uint)i * 4);
                if (BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(pc, 4), 4)) != table.ExpectedValues[i])
                    throw new InvalidDataException($"Symbolic word-table data mismatch at 0x{pc:X8}.");
            }
            foreach (var load in table.Loads)
            {
                var word = BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(load.Instruction, 4), 4));
                CheckWordTableLoad(load, word, new PpcLifter().Lift(new[] { PpcDecoder.Decode(load.Instruction, word) })[0].Ir);
            }
        }
    }

    private static void CheckWordTableLoad(GuestWordTableLoad site, uint word, IReadOnlyList<IrInstruction> nodes)
    {
        if (word != site.ExpectedWord) throw new InvalidDataException("Symbolic word-table instruction mismatch.");
        if (site.Index is null)
        {
            if (nodes.Count != 1 || nodes[0] != new IrLoad(site.Destination, new(site.Base, site.Offset), 4))
                throw new InvalidDataException("Unproven symbolic word-table load.");
        }
        else if (nodes.Count != 2 || nodes[0] is not IrBinary { Op: "add", Destination: var address,
                Left.RegisterName: var left, Right.RegisterName: var index } || left != site.Base || index != site.Index ||
            nodes[1] != new IrLoad(site.Destination, new(address, 0), 4))
            throw new InvalidDataException("Unproven indexed symbolic word-table load.");
    }

    private IReadOnlyList<IrInstruction>? ApplyWordArray(PpcInstruction instruction, IReadOnlyList<IrInstruction> nodes)
    {
        var converted = ApplyCountConversion(instruction,nodes);
        if(converted is not null)return converted;
        var comparison = _wordComparisons.FirstOrDefault(site => WordComparePc(site) == instruction.Address);
        if (comparison is not null)
        {
            if (instruction.Word != comparison.ExpectedWords[^1]) throw new InvalidDataException("Word-array comparison instruction mismatch.");
            CheckWordCompare(comparison, nodes);
            if (Players == OriginalPlayers) return nodes;
            var result = $"addr_layout_word_compare_{instruction.Address:X8}_loc";
            return new IrInstruction[] {
                new IrCall(result, WordCompareHelper(comparison), comparison.RightLoad is null
                    ? new[] { IrValue.Register(comparison.LeftBase) }
                    : new[] { IrValue.Register(comparison.LeftBase), IrValue.Register(comparison.RightBase!) }),
                new IrSetCrField(comparison.Field, IrValue.Register(result), IrValue.Imm(0), false)
            };
        }
        foreach (var table in _wordTables)
            foreach (var load in table.Loads)
            {
                if (load.Instruction != instruction.Address) continue;
                CheckWordTableLoad(load, instruction.Word, nodes);
                if (Players == OriginalPlayers) return nodes;
                if (load.Index is null)
                {
                    var address = $"addr_layout_word_table_{instruction.Address:X8}_loc";
                    return new IrInstruction[] { new IrBinary(address, IrValue.Register(load.Base), IrValue.Imm(load.Offset), "add"),
                        new IrCall(load.Destination, WordTableHelper(load), new[] { IrValue.Register(address) }) };
                }
                return new[] { nodes[0], new IrCall(load.Destination, WordTableHelper(load),
                    new[] { IrValue.Register(((IrBinary)nodes[0]).Destination) }) };
            }
        return null;
    }

    private void ValidateWordCompareControlFlow(IReadOnlyList<PpcInstruction> instructions)
    {
        foreach (var site in _wordComparisons)
        {
            var end = WordComparePc(site) + 4;
            var count = instructions.Count(x => x.Address >= site.Begin && x.Address < end);
            if (count == 0) continue;
            if (count != site.ExpectedWords.Count || instructions.Where(x => x.Address < site.Begin || x.Address >= end)
                    .Any(x => x.BranchTargets.Any(target => target > site.Begin && target < end)))
                throw new InvalidDataException($"Partial or interior entry into word-array comparison at 0x{site.Begin:X8}.");
        }
    }

    internal string EmitWordArrayHelpers(IrFunction function)
    {
        var calls = function.Blocks.SelectMany(b => b.Instructions).OfType<IrCall>().Select(c => c.Target).ToHashSet(StringComparer.Ordinal);
        var output = new StringBuilder();
        foreach (var site in _wordComparisons)
        {
            var helper = WordCompareHelper(site); if (!calls.Contains(helper)) continue;
            var right = site.RightLoad is null ? "0u" : $"Memory::Read32(right+{_expanded.Symbol(site.RightOffset!)}u+i*4u)";
            output.AppendLine($$"""
                #ifndef WIICOMPILED_{{helper}}
                #define WIICOMPILED_{{helper}}
                #include "guest_abi/guest_abi.h"
                static inline uint32_t {{helper}}(uint32_t left{{(site.RightLoad is null ? "" : ", uint32_t right")}}) {
                    for (uint32_t i=0;i<{{_expanded.Symbol(site.Count)}}u;i++) {
                        const uint32_t a=Memory::Read32(left+{{_expanded.Symbol(site.LeftOffset)}}u+i*4u),b={{right}};
                        if(a!=b)return {{(site.IsUnsigned ? "a<b" : "int32_t(a)<int32_t(b)")}} ? 0xffffffffu : 1u;
                    }
                    return 0;
                }
                #endif
                """);
        }
        foreach (var table in _wordTables)
            foreach (var load in table.Loads)
            {
                var helper = WordTableHelper(load); if (!calls.Contains(helper)) continue;
                var values = table.ExpectedValues.ToArray();
                foreach (var entry in table.Entries) values[entry.Index] = checked((uint)(_expanded.Symbol(entry.Symbol) * (long)entry.Scale + entry.Addend));
                output.AppendLine($$"""
                    #ifndef WIICOMPILED_{{helper}}
                    #define WIICOMPILED_{{helper}}
                    #include "guest_abi/guest_abi.h"
                    static inline uint32_t {{helper}}(uint32_t address) {
                        const uint32_t offset=address-{{table.Address}}u;
                        if((offset&3u)==0 && offset/4u<{{values.Length}}u) {
                            static constexpr uint32_t values[]={ {{string.Join(",",values.Select(x => x+"u"))}} };
                            return values[offset/4u];
                        }
                        return Memory::Read32(address);
                    }
                    #endif
                    """);
            }
        EmitCountConversionHelpers(calls,output);
        return output.ToString();
    }
}
