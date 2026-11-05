using System.Buffers.Binary;
using System.Text;
using Translator.Core.Disassembly;
using Translator.Core.Ir;
using Translator.Core.Lifting;
using Translator.Core.Loading;

namespace Translator.Core.Translation;

public sealed record GuestGridLookup(uint Instruction, uint ExpectedWord, string Base, string Index,
    string Destination, string CountOffset, string? Position = null);
public sealed record GuestGridTable(uint Address, byte[] ExpectedValues, bool AdvanceRows,
    IReadOnlyList<GuestGridLookup> Lookups);

public sealed partial class GuestLayoutRelocations
{
    private readonly GuestGridTable[] _gridTables;
    private static string GridHelper(GuestGridLookup site) => $"layout_grid_{site.Instruction:X8}";

    private void ValidateGridDefinitions(GuestLayoutReference[] references)
    {
        var seen = new HashSet<uint>();
        foreach (var table in _gridTables)
        {
            if (table.ExpectedValues.Length != checked(OriginalPlayers * OriginalPlayers) || table.Lookups.Count == 0 || Players < OriginalPlayers)
                throw new InvalidDataException("Grid tables require a complete authored square and expanding capacity.");
            // Unused cells in every authored field-size row must remain inactive.
            for (var count = 1; count <= OriginalPlayers; count++)
                for (var index = count; index < OriginalPlayers; index++)
                    if (table.ExpectedValues[(count - 1) * OriginalPlayers + index] != 0)
                        throw new InvalidDataException("Grid table has active cells outside an authored field size.");
            var terminal = table.ExpectedValues.AsSpan((OriginalPlayers - 1) * OriginalPlayers);
            if (table.AdvanceRows && (terminal[0] != 0 || terminal.ToArray().Zip(terminal.ToArray().Skip(1)).Any(pair => pair.Second < pair.First || pair.Second > pair.First + 1)))
                throw new InvalidDataException("An advancing grid requires consecutive nondecreasing row indices.");
            if (table.AdvanceRows && terminal[^1] + (Players - 1) / OriginalPlayers * (terminal[^1] + 1) > sbyte.MaxValue)
                throw new InvalidDataException("Expanded grid rows exceed the original signed-byte consumer range.");
            foreach (var site in table.Lookups)
            {
                if (site.Instruction % 4 != 0 || !Gpr(site.Base) || !Gpr(site.Index) || !Gpr(site.Destination) ||
                    !Gpr(site.CountOffset) || (site.Position is not null && !Gpr(site.Position)) || !seen.Add(site.Instruction) ||
                    references.Any(reference => reference.Instruction == site.Instruction) ||
                    _arrayCopies.Any(copy => site.Instruction >= copy.Begin && site.Instruction < CopyEnd(copy)) ||
                    _arrayFills.Any(fill => site.Instruction >= fill.Begin && site.Instruction < FillEnd(fill)) ||
                    _traversalMarkers.Any(marker => site.Instruction >= marker.Begin && site.Instruction < MarkerEnd(marker)) ||
                    _resampledCursors.Any(cursor => cursor.Instruction == site.Instruction) ||
                    _boundedAccesses.Any(bound => bound.Instruction == site.Instruction) ||
                    _capacityTables.Any(curve => curve.Lookups.Any(lookup => lookup.Instruction == site.Instruction)) ||
                    _rankTables.Any(rank => rank.Lookups.Any(lookup => lookup.Instruction == site.Instruction)))
                    throw new InvalidDataException("Invalid or overlapping starting-grid lookup.");
            }
        }
    }

    private static void CheckGridLoad(GuestGridLookup site, PpcInstruction instruction, IReadOnlyList<IrInstruction> nodes)
    {
        if (instruction.Word != site.ExpectedWord || nodes.Count != 2 ||
            nodes[0] is not IrBinary { Op: "add", Destination: var address, Left.RegisterName: var left, Right.RegisterName: var right } ||
            left != site.Base || right != site.Index ||
            nodes[1] is not IrLoad { SizeBytes: 1, Destination: var destination, Address.Base: var basis, Address.Offset: 0 } ||
            destination != site.Destination || basis != address)
            throw new InvalidDataException($"Unproven grid lookup at 0x{site.Instruction:X8}.");
    }

    private void ValidateGridTables(ProgramImage image)
    {
        foreach (var table in _gridTables)
        {
            if (!image.Memory.AsSpan(image.GetOffset(table.Address, table.ExpectedValues.Length), table.ExpectedValues.Length).SequenceEqual(table.ExpectedValues))
                throw new InvalidDataException($"Grid table mismatch at 0x{table.Address:X8}.");
            foreach (var site in table.Lookups)
            {
                var word = BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(site.Instruction, 4), 4));
                var instruction = PpcDecoder.Decode(site.Instruction, word);
                CheckGridLoad(site, instruction, new PpcLifter().Lift(new[] { instruction })[0].Ir);
            }
        }
    }

    private IReadOnlyList<IrInstruction>? ApplyGridLookup(PpcInstruction instruction, IReadOnlyList<IrInstruction> nodes)
    {
        foreach (var table in _gridTables)
        foreach (var site in table.Lookups)
        {
            if (instruction.Address != site.Instruction) continue;
            CheckGridLoad(site, instruction, nodes);
            if (Players == OriginalPlayers) return nodes;
            return new IrInstruction[] { nodes[0], new IrCall(site.Destination, GridHelper(site), new[] {
                IrValue.Register(site.CountOffset), site.Position is null ? IrValue.Imm(0) : IrValue.Register(site.Position) }) };
        }
        return null;
    }

    public uint GridCoordinate(int tableIndex, int count, int position)
    {
        if (count < 1 || count > Players || position < 0 || position >= count) return 0;
        var table = _gridTables[tableIndex];
        if (count <= OriginalPlayers) return table.ExpectedValues[(count - 1) * OriginalPlayers + position];
        var last = (OriginalPlayers - 1) * OriginalPlayers;
        return checked((uint)(table.ExpectedValues[last + position % OriginalPlayers] +
            (table.AdvanceRows ? position / OriginalPlayers * (table.ExpectedValues[^1] + 1) : 0)));
    }

    internal string EmitGridHelpers(IrFunction function)
    {
        var calls = function.Blocks.SelectMany(block => block.Instructions).OfType<IrCall>().Select(call => call.Target).ToHashSet(StringComparer.Ordinal);
        var source = new StringBuilder();
        foreach (var table in _gridTables)
        foreach (var site in table.Lookups)
        {
            var helper = GridHelper(site);
            if (!calls.Contains(helper)) continue;
            var advance = table.AdvanceRows ? $" + (position / {OriginalPlayers}u) * {table.ExpectedValues[^1] + 1}u" : "";
            source.AppendLine($$"""
                #ifndef WIICOMPILED_LAYOUT_GRID_{{site.Instruction:X8}}
                #define WIICOMPILED_LAYOUT_GRID_{{site.Instruction:X8}}
                #include <cstdint>
                static inline uint32_t {{helper}}(uint32_t rowOffset, uint32_t position) {
                    if (rowOffset % {{OriginalPlayers}}u || rowOffset / {{OriginalPlayers}}u >= {{Players}}u) return 0;
                    const uint32_t count = rowOffset / {{OriginalPlayers}}u + 1u;
                    if (position >= count) return 0;
                    static constexpr uint8_t original[] = { {{string.Join(", ", table.ExpectedValues)}} };
                    if (count <= {{OriginalPlayers}}u) return original[rowOffset + position];
                    return original[{{(OriginalPlayers - 1) * OriginalPlayers}}u + position % {{OriginalPlayers}}u]{{advance}};
                }
                #endif
                """);
        }
        return source.ToString();
    }
}
