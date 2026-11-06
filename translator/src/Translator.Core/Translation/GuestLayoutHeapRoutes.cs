using System.Buffers.Binary;
using Translator.Core.Analysis.BasicBlocks;
using Translator.Core.Disassembly;
using Translator.Core.Ir;
using Translator.Core.Lifting;
using Translator.Core.Loading;

namespace Translator.Core.Translation;

// Redirect the default allocation slot to an existing secondary heap. Backing
// heap ownership fields remain distinct: callers must audit destruction uses
// those ownership fields, rather than destroying the routed slots twice.
public sealed record GuestHeapRoute(uint Begin,IReadOnlyList<uint> ExpectedWords,string Scene,
    string PrimaryRegister,string SecondaryRegister,string ZeroRegister,
    int PrimaryHeap,int SecondaryHeap,int PrimarySlot,int SecondarySlot);

public sealed partial class GuestLayoutRelocations
{
    private readonly GuestHeapRoute[] _heapRoutes;
    private static uint HeapRouteEnd(GuestHeapRoute site)=>checked(site.Begin+20);

    private void ValidateHeapRouteDefinitions(GuestLayoutReference[] references)
    {
        foreach(var site in _heapRoutes)
        {
            var registers=new[]{site.Scene,site.PrimaryRegister,site.SecondaryRegister,site.ZeroRegister};
            var fields=new[]{site.PrimaryHeap,site.SecondaryHeap,site.PrimarySlot,site.SecondarySlot};
            if(site.Begin%4!=0 || site.ExpectedWords.Count!=5 || Players<OriginalPlayers ||
                registers.Distinct().Count()!=4 || registers.Any(register=>!IsGpr(register) || register is "r1" or "r2") ||
                fields.Distinct().Count()!=4 || fields.Any(offset=>offset<0 || offset%4!=0))
                throw new InvalidDataException("Invalid heap route definition.");
            var end=HeapRouteEnd(site);bool Inside(uint pc)=>pc>=site.Begin && pc<end;
            if(references.Any(reference=>Inside(reference.Instruction)) ||
                _arrayCopies.Any(copy=>copy.Begin<end && CopyEnd(copy)>site.Begin) ||
                _arrayFills.Any(fill=>fill.Begin<end && FillEnd(fill)>site.Begin) ||
                _traversalMarkers.Any(marker=>marker.Begin<end && MarkerEnd(marker)>site.Begin) ||
                _bitsetStores.Any(bitset=>bitset.Begin<end && BitsetEnd(bitset)>site.Begin) ||
                _rankTables.Any(table=>table.Lookups.Any(lookup=>Inside(lookup.Instruction))) ||
                _capacityTables.Any(table=>table.Lookups.Any(lookup=>Inside(lookup.Instruction))) ||
                _gridTables.Any(table=>table.Lookups.Any(lookup=>Inside(lookup.Instruction))) ||
                _boundedAccesses.Any(bound=>Inside(bound.Instruction)) ||
                _resampledCursors.Any(cursor=>Inside(cursor.Instruction)) ||
                _normalizedRanks.Any(rank=>Inside(rank.Instruction)) ||
                _heapRoutes.Count(other=>other.Begin<end && HeapRouteEnd(other)>site.Begin)!=1)
                throw new InvalidDataException("Overlapping heap route relocation.");
        }
    }

    private void ValidateHeapRoutes(ProgramImage image)
    {
        foreach(var site in _heapRoutes)
        {
            var expected=new IrInstruction[]{
                new IrLoad(site.SecondaryRegister,new(site.Scene,site.SecondaryHeap),4),
                new IrAssign(site.ZeroRegister,IrValue.Imm(0)),
                new IrLoad(site.PrimaryRegister,new(site.Scene,site.PrimaryHeap),4),
                new IrStore(new(site.Scene,site.PrimarySlot),IrValue.Register(site.PrimaryRegister),4),
                new IrStore(new(site.Scene,site.SecondarySlot),IrValue.Register(site.SecondaryRegister),4)};
            for(var index=0;index<5;index++)
            {
                var pc=site.Begin+(uint)index*4;
                var word=BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(pc,4),4));
                if(word!=site.ExpectedWords[index])throw new InvalidDataException($"Heap route word mismatch at 0x{pc:X8}.");
                IReadOnlyList<IrInstruction> nodes;
                try{nodes=new PpcLifter().Lift(new[]{PpcDecoder.Decode(pc,word)})[0].Ir;}
                catch(InvalidOperationException error){throw new InvalidDataException("Unsupported heap route instruction.",error);}
                if(nodes.Count!=1 || nodes[0]!=expected[index])throw new InvalidDataException($"Unproven heap route at 0x{pc:X8}.");
            }
        }
    }

    private IReadOnlyList<IrInstruction>? ApplyHeapRoute(PpcInstruction instruction,IReadOnlyList<IrInstruction> nodes)
    {
        var site=_heapRoutes.FirstOrDefault(candidate=>instruction.Address>=candidate.Begin && instruction.Address<HeapRouteEnd(candidate));
        if(site is null)return null;
        if(instruction.Word!=site.ExpectedWords[(int)(instruction.Address-site.Begin)/4])throw new InvalidDataException("Heap route instruction mismatch.");
        return Players!=OriginalPlayers && instruction.Address==site.Begin+12
            ? new IrInstruction[]{new IrStore(new(site.Scene,site.PrimarySlot),IrValue.Register(site.SecondaryRegister),4)}:nodes;
    }

    private void ValidateHeapRouteControlFlow(IReadOnlyList<PpcInstruction> instructions,IReadOnlyList<BasicBlock> blocks)
    {
        foreach(var site in _heapRoutes)
        {
            var end=HeapRouteEnd(site);var inside=instructions.Count(instruction=>instruction.Address>=site.Begin && instruction.Address<end);
            if(inside!=0 && (inside!=5 || blocks.Any(block=>block.StartAddress>site.Begin && block.StartAddress<end)))
                throw new InvalidDataException($"Heap route span is partial or has an interior entry at 0x{site.Begin:X8}.");
        }
    }
}
