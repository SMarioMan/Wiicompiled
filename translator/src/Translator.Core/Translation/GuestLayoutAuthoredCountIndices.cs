using System.Buffers.Binary;
using Translator.Core.Disassembly;
using Translator.Core.Ir;
using Translator.Core.Lifting;
using Translator.Core.Loading;

namespace Translator.Core.Translation;

// A fixed resource table may describe rules for two through twelve entrants.
// Bound that table's index without changing the actual field or its storage.
public sealed record GuestAuthoredCountIndex(uint Instruction,uint ExpectedWord,
    string Input,string Destination,int FirstCount,int LastCount);

public sealed partial class GuestLayoutRelocations
{
    private readonly GuestAuthoredCountIndex[] _authoredCountIndices;

    private void ValidateAuthoredCountIndexDefinitions(GuestLayoutReference[] references)
    {
        if(_authoredCountIndices.Select(site=>site.Instruction).Distinct().Count()!=_authoredCountIndices.Length)
            throw new InvalidDataException("Duplicate authored count-index site.");
        foreach(var site in _authoredCountIndices)
        {
            if(site.Instruction%4!=0 || !IsGpr(site.Input) || !IsGpr(site.Destination) ||
                site.Input is "r1" or "r2" || site.Destination is "r1" or "r2" ||
                site.FirstCount<1 || site.LastCount<site.FirstCount || site.LastCount>OriginalPlayers || Players<OriginalPlayers)
                throw new InvalidDataException("Invalid authored count-index dimensions or registers.");
            var pc=site.Instruction;
            if(references.Any(reference=>reference.Instruction==pc) ||
                _arrayCopies.Any(copy=>pc>=copy.Begin && pc<CopyEnd(copy)) ||
                _arrayFills.Any(fill=>pc>=fill.Begin && pc<FillEnd(fill)) ||
                _traversalMarkers.Any(marker=>pc>=marker.Begin && pc<MarkerEnd(marker)) ||
                _bitsetStores.Any(bitset=>pc>=bitset.Begin && pc<BitsetEnd(bitset)) ||
                _heapRoutes.Any(route=>pc>=route.Begin && pc<HeapRouteEnd(route)) ||
                _rankTables.Any(table=>table.Lookups.Any(lookup=>lookup.Instruction==pc)) ||
                _capacityTables.Any(table=>table.Lookups.Any(lookup=>lookup.Instruction==pc)) ||
                _gridTables.Any(table=>table.Lookups.Any(lookup=>lookup.Instruction==pc)) ||
                _boundedAccesses.Any(bound=>bound.Instruction==pc) ||
                _resampledCursors.Any(cursor=>cursor.Instruction==pc) ||
                _normalizedRanks.Any(rank=>rank.Instruction==pc))
                throw new InvalidDataException("Overlapping authored count-index relocation.");
        }
    }

    private static void CheckAuthoredCountIndex(GuestAuthoredCountIndex site,PpcInstruction instruction,IReadOnlyList<IrInstruction> nodes)
    {
        if(instruction.Word!=site.ExpectedWord || nodes.Count!=1 || nodes[0] is not IrBinary {
            Op:"add",Destination:var destination,Left.RegisterName:var input,Right.Constant:var bias} ||
            destination!=site.Destination || input!=site.Input || bias!=-site.FirstCount)
            throw new InvalidDataException($"Unproven authored count-index conversion at 0x{site.Instruction:X8}.");
    }

    private void ValidateAuthoredCountIndices(ProgramImage image)
    {
        foreach(var site in _authoredCountIndices)
        {
            var word=BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(site.Instruction,4),4));
            var instruction=PpcDecoder.Decode(site.Instruction,word);
            CheckAuthoredCountIndex(site,instruction,new PpcLifter().Lift(new[]{instruction})[0].Ir);
        }
    }

    private IReadOnlyList<IrInstruction>? ApplyAuthoredCountIndex(PpcInstruction instruction,IReadOnlyList<IrInstruction> nodes)
    {
        var site=_authoredCountIndices.FirstOrDefault(candidate=>candidate.Instruction==instruction.Address);
        if(site is null)return null;
        CheckAuthoredCountIndex(site,instruction,nodes);
        if(Players==OriginalPlayers)return nodes;
        var prefix=$"addr_layout_count_{site.Instruction:X8}_";
        string N(string suffix)=>prefix+suffix+"_loc";
        IrValue V(string suffix)=>IrValue.Register(N(suffix));
        // CompareUnsigned yields -1/0/1. Unsigned division extracts a 0/1
        // selection bit without guest branches or changes to CR/XER. All
        // temporaries are native locals; only the original destination changes.
        return new IrInstruction[]{
            new IrBinary(N("lowCmp"),IrValue.Register(site.Input),IrValue.Imm(site.FirstCount),"sub_u"),
            new IrBinary(N("below"),V("lowCmp"),IrValue.Imm(int.MinValue),"divu"),
            new IrBinary(N("lowMask"),IrValue.Imm(0),V("below"),"sub"),
            new IrBinary(N("lowKeep"),IrValue.Register(site.Input),V("lowMask"),"andc"),
            new IrBinary(N("lowLimit"),IrValue.Imm(site.FirstCount),V("lowMask"),"and"),
            new IrBinary(N("lower"),V("lowKeep"),V("lowLimit"),"or"),
            new IrBinary(N("highCmp"),V("lower"),IrValue.Imm(site.LastCount),"sub_u"),
            new IrBinary(N("highPlus"),V("highCmp"),IrValue.Imm(1),"add"),
            new IrBinary(N("above"),V("highPlus"),IrValue.Imm(2),"divu"),
            new IrBinary(N("highMask"),IrValue.Imm(0),V("above"),"sub"),
            new IrBinary(N("highKeep"),V("lower"),V("highMask"),"andc"),
            new IrBinary(N("highLimit"),IrValue.Imm(site.LastCount),V("highMask"),"and"),
            new IrBinary(N("upper"),V("highKeep"),V("highLimit"),"or"),
            new IrBinary(site.Destination,V("upper"),IrValue.Imm(site.FirstCount),"sub")};
    }
}
