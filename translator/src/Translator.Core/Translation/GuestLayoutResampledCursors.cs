using System.Buffers.Binary;
using Translator.Core.Disassembly;
using Translator.Core.Ir;
using Translator.Core.Lifting;
using Translator.Core.Loading;

namespace Translator.Core.Translation;

// A loop writes a capacity-sized destination from a fixed authored rank curve.
// NextIndex is already advanced by one destination stride at this instruction.
public sealed record GuestResampledCursor(uint Instruction, uint ExpectedWord,
    string Cursor, string NextIndex, string IndexStride, string SourceStride, string Count, int SourceCount);

public sealed partial class GuestLayoutRelocations
{
    private readonly GuestResampledCursor[] _resampledCursors;

    private void ValidateCursorDefinitions(GuestLayoutReference[] references)
    {
        if (_resampledCursors.Select(site=>site.Instruction).Distinct().Count()!=_resampledCursors.Length)
            throw new InvalidDataException("Duplicate resampled cursor site.");
        foreach(var site in _resampledCursors)
        {
            if(site.Instruction%4!=0 || !IsGpr(site.Cursor) || !IsGpr(site.NextIndex) || site.Cursor==site.NextIndex ||
                site.SourceCount is < 2 or > 65535 || _original.Symbol(site.Count)!=site.SourceCount ||
                _expanded.Symbol(site.Count)<site.SourceCount || _expanded.Symbol(site.Count)>65535)
                throw new InvalidDataException("Invalid resampled cursor dimensions/registers.");
            foreach(var symbol in new[] {site.IndexStride,site.SourceStride})
                if(_original.Symbol(symbol)<=0 || _original.Symbol(symbol)!=_expanded.Symbol(symbol))
                    throw new InvalidDataException("Resampled cursor strides must remain positive and unchanged.");
            if(references.Any(reference=>reference.Instruction==site.Instruction) ||
                _arrayCopies.Any(copy=>site.Instruction>=copy.Begin && site.Instruction<CopyEnd(copy)) ||
                _arrayFills.Any(fill=>site.Instruction>=fill.Begin && site.Instruction<FillEnd(fill)) ||
                _rankTables.Any(table=>table.Lookups.Any(lookup=>lookup.Instruction==site.Instruction)) ||
                _boundedAccesses.Any(bound=>bound.Instruction==site.Instruction))
                throw new InvalidDataException("Overlapping resampled cursor relocation.");
        }
    }

    private void CheckCursor(GuestResampledCursor site, PpcInstruction instruction, IReadOnlyList<IrInstruction> nodes)
    {
        if(instruction.Word!=site.ExpectedWord || nodes.Count!=1 || nodes[0] is not IrBinary {
            Op:"add",Left.RegisterName:var left,Right.Constant:var step,Destination:var destination} ||
            left!=site.Cursor || destination!=site.Cursor || step!=_original.Symbol(site.SourceStride))
            throw new InvalidDataException($"Unproven resampled cursor step at 0x{site.Instruction:X8}.");
    }

    private void ValidateCursors(ProgramImage image)
    {
        foreach(var site in _resampledCursors)
        {
            var word=BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(site.Instruction,4),4));
            var instruction=PpcDecoder.Decode(site.Instruction,word);
            CheckCursor(site,instruction,new PpcLifter().Lift(new[] {instruction})[0].Ir);
        }
    }

    private IReadOnlyList<IrInstruction>? ApplyCursor(PpcInstruction instruction,IReadOnlyList<IrInstruction> nodes)
    {
        GuestResampledCursor? site=null;
        foreach(var candidate in _resampledCursors)
            if(candidate.Instruction==instruction.Address){site=candidate;break;}
        if(site is null)return null;
        CheckCursor(site,instruction,nodes);
        var count=_expanded.Symbol(site.Count);
        if(count==site.SourceCount)return nodes;
        // Every scratch value is defined and consumed in this one lifted
        // instruction. Use SSA's existing block-local address namespace.
        var prefix=$"addr_layout_cursor_{site.Instruction:X8}_";
        string Name(string suffix)=>prefix+suffix+"_loc";
        IrValue V(string suffix)=>IrValue.Register(Name(suffix));
        var denominator=count-1;var scale=site.SourceCount-1;
        // Closest authored rank at each normalized destination position. Move by
        // the difference, retaining the original cursor and float conversions.
        return new IrInstruction[] {
            new IrBinary(Name("next"),IrValue.Register(site.NextIndex),IrValue.Imm(_original.Symbol(site.IndexStride)),"divu"),
            new IrBinary(Name("previous"),V("next"),IrValue.Imm(1),"sub"),
            new IrBinary(Name("oldScaled"),V("previous"),IrValue.Imm(scale),"mul"),
            new IrBinary(Name("oldRounded"),V("oldScaled"),IrValue.Imm(denominator/2),"add"),
            new IrBinary(Name("oldRank"),V("oldRounded"),IrValue.Imm(denominator),"divu"),
            new IrBinary(Name("newScaled"),V("next"),IrValue.Imm(scale),"mul"),
            new IrBinary(Name("newRounded"),V("newScaled"),IrValue.Imm(denominator/2),"add"),
            new IrBinary(Name("newRank"),V("newRounded"),IrValue.Imm(denominator),"divu"),
            new IrBinary(Name("difference"),V("newRank"),V("oldRank"),"sub"),
            new IrBinary(Name("bytes"),V("difference"),IrValue.Imm(_original.Symbol(site.SourceStride)),"mul"),
            new IrBinary(site.Cursor,IrValue.Register(site.Cursor),V("bytes"),"add")
        };
    }
}
