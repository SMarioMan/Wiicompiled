using System.Buffers.Binary;
using System.Text;
using Translator.Core.Disassembly;
using Translator.Core.Ir;
using Translator.Core.Lifting;
using Translator.Core.Loading;

namespace Translator.Core.Translation;

public sealed record GuestCapacityLookup(uint Instruction,uint ExpectedWord,string Base,string Index,string Destination,string Count);
public sealed record GuestCapacityFormula(int Bias, int Divisor);
public sealed record GuestCapacityTable(uint Address,IReadOnlyList<uint> ExpectedValues,IReadOnlyList<GuestCapacityLookup> Lookups,
    bool SignedHalfwords = false, GuestCapacityFormula? Formula = null);

public sealed partial class GuestLayoutRelocations
{
    private readonly GuestCapacityTable[] _capacityTables;
    private static string CapacityHelper(GuestCapacityLookup site)=>$"layout_capacity_{site.Instruction:X8}";

    private void ValidateCapacityDefinitions(GuestLayoutReference[] references)
    {
        var seen=new HashSet<uint>();
        foreach(var table in _capacityTables)
        {
            if(table.Address%4!=0 || table.ExpectedValues.Count!=OriginalPlayers || table.Lookups.Count==0 ||
                table.ExpectedValues[^1]==0 || table.ExpectedValues.Where((value,index)=>value>(uint)index+1).Any() ||
                table.ExpectedValues.Zip(table.ExpectedValues.Skip(1)).Any(pair=>pair.First>pair.Second))
                throw new InvalidDataException("Invalid capacity threshold curve.");
            if (table.SignedHalfwords && table.ExpectedValues.Any(value => value > short.MaxValue))
                throw new InvalidDataException("Capacity threshold does not fit a signed halfword.");
            if (table.SignedHalfwords && table.Formula is null &&
                (ulong)table.ExpectedValues[^1] * (uint)Players / (uint)OriginalPlayers > (ulong)short.MaxValue)
                throw new InvalidDataException("Expanded capacity threshold does not fit a signed halfword.");
            if (table.Formula is { } formula)
            {
                if (formula.Divisor < 1 || formula.Bias > 0 || formula.Bias < -OriginalPlayers ||
                    table.ExpectedValues.Where((value, index) => value != FormulaThreshold(formula, index + 1)).Any() ||
                    (table.SignedHalfwords && FormulaThreshold(formula, Players) > short.MaxValue))
                    throw new InvalidDataException("Capacity formula must reproduce every authored threshold and fit the load.");
            }
            foreach(var site in table.Lookups)
            {
                if(site.Instruction%4!=0 || !Gpr(site.Base) || !Gpr(site.Index) || !Gpr(site.Destination) || !Gpr(site.Count) ||
                    !seen.Add(site.Instruction) || Players<OriginalPlayers ||
                    references.Any(reference=>reference.Instruction==site.Instruction) ||
                    _arrayCopies.Any(copy=>site.Instruction>=copy.Begin && site.Instruction<CopyEnd(copy)) ||
                    _arrayFills.Any(fill=>site.Instruction>=fill.Begin && site.Instruction<FillEnd(fill)) ||
                    _traversalMarkers.Any(marker=>site.Instruction>=marker.Begin && site.Instruction<MarkerEnd(marker)) ||
                    _resampledCursors.Any(cursor=>cursor.Instruction==site.Instruction) ||
                    _boundedAccesses.Any(bound=>bound.Instruction==site.Instruction) ||
                    _rankTables.Any(rank=>rank.Lookups.Any(lookup=>lookup.Instruction==site.Instruction)))
                    throw new InvalidDataException("Invalid or overlapping capacity lookup.");
            }
        }
    }

    private static void CheckCapacityLoad(GuestCapacityTable table,GuestCapacityLookup site,PpcInstruction instruction,IReadOnlyList<IrInstruction> nodes)
    {
        if(instruction.Word!=site.ExpectedWord || nodes.Count!=(table.SignedHalfwords ? 4 : 2) ||
            nodes[0] is not IrBinary {Op:"add",Destination:var address,Left.RegisterName:var left,Right.RegisterName:var right} ||
            left!=site.Base || right!=site.Index ||
            nodes[1] is not IrLoad {SizeBytes:var width,Destination:var destination,Address.Base:var basis,Address.Offset:0} ||
            width!=(table.SignedHalfwords ? 2 : 4) || destination!=site.Destination || basis!=address ||
            (table.SignedHalfwords &&
                (nodes[2] is not IrBinary {Op:"shl",Destination:var shifted,Left.RegisterName:var shiftInput,Right.Constant:16} ||
                 shifted!=destination || shiftInput!=destination ||
                 nodes[3] is not IrBinary {Op:"sar",Destination:var extended,Left.RegisterName:var extendInput,Right.Constant:16} ||
                 extended!=destination || extendInput!=destination)))
            throw new InvalidDataException($"Unproven capacity lookup at 0x{site.Instruction:X8}.");
    }

    private void ValidateCapacityTables(ProgramImage image)
    {
        foreach(var table in _capacityTables)
        {
            for(var index=0;index<table.ExpectedValues.Count;index++)
            {
                var width=table.SignedHalfwords ? 2 : 4;
                var pc=checked(table.Address+(uint)(index*width));
                var data=image.Memory.AsSpan(image.GetOffset(pc,width),width);
                var value=table.SignedHalfwords ? BinaryPrimitives.ReadUInt16BigEndian(data) : BinaryPrimitives.ReadUInt32BigEndian(data);
                if(value!=table.ExpectedValues[index])
                    throw new InvalidDataException($"Capacity table mismatch at 0x{pc:X8}.");
            }
            foreach(var site in table.Lookups)
            {
                var word=BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(site.Instruction,4),4));
                var instruction=PpcDecoder.Decode(site.Instruction,word);
                CheckCapacityLoad(table,site,instruction,new PpcLifter().Lift(new[] {instruction})[0].Ir);
            }
        }
    }

    private IReadOnlyList<IrInstruction>? ApplyCapacityLookup(PpcInstruction instruction,IReadOnlyList<IrInstruction> nodes)
    {
        foreach(var table in _capacityTables)
        foreach(var site in table.Lookups)
        {
            if(instruction.Address!=site.Instruction)continue;
            CheckCapacityLoad(table,site,instruction,nodes);
            if(Players==OriginalPlayers)return nodes;
            return new IrInstruction[] {nodes[0],new IrCall(site.Destination,CapacityHelper(site),new[] {IrValue.Register(site.Count)})};
        }
        return null;
    }

    public uint CapacityThreshold(int tableIndex,int count)
    {
        var values=_capacityTables[tableIndex].ExpectedValues;
        if(count<1 || count>Players)return 0;
        if(count<=OriginalPlayers)return values[count-1];
        if (_capacityTables[tableIndex].Formula is { } formula) return FormulaThreshold(formula, count);
        return Math.Max(1u,checked((uint)((ulong)values[^1]*(uint)count/(uint)OriginalPlayers)));
    }

    private static uint FormulaThreshold(GuestCapacityFormula formula, int count) =>
        checked((uint)Math.Max(1L, ((long)count + formula.Bias) / formula.Divisor));

    internal string EmitCapacityHelpers(IrFunction function)
    {
        var calls=function.Blocks.SelectMany(block=>block.Instructions).OfType<IrCall>().Select(call=>call.Target).ToHashSet(StringComparer.Ordinal);
        var source=new StringBuilder();
        foreach(var table in _capacityTables)
        foreach(var site in table.Lookups)
        {
            var helper=CapacityHelper(site);if(!calls.Contains(helper))continue;
            var expression = table.Formula is { } formula
                ? $"uint32_t((int64_t(count)+({formula.Bias}))/{formula.Divisor})"
                : $"uint32_t(uint64_t({table.ExpectedValues[^1]}u)*count/{OriginalPlayers}u)";
            source.AppendLine($$"""
                #ifndef WIICOMPILED_LAYOUT_CAPACITY_{{site.Instruction:X8}}
                #define WIICOMPILED_LAYOUT_CAPACITY_{{site.Instruction:X8}}
                #include <cstdint>
                static inline uint32_t {{helper}}(uint32_t count) {
                    if(count==0 || count>{{Players}}u)return 0;
                    static constexpr uint32_t original[]={ {{string.Join(", ",table.ExpectedValues)}} };
                    if(count<={{OriginalPlayers}}u)return original[count-1];
                    const uint32_t threshold={{expression}};
                    return threshold ? threshold : 1u;
                }
                #endif
                """);
        }
        return source.ToString();
    }
}
