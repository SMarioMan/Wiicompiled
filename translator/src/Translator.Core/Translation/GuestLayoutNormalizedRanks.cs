using System.Buffers.Binary;
using Translator.Core.Disassembly;
using Translator.Core.Ir;
using Translator.Core.Lifting;
using Translator.Core.Loading;

namespace Translator.Core.Translation;

// Converts a one-based runtime rank to an index on an unchanged authored axis.
// The caller must audit that the rank lies in 1..Count; the checked instruction
// is the original conversion to a zero-based rank before its bucket arithmetic.
public sealed record GuestNormalizedRank(uint Instruction, uint ExpectedWord, string Register, string Count, int SourceCount);

public sealed partial class GuestLayoutRelocations
{
    private readonly GuestNormalizedRank[] _normalizedRanks;

    private void ValidateNormalizedRankDefinitions(GuestLayoutReference[] references)
    {
        if (_normalizedRanks.Select(site => site.Instruction).Distinct().Count() != _normalizedRanks.Length)
            throw new InvalidDataException("Duplicate normalized-rank site.");
        foreach (var site in _normalizedRanks)
        {
            if (site.Instruction % 4 != 0 || !IsGpr(site.Register) || site.Register is "r1" or "r2" ||
                site.SourceCount is < 2 or > 65535 || _original.Symbol(site.Count) != site.SourceCount ||
                _expanded.Symbol(site.Count) < site.SourceCount || _expanded.Symbol(site.Count) > 65535)
                throw new InvalidDataException("Invalid normalized-rank dimensions or register.");
            if (references.Any(reference => reference.Instruction == site.Instruction) ||
                _arrayCopies.Any(copy => site.Instruction >= copy.Begin && site.Instruction < CopyEnd(copy)) ||
                _arrayFills.Any(fill => site.Instruction >= fill.Begin && site.Instruction < FillEnd(fill)) ||
                _rankTables.Any(table => table.Lookups.Any(lookup => lookup.Instruction == site.Instruction)) ||
                _boundedAccesses.Any(bound => bound.Instruction == site.Instruction) ||
                _resampledCursors.Any(cursor => cursor.Instruction == site.Instruction) ||
                _traversalMarkers.Any(marker => site.Instruction >= marker.Begin && site.Instruction < MarkerEnd(marker)) ||
                _capacityTables.Any(table => table.Lookups.Any(lookup => lookup.Instruction == site.Instruction)) ||
                _gridTables.Any(table => table.Lookups.Any(lookup => lookup.Instruction == site.Instruction)))
                throw new InvalidDataException("Overlapping normalized-rank relocation.");
        }
    }

    private static void CheckNormalizedRank(GuestNormalizedRank site, PpcInstruction instruction, IReadOnlyList<IrInstruction> nodes)
    {
        if (instruction.Word != site.ExpectedWord || nodes.Count != 1 || nodes[0] is not IrBinary {
                Op: "add", Destination: var destination, Left.RegisterName: var source, Right.Constant: -1 } ||
            destination != site.Register || source != site.Register)
            throw new InvalidDataException($"Unproven zero-based rank conversion at 0x{site.Instruction:X8}.");
    }

    private void ValidateNormalizedRanks(ProgramImage image)
    {
        foreach (var site in _normalizedRanks)
        {
            var word = BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(site.Instruction, 4), 4));
            var instruction = PpcDecoder.Decode(site.Instruction, word);
            CheckNormalizedRank(site, instruction, new PpcLifter().Lift(new[] { instruction })[0].Ir);
        }
    }

    private IReadOnlyList<IrInstruction>? ApplyNormalizedRank(PpcInstruction instruction, IReadOnlyList<IrInstruction> nodes)
    {
        var site = _normalizedRanks.FirstOrDefault(candidate => candidate.Instruction == instruction.Address);
        if (site is null) return null;
        CheckNormalizedRank(site, instruction, nodes);
        var count = _expanded.Symbol(site.Count);
        if (count == site.SourceCount) return nodes;
        // At most 65535 valid runtime ranks and source positions: this product
        // fits in an unsigned guest word. Preserve the following bucket logic,
        // including its original carry operations, rather than changing it.
        return nodes.Concat(new IrInstruction[] {
            new IrBinary(site.Register, IrValue.Register(site.Register), IrValue.Imm(site.SourceCount), "mul"),
            new IrBinary(site.Register, IrValue.Register(site.Register), IrValue.Imm(count), "divu")
        }).ToArray();
    }
}
