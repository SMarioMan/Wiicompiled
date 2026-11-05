using System.Buffers.Binary;
using System.Text;
using Translator.Core.Disassembly;
using Translator.Core.Ir;
using Translator.Core.Lifting;
using Translator.Core.Loading;

namespace Translator.Core.Translation;

public sealed record GuestCapacityLookup(uint Instruction,uint ExpectedWord,string Base,string Index,string Destination,string Count);
public sealed record GuestCapacityTable(uint Address,IReadOnlyList<uint> ExpectedValues,IReadOnlyList<GuestCapacityLookup> Lookups);

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

    private static void CheckCapacityLoad(GuestCapacityLookup site,PpcInstruction instruction,IReadOnlyList<IrInstruction> nodes)
    {
        if(instruction.Word!=site.ExpectedWord || nodes.Count!=2 ||
            nodes[0] is not IrBinary {Op:"add",Destination:var address,Left.RegisterName:var left,Right.RegisterName:var right} ||
            left!=site.Base || right!=site.Index ||
            nodes[1] is not IrLoad {SizeBytes:4,Destination:var destination,Address.Base:var basis,Address.Offset:0} ||
            destination!=site.Destination || basis!=address)
            throw new InvalidDataException($"Unproven capacity lookup at 0x{site.Instruction:X8}.");
    }

    private void ValidateCapacityTables(ProgramImage image)
    {
        foreach(var table in _capacityTables)
        {
            for(var index=0;index<table.ExpectedValues.Count;index++)
            {
                var pc=checked(table.Address+(uint)index*4);
                if(BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(pc,4),4))!=table.ExpectedValues[index])
                    throw new InvalidDataException($"Capacity table mismatch at 0x{pc:X8}.");
            }
            foreach(var site in table.Lookups)
            {
                var word=BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(site.Instruction,4),4));
                var instruction=PpcDecoder.Decode(site.Instruction,word);
                CheckCapacityLoad(site,instruction,new PpcLifter().Lift(new[] {instruction})[0].Ir);
            }
        }
    }

    private IReadOnlyList<IrInstruction>? ApplyCapacityLookup(PpcInstruction instruction,IReadOnlyList<IrInstruction> nodes)
    {
        foreach(var table in _capacityTables)
        foreach(var site in table.Lookups)
        {
            if(instruction.Address!=site.Instruction)continue;
            CheckCapacityLoad(site,instruction,nodes);
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
        return Math.Max(1u,checked((uint)((ulong)values[^1]*(uint)count/(uint)OriginalPlayers)));
    }

    internal string EmitCapacityHelpers(IrFunction function)
    {
        var calls=function.Blocks.SelectMany(block=>block.Instructions).OfType<IrCall>().Select(call=>call.Target).ToHashSet(StringComparer.Ordinal);
        var source=new StringBuilder();
        foreach(var table in _capacityTables)
        foreach(var site in table.Lookups)
        {
            var helper=CapacityHelper(site);if(!calls.Contains(helper))continue;
            source.AppendLine($$"""
                #ifndef WIICOMPILED_LAYOUT_CAPACITY_{{site.Instruction:X8}}
                #define WIICOMPILED_LAYOUT_CAPACITY_{{site.Instruction:X8}}
                #include <cstdint>
                static inline uint32_t {{helper}}(uint32_t count) {
                    if(count==0 || count>{{Players}}u)return 0;
                    static constexpr uint32_t original[]={ {{string.Join(", ",table.ExpectedValues)}} };
                    if(count<={{OriginalPlayers}}u)return original[count-1];
                    const uint32_t threshold=uint32_t(uint64_t({{table.ExpectedValues[^1]}}u)*count/{{OriginalPlayers}}u);
                    return threshold ? threshold : 1u;
                }
                #endif
                """);
        }
        return source.ToString();
    }
}
