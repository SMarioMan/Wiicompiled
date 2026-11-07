using System.Buffers.Binary;
using Translator.Core.Analysis.BasicBlocks;
using Translator.Core.Disassembly;
using Translator.Core.Ir;
using Translator.Core.Lifting;
using Translator.Core.Loading;

namespace Translator.Core.Translation;

// Expand a proven scalar copy into an array transfer. Safe
// address setup between the load and store retains its original register effects.
public sealed record GuestArrayTransfer(uint Begin, IReadOnlyList<uint> ExpectedWords,
    string SourceBase, string SourceOffset, string DestinationBase, string DestinationOffset,
    string Value, string Stride, string Count, int Width = 4, bool AllowObjectSource = false);

public sealed partial class GuestLayoutRelocations
{
    private readonly GuestArrayTransfer[] _arrayTransfers;
    private static uint TransferEnd(GuestArrayTransfer site) => checked(site.Begin + (uint)site.ExpectedWords.Count * 4);

    private void ValidateTransferDefinitions(GuestLayoutReference[] references)
    {
        foreach (var site in _arrayTransfers)
        {
            var registers = new[] { site.SourceBase, site.DestinationBase, site.Value };
            if (site.Begin % 4 != 0 || site.ExpectedWords.Count is < 2 or > 16 || (!site.AllowObjectSource && site.SourceBase != "r1") || site.SourceBase is "r0" or "r2" ||
                registers.Any(register => !IsGpr(register)) || registers.Distinct().Count() != 3 ||
                site.DestinationBase is "r0" or "r2" || site.Width is not (2 or 4) ||
                _original.Symbol(site.Count) != 1 || _expanded.Symbol(site.Count) < 1 || Players < OriginalPlayers ||
                new[] { _original, _expanded }.Any(layout => layout.Symbol(site.Stride) < site.Width ||
                    layout.Symbol(site.SourceOffset) < 0 || layout.Symbol(site.DestinationOffset) < 0))
                throw new InvalidDataException("Invalid scalar-to-array transfer.");
            var end = TransferEnd(site);
            bool Inside(uint pc) => site.Begin <= pc && pc < end;
            bool Overlap(uint begin, uint otherEnd) => begin < end && otherEnd > site.Begin;
            // Middle address operands can have their own checked layout references.
            if (references.Any(reference => reference.Instruction == site.Begin || reference.Instruction == end - 4) ||
                _arrayTransfers.Count(other => Overlap(other.Begin, TransferEnd(other))) != 1 ||
                _arrayCopies.Any(copy => Overlap(copy.Begin, CopyEnd(copy))) ||
                _arrayFills.Any(fill => Overlap(fill.Begin, FillEnd(fill))) ||
                _bitsetStores.Any(store => Overlap(store.Begin, BitsetEnd(store))) ||
                _bitsetLoads.Any(load => Inside(load.Instruction)) || _boundedAccesses.Any(bound => Inside(bound.Instruction)) ||
                _rankTables.Any(table => table.Lookups.Any(lookup => Inside(lookup.Instruction))) ||
                _capacityTables.Any(table => table.Lookups.Any(lookup => Inside(lookup.Instruction))) ||
                _gridTables.Any(table => table.Lookups.Any(lookup => Inside(lookup.Instruction))) ||
                _normalizedRanks.Any(rank => Inside(rank.Instruction)) || _resampledCursors.Any(cursor => Inside(cursor.Instruction)) ||
                _authoredCountIndices.Any(index => Inside(index.Instruction)) ||
                _traversalMarkers.Any(marker => Overlap(marker.Begin, MarkerEnd(marker))) ||
                _heapRoutes.Any(route => Overlap(route.Begin, HeapRouteEnd(route))))
                throw new InvalidDataException("Overlapping scalar-to-array transfer.");
        }
    }

    private void ValidateTransfers(ProgramImage image)
    {
        foreach (var site in _arrayTransfers)
        {
            for (var index = 0; index < site.ExpectedWords.Count; index++)
            {
                var pc = site.Begin + (uint)index * 4;
                var word = BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(pc, 4), 4));
                var nodes = new PpcLifter().Lift(new[] { PpcDecoder.Decode(pc, word) })[0].Ir;
                if (word != site.ExpectedWords[index] || nodes.Count != 1)
                    throw new InvalidDataException($"Array-transfer instruction mismatch at 0x{pc:X8}.");
                var valid = index == 0
                    ? nodes[0] == new IrLoad(site.Value, new(site.SourceBase, _original.Symbol(site.SourceOffset)), site.Width)
                    : index == site.ExpectedWords.Count - 1
                        ? nodes[0] == new IrStore(new(site.DestinationBase, _original.Symbol(site.DestinationOffset)), IrValue.Register(site.Value), site.Width)
                        : nodes[0] is IrBinary { Op: "add", Right.Constant: >= 0 } address &&
                            address.Left.RegisterName == site.SourceBase && IsGpr(address.Destination) &&
                            address.Destination != site.SourceBase && address.Destination != site.DestinationBase &&
                            address.Destination != site.Value && address.Destination is not "r2";
                if (!valid) throw new InvalidDataException($"Unproven scalar-to-array transfer at 0x{pc:X8}.");
            }
        }
    }

    private IReadOnlyList<IrInstruction>? ApplyTransfer(PpcInstruction instruction, IReadOnlyList<IrInstruction> nodes)
    {
        var site = _arrayTransfers.FirstOrDefault(candidate => instruction.Address >= candidate.Begin && instruction.Address < TransferEnd(candidate));
        if (site is null) return null;
        if (instruction.Word != site.ExpectedWords[(int)(instruction.Address - site.Begin) / 4])
            throw new InvalidDataException("Array-transfer instruction mismatch.");
        if (Players == OriginalPlayers) return null;
        if (instruction.Address == site.Begin)
            return new IrInstruction[] { new IrLoad(site.Value, new(site.SourceBase, _expanded.Symbol(site.SourceOffset)), site.Width) };
        if (instruction.Address != TransferEnd(site) - 4) return null;
        var result = new List<IrInstruction>();
        for (var index = 0; index < _expanded.Symbol(site.Count); index++)
        {
            var value = $"addr_layout_transfer_{site.Begin:X8}_{index}_loc";
            var offset = checked(index * _expanded.Symbol(site.Stride));
            result.Add(new IrLoad(value, new(site.SourceBase, checked(_expanded.Symbol(site.SourceOffset) + offset)), site.Width));
        }
        // Snapshot all words first so overlapping source/destination regions
        // retain transfer semantics instead of repeatedly reading our own writes.
        for (var index = 0; index < _expanded.Symbol(site.Count); index++)
        {
            var value = $"addr_layout_transfer_{site.Begin:X8}_{index}_loc";
            var offset = checked(index * _expanded.Symbol(site.Stride));
            result.Add(new IrStore(new(site.DestinationBase, checked(_expanded.Symbol(site.DestinationOffset) + offset)), IrValue.Register(value), site.Width));
        }
        return result;
    }

    private void ValidateTransferControlFlow(IReadOnlyList<PpcInstruction> instructions, IReadOnlyList<BasicBlock> blocks)
    {
        foreach (var site in _arrayTransfers)
        {
            var end = TransferEnd(site);
            var inside = instructions.Count(instruction => instruction.Address >= site.Begin && instruction.Address < end);
            if (inside != 0 && (inside != site.ExpectedWords.Count || blocks.Any(block => block.StartAddress > site.Begin && block.StartAddress < end)))
                throw new InvalidDataException($"Array-transfer span is partial or has an interior entry at 0x{site.Begin:X8}.");
        }
    }
}
