using System.Buffers.Binary;
using Translator.Core.Analysis.BasicBlocks;
using Translator.Core.Disassembly;
using Translator.Core.Ir;
using Translator.Core.Lifting;
using Translator.Core.Loading;

namespace Translator.Core.Translation;

// An audited bit index inserts one bit in a uniform array of packed words.
// Unit must contain one; Index must remain available after the original shift.
public sealed record GuestBitsetStore(uint Begin, IReadOnlyList<uint> ExpectedWords,
    string Base, string Index, string Unit, string Shift, string Loaded, string Offset, string Count,
    int WordBits = 16, int WordBytes = 2, int BitsPerPlayer = 1, string? ShiftInput = null);

public sealed partial class GuestLayoutRelocations
{
    private readonly GuestBitsetStore[] _bitsetStores;
    private static uint BitsetEnd(GuestBitsetStore site)=>checked(site.Begin+(uint)site.ExpectedWords.Count*4);

    private void ValidateBitsetDefinitions(GuestLayoutReference[] references)
    {
        foreach(var site in _bitsetStores)
        {
            var registers=new[] {site.Base,site.Index,site.Unit,site.Shift,site.Loaded};
            if(site.Begin%4!=0 || site.ExpectedWords.Count is <4 or >16 || registers.Distinct().Count()!=5 ||
                registers.Any(register=>!IsGpr(register) || register is "r1" or "r2") ||
                (site.ShiftInput is not null && site.ShiftInput != site.Shift && site.ShiftInput != site.Index) ||
                site.WordBytes is not (2 or 4) || site.WordBits<=0 || site.WordBits>site.WordBytes*8 ||
                site.BitsPerPlayer<=0 || site.BitsPerPlayer>32 || (long)OriginalPlayers*site.BitsPerPlayer>site.WordBits || Players<OriginalPlayers ||
                _original.Symbol(site.Count)!=1 || _expanded.Symbol(site.Count)!=((long)Players*site.BitsPerPlayer+site.WordBits-1)/site.WordBits ||
                _original.Symbol(site.Offset)<0 || _original.Symbol(site.Offset)%site.WordBytes!=0 ||
                _expanded.Symbol(site.Offset)<0 || _expanded.Symbol(site.Offset)%site.WordBytes!=0)
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
                new IrLoad(site.Loaded,new(site.Base,offset),site.WordBytes),
                new IrBinary(site.Shift,IrValue.Register(site.Unit),IrValue.Register(site.ShiftInput ?? site.Shift),"ppc_slw"),
                new IrBinary(site.Shift,IrValue.Register(site.Loaded),IrValue.Register(site.Shift),"or"),
                new IrStore(new(site.Base,offset),IrValue.Register(site.Shift),site.WordBytes)};
            var effect=0;
            var protectedRegisters=new[] {site.Base,site.Index,site.Unit,site.Shift,site.Loaded};
            for(var index=0;index<site.ExpectedWords.Count;index++)
            {
                var pc=site.Begin+(uint)index*4;
                var word=BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(pc,4),4));
                if(word!=site.ExpectedWords[index])throw new InvalidDataException($"Bitset instruction mismatch at 0x{pc:X8}.");
                IReadOnlyList<IrInstruction> nodes;
                try{nodes=new PpcLifter().Lift(new[] {PpcDecoder.Decode(pc,word)})[0].Ir;}
                catch(InvalidOperationException error){throw new InvalidDataException($"Unsupported bitset instruction at 0x{pc:X8}.",error);}
                if(nodes.Count==1 && effect<4 && nodes[0]==expected[effect])effect++;
                else if(nodes.Count!=1 || nodes[0] is not IrBinary {Op:"add", Right.Constant:1} increment ||
                    increment.Left.RegisterName!=increment.Destination || protectedRegisters.Contains(increment.Destination) ||
                    increment.Destination is "r1" or "r2" || effect==4)
                    throw new InvalidDataException($"Unproven bitset load/shift/or/store at 0x{pc:X8}.");
            }
            if(effect!=4)throw new InvalidDataException($"Incomplete bitset effects at 0x{site.Begin:X8}.");
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
        // Retain original register effects, including independent loop counters.
        // Only the store selects the current word and its usable bit range.
        return new IrInstruction[] {
            new IrBinary(Name("word"),IrValue.Register(site.Index),IrValue.Imm(site.WordBits),"divu"),
            new IrBinary(Name("bytes"),V("word"),IrValue.Imm(site.WordBytes),"mul"),
            new IrBinary(Name("address"),IrValue.Register(site.Base),V("bytes"),"add"),
            new IrLoad(Name("old"),new(Name("address"),_expanded.Symbol(site.Offset)),site.WordBytes),
            new IrBinary(Name("first"),V("word"),IrValue.Imm(site.WordBits),"mul"),
            new IrBinary(Name("bit"),IrValue.Register(site.Index),V("first"),"sub"),
            new IrBinary(Name("mask"),IrValue.Register(site.Unit),V("bit"),"shl"),
            new IrBinary(Name("new"),V("old"),V("mask"),"or"),
            new IrStore(new(Name("address"),_expanded.Symbol(site.Offset)),V("new"),site.WordBytes)
        };
    }

    private void ValidateBitsetControlFlow(IReadOnlyList<PpcInstruction> instructions,IReadOnlyList<BasicBlock> blocks)
    {
        foreach(var site in _bitsetStores)
        {
            var end=BitsetEnd(site);var inside=instructions.Count(instruction=>instruction.Address>=site.Begin && instruction.Address<end);
            if(inside!=0 && (inside!=site.ExpectedWords.Count || blocks.Any(block=>block.StartAddress>site.Begin && block.StartAddress<end)))
                throw new InvalidDataException($"Bitset span is partial or has an interior entry at 0x{site.Begin:X8}.");
        }
    }
}
