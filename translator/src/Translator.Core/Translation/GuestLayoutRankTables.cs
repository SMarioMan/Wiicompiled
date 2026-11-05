using System.Buffers.Binary;
using System.Text;
using Translator.Core.Disassembly;
using Translator.Core.Ir;
using Translator.Core.Lifting;
using Translator.Core.Loading;

namespace Translator.Core.Translation;

public sealed record GuestRankLookup(uint Instruction, uint ExpectedWord, int Node, string AddressBase,
    int AddressOffset, string Destination, string Players, string Position);
public sealed record GuestRankTable(uint Address, byte[] ExpectedValues, IReadOnlyList<int> Curve,
    IReadOnlyList<GuestRankLookup> Lookups);

public sealed partial class GuestLayoutRelocations
{
    private readonly GuestRankTable[] _rankTables;
    private static string RankHelper(GuestRankLookup site) => $"layout_rank_{site.Instruction:X8}";
    private static bool Gpr(string value) => value.Length >= 2 && value[0] == 'r' &&
        int.TryParse(value.AsSpan(1), out var index) && index is >= 0 and < 32 && value == $"r{index}";

    private void ValidateRankDefinitions(GuestLayoutReference[] references)
    {
        var seen = new HashSet<uint>();
        foreach (var table in _rankTables)
        {
            if (table.ExpectedValues.Length != checked(OriginalPlayers*OriginalPlayers) ||
                table.Curve.Count != OriginalPlayers || table.Curve.Any(value => value < 0 || value > 255) ||
                table.Curve.Zip(table.Curve.Skip(1)).Any(pair => pair.First < pair.Second))
                throw new InvalidDataException("Invalid guest rank table or nonmonotonic extension curve.");
            foreach (var site in table.Lookups)
                if (site.Instruction % 4 != 0 || site.Node < 0 || !Gpr(site.AddressBase) || !Gpr(site.Destination) ||
                    !Gpr(site.Players) || !Gpr(site.Position) || !seen.Add(site.Instruction) ||
                    references.Any(reference => reference.Instruction == site.Instruction) ||
                    _arrayCopies.Any(copy => site.Instruction >= copy.Begin && site.Instruction < CopyEnd(copy)))
                    throw new InvalidDataException("Invalid or overlapping guest rank lookup.");
        }
    }

    private void ValidateRankTables(ProgramImage image)
    {
        foreach (var table in _rankTables)
        {
            if (!image.Memory.AsSpan(image.GetOffset(table.Address, table.ExpectedValues.Length), table.ExpectedValues.Length)
                .SequenceEqual(table.ExpectedValues))
                throw new InvalidDataException($"Guest rank table mismatch at 0x{table.Address:X8}.");
            foreach (var site in table.Lookups)
            {
                var word = BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(site.Instruction, 4), 4));
                if (word != site.ExpectedWord) throw RankMismatch(site);
                var nodes = new PpcLifter().Lift(new[] { PpcDecoder.Decode(site.Instruction, word) })[0].Ir;
                CheckRankLoad(site, nodes);
            }
        }
    }

    private static InvalidDataException RankMismatch(GuestRankLookup site) =>
        new($"Guest rank lookup instruction/operand mismatch at 0x{site.Instruction:X8}.");

    private static void CheckRankLoad(GuestRankLookup site, IReadOnlyList<IrInstruction> nodes)
    {
        if (site.Node >= nodes.Count || nodes[site.Node] is not IrLoad load || load.SizeBytes != 1 ||
            load.Destination != site.Destination || load.Address.Base != site.AddressBase ||
            load.Address.Offset != site.AddressOffset) throw RankMismatch(site);
    }

    private IReadOnlyList<IrInstruction>? ApplyRankLookup(PpcInstruction origin, IReadOnlyList<IrInstruction> nodes)
    {
        foreach (var table in _rankTables)
            foreach (var site in table.Lookups)
            {
                if (origin.Address != site.Instruction) continue;
                if (origin.Word != site.ExpectedWord) throw RankMismatch(site);
                CheckRankLoad(site, nodes);
                if (Players == OriginalPlayers) return nodes;
                var result = nodes.ToArray();
                result[site.Node] = new IrCall(site.Destination, RankHelper(site),
                    new[] { IrValue.Register(site.Players), IrValue.Register(site.Position) });
                return result;
            }
        return null;
    }

    // Original rows retain their exact contents. Larger fields use a single rank curve,
    // interpolated over all positions and scaled by field size. This has no fixed-size tail.
    public uint RankPoints(int tableIndex, int players, int position)
    {
        var table = _rankTables[tableIndex];
        if (players < 1 || players > Players || position < 1 || position > players) return 0;
        if (players <= OriginalPlayers) return table.ExpectedValues[(players-1)*OriginalPlayers+position-1];
        uint best = 0;
        for (var rank = position; rank <= players; rank++)
        {
            var scaledRank = (long)(rank-1)*(OriginalPlayers-1);
            var lower = (int)(scaledRank/(players-1));
            var remainder = scaledRank%(players-1);
            var upper = Math.Min(lower+1, OriginalPlayers-1);
            var weighted = table.Curve[lower]*(players-1-remainder)+table.Curve[upper]*remainder;
            var denominator = (long)(players-1)*OriginalPlayers;
            var points = checked((uint)((weighted*players+denominator/2)/denominator+rank-position));
            best = Math.Max(best, points);
        }
        return best;
    }

    internal string EmitRankHelpers(IrFunction function)
    {
        var calls = function.Blocks.SelectMany(block => block.Instructions).OfType<IrCall>()
            .Select(call => call.Target).ToHashSet(StringComparer.Ordinal);
        var source = new StringBuilder();
        foreach (var table in _rankTables)
            foreach (var site in table.Lookups)
            {
                var helper = RankHelper(site);
                if (!calls.Contains(helper)) continue;
                source.AppendLine($$"""
                    #ifndef WIICOMPILED_LAYOUT_RANK_{{site.Instruction:X8}}
                    #define WIICOMPILED_LAYOUT_RANK_{{site.Instruction:X8}}
                    #include <cstdint>
                    static inline uint32_t {{helper}}(uint32_t players, uint32_t position) {
                        if (players == 0 || players > {{Players}}u || position == 0 || position > players) return 0;
                        static constexpr uint32_t original[] = { {{string.Join(", ", table.ExpectedValues)}} };
                        static constexpr uint32_t curve[] = { {{string.Join(", ", table.Curve)}} };
                        if (players <= {{OriginalPlayers}}u) return original[(players-1)*{{OriginalPlayers}}u+position-1];
                        uint32_t best = 0;
                        for (uint32_t rankPosition = position; rankPosition <= players; rankPosition++) {
                            const uint64_t rank = uint64_t(rankPosition-1)*{{OriginalPlayers-1}}u;
                            const uint32_t lower = uint32_t(rank/(players-1));
                            const uint32_t upper = lower+1 < {{OriginalPlayers}}u ? lower+1 : lower;
                            const uint64_t remainder = rank%(players-1);
                            const uint64_t weighted = curve[lower]*(players-1-remainder)+curve[upper]*remainder;
                            const uint64_t denominator = uint64_t(players-1)*{{OriginalPlayers}}u;
                            const uint32_t points = uint32_t((weighted*players+denominator/2)/denominator)+rankPosition-position;
                            if (points > best) best = points;
                        }
                        return best;
                    }
                    #endif
                    """);
            }
        return source.ToString();
    }
}
