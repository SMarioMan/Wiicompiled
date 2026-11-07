using System.Buffers.Binary;
using Translator.Core.Disassembly;
using Translator.Core.Ir;
using Translator.Core.Lifting;
using Translator.Core.Loading;

namespace Translator.Core.Translation;

// Normalize an audited bit index while reading its containing packed word.
// The caller retains its original mask/extraction operations after this load.
public sealed record GuestBitsetLoad(uint Instruction, uint ExpectedWord, int Node,
    string Base, string Index, string Destination, string Offset, string Count,
    int WordBits = 16, int WordBytes = 2, int BitsPerPlayer = 1);

public sealed partial class GuestLayoutRelocations
{
    private readonly GuestBitsetLoad[] _bitsetLoads;

    private void ValidateBitsetLoadDefinitions(GuestLayoutReference[] references)
    {
        if (_bitsetLoads.Select(site => site.Instruction).Distinct().Count() != _bitsetLoads.Length)
            throw new InvalidDataException("Duplicate packed-word load.");
        foreach (var site in _bitsetLoads)
        {
            var registers = new[] { site.Base, site.Index, site.Destination };
            if (site.Instruction % 4 != 0 || site.Node != 0 || registers.Distinct().Count() != 3 ||
                registers.Any(register => !IsGpr(register) || register is "r1" or "r2") ||
                site.WordBytes is not (2 or 4) || site.WordBits <= 0 || site.WordBits > site.WordBytes * 8 ||
                site.BitsPerPlayer <= 0 || site.BitsPerPlayer > 32 ||
                (long)OriginalPlayers * site.BitsPerPlayer > site.WordBits || Players < OriginalPlayers ||
                _original.Symbol(site.Count) != 1 ||
                _expanded.Symbol(site.Count) != ((long)Players * site.BitsPerPlayer + site.WordBits - 1) / site.WordBits ||
                new[] { _original, _expanded }.Any(layout => layout.Symbol(site.Offset) < 0 || layout.Symbol(site.Offset) % site.WordBytes != 0))
                throw new InvalidDataException("Invalid packed-word load shape.");
            bool At(uint pc) => pc == site.Instruction;
            bool Inside(uint begin, uint end) => begin <= site.Instruction && site.Instruction < end;
            if (references.Any(reference => At(reference.Instruction)) ||
                _arrayCopies.Any(copy => Inside(copy.Begin, CopyEnd(copy))) ||
                _arrayFills.Any(fill => Inside(fill.Begin, FillEnd(fill))) ||
                _bitsetStores.Any(store => Inside(store.Begin, BitsetEnd(store))) ||
                _arrayTransfers.Any(transfer => Inside(transfer.Begin, TransferEnd(transfer))) ||
                _boundedAccesses.Any(bound => At(bound.Instruction)) ||
                _rankTables.Any(table => table.Lookups.Any(lookup => At(lookup.Instruction))) ||
                _capacityTables.Any(table => table.Lookups.Any(lookup => At(lookup.Instruction))) ||
                _gridTables.Any(table => table.Lookups.Any(lookup => At(lookup.Instruction))) ||
                _normalizedRanks.Any(rank => At(rank.Instruction)) || _resampledCursors.Any(cursor => At(cursor.Instruction)) ||
                _authoredCountIndices.Any(index => At(index.Instruction)) ||
                _traversalMarkers.Any(marker => Inside(marker.Begin, MarkerEnd(marker))) ||
                _heapRoutes.Any(route => Inside(route.Begin, HeapRouteEnd(route))))
                throw new InvalidDataException("Overlapping packed-word load.");
        }
    }

    private void ValidateBitsetLoads(ProgramImage image)
    {
        foreach (var site in _bitsetLoads)
        {
            var word = BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(site.Instruction, 4), 4));
            var nodes = new PpcLifter().Lift(new[] { PpcDecoder.Decode(site.Instruction, word) })[0].Ir;
            if (word != site.ExpectedWord || nodes.Count != 1 ||
                nodes[site.Node] != new IrLoad(site.Destination, new(site.Base, _original.Symbol(site.Offset)), site.WordBytes))
                throw new InvalidDataException($"Unproven packed-word load at 0x{site.Instruction:X8}.");
        }
    }

    private IReadOnlyList<IrInstruction>? ApplyBitsetLoad(PpcInstruction instruction, IReadOnlyList<IrInstruction> nodes)
    {
        var site = _bitsetLoads.FirstOrDefault(candidate => candidate.Instruction == instruction.Address);
        if (site is null) return null;
        if (instruction.Word != site.ExpectedWord) throw new InvalidDataException("Packed-word load instruction mismatch.");
        if (Players == OriginalPlayers) return nodes;
        var prefix = $"addr_layout_bitload_{site.Instruction:X8}_";
        string Name(string suffix) => prefix + suffix + "_loc";
        IrValue V(string suffix) => IrValue.Register(Name(suffix));
        var replacement = new IrInstruction[] {
            new IrBinary(Name("word"),IrValue.Register(site.Index),IrValue.Imm(site.WordBits),"divu"),
            new IrBinary(Name("bytes"),V("word"),IrValue.Imm(site.WordBytes),"mul"),
            new IrBinary(Name("address"),IrValue.Register(site.Base),V("bytes"),"add"),
            new IrBinary(Name("first"),V("word"),IrValue.Imm(site.WordBits),"mul"),
            new IrBinary(site.Index,IrValue.Register(site.Index),V("first"),"sub"),
            new IrLoad(site.Destination,new(Name("address"),_expanded.Symbol(site.Offset)),site.WordBytes)
        };
        return nodes.Take(site.Node).Concat(replacement).Concat(nodes.Skip(site.Node + 1)).ToArray();
    }
}
