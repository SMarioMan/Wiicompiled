using System.Buffers.Binary;
using Translator.Core.Analysis.BasicBlocks;
using Translator.Core.Disassembly;
using Translator.Core.Ir;
using Translator.Core.Lifting;
using Translator.Core.Loading;

namespace Translator.Core.Translation;

// An audited 0..players-1 index inserts one bit in a uniform u16-word array.
// Unit must contain one; Index must remain available after the original shift.
public sealed record GuestBitsetStore(uint Begin, IReadOnlyList<uint> ExpectedWords,
    string Base, string Index, string Unit, string Shift, string Loaded, string Offset, string Count);

public sealed partial class GuestLayoutRelocations
{
    private readonly GuestBitsetStore[] _bitsetStores;
    private static uint BitsetEnd(GuestBitsetStore site)=>checked(site.Begin+16);

    private void ValidateBitsetDefinitions(GuestLayoutReference[] references)
    {
        foreach(var site in _bitsetStores)
        {
            var registers=new[] {site.Base,site.Index,site.Unit,site.Shift,site.Loaded};
            if(site.Begin%4!=0 || site.ExpectedWords.Count!=4 || registers.Distinct().Count()!=5 ||
                registers.Any(register=>!IsGpr(register) || register is "r1" or "r2") ||
                OriginalPlayers>16 || Players<OriginalPlayers ||
                _original.Symbol(site.Count)!=1 || _expanded.Symbol(site.Count)!=(Players+15)/16 ||
                _original.Symbol(site.Offset)<0 || _original.Symbol(site.Offset)%2!=0 ||
                _expanded.Symbol(site.Offset)<0 || _expanded.Symbol(site.Offset)%2!=0)
                throw new InvalidDataException("Invalid grouped bitset store definition.");
            var end=BitsetEnd(site);
            bool Inside(uint pc)=>pc>=site.Begin && pc<end;
            if(references.Any(reference=>Inside(reference.Instruction)) ||
                _arrayCopies.Any(copy=>copy.Begin<end && CopyEnd(copy)>site.Begin) ||
                _arrayFills.Any(fill=>fill.Begin<end && FillEnd(fill)>site.Begin) ||
                _traversalMarkers.Any(marker=>marker.Begin<end && MarkerEnd(marker)>site.Begin) ||
                _rankTables.Any(table=>table.Lookups.Any(lookup=>Inside(lookup.Instruction))) ||
                _capacityTables.Any(table=>table.Lookups.Any(lookup=>Inside(lookup.Instruction))) ||
                _gridTables.Any(table=>table.Lookups.Any(lookup=>Inside(lookup.Instruction))) ||
                _boundedAccesses.Any(bound=>Inside(bound.Instruction)) ||
                _resampledCursors.Any(cursor=>Inside(cursor.Instruction)) ||
                _normalizedRanks.Any(rank=>Inside(rank.Instruction)) ||
                _bitsetStores.Count(other=>other.Begin<end && BitsetEnd(other)>site.Begin)!=1)
                throw new InvalidDataException("Overlapping bitset store relocation.");
        }
    }

    private void ValidateBitsets(ProgramImage image)
    {
        foreach(var site in _bitsetStores)
        {
            var offset=_original.Symbol(site.Offset);
            var expected=new IrInstruction[] {
                new IrLoad(site.Loaded,new(site.Base,offset),2),
                new IrBinary(site.Shift,IrValue.Register(site.Unit),IrValue.Register(site.Shift),"ppc_slw"),
                new IrBinary(site.Shift,IrValue.Register(site.Loaded),IrValue.Register(site.Shift),"or"),
                new IrStore(new(site.Base,offset),IrValue.Register(site.Shift),2)};
            for(var index=0;index<4;index++)
            {
                var pc=site.Begin+(uint)index*4;
                var word=BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(pc,4),4));
                if(word!=site.ExpectedWords[index])throw new InvalidDataException($"Bitset instruction mismatch at 0x{pc:X8}.");
                IReadOnlyList<IrInstruction> nodes;
                try{nodes=new PpcLifter().Lift(new[] {PpcDecoder.Decode(pc,word)})[0].Ir;}
                catch(InvalidOperationException error){throw new InvalidDataException($"Unsupported bitset instruction at 0x{pc:X8}.",error);}
                if(nodes.Count!=1 || nodes[0]!=expected[index])
                    throw new InvalidDataException($"Unproven bitset load/shift/or/store at 0x{pc:X8}.");
            }
        }
    }

    private IReadOnlyList<IrInstruction>? ApplyBitsetStore(PpcInstruction instruction,IReadOnlyList<IrInstruction> nodes)
    {
        var site=_bitsetStores.FirstOrDefault(candidate=>instruction.Address>=candidate.Begin && instruction.Address<BitsetEnd(candidate));
        if(site is null)return null;
        if(instruction.Word!=site.ExpectedWords[(int)(instruction.Address-site.Begin)/4])
            throw new InvalidDataException("Bitset store instruction mismatch.");
        if(Players==OriginalPlayers || instruction.Address!=BitsetEnd(site)-4)return nodes;
        var prefix=$"addr_layout_bitset_{site.Begin:X8}_";
        string Name(string suffix)=>prefix+suffix+"_loc";
        IrValue V(string suffix)=>IrValue.Register(Name(suffix));
        // Retain the original load/shift/or register effects. Only the store
        // changes: high participant IDs select a later u16 word, never wrap.
        return new IrInstruction[] {
            new IrBinary(Name("word"),IrValue.Register(site.Index),IrValue.Imm(4),"shr"),
            new IrBinary(Name("bytes"),V("word"),IrValue.Imm(2),"mul"),
            new IrBinary(Name("address"),IrValue.Register(site.Base),V("bytes"),"add"),
            new IrLoad(Name("old"),new(Name("address"),_expanded.Symbol(site.Offset)),2),
            new IrBinary(Name("bit"),IrValue.Register(site.Index),IrValue.Imm(15),"and"),
            new IrBinary(Name("mask"),IrValue.Register(site.Unit),V("bit"),"shl"),
            new IrBinary(Name("new"),V("old"),V("mask"),"or"),
            new IrStore(new(Name("address"),_expanded.Symbol(site.Offset)),V("new"),2)
        };
    }

    private void ValidateBitsetControlFlow(IReadOnlyList<PpcInstruction> instructions,IReadOnlyList<BasicBlock> blocks)
    {
        foreach(var site in _bitsetStores)
        {
            var end=BitsetEnd(site);var inside=instructions.Count(instruction=>instruction.Address>=site.Begin && instruction.Address<end);
            if(inside!=0 && (inside!=4 || blocks.Any(block=>block.StartAddress>site.Begin && block.StartAddress<end)))
                throw new InvalidDataException($"Bitset span is partial or has an interior entry at 0x{site.Begin:X8}.");
        }
    }
}
