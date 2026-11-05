using System.Buffers.Binary;
using Translator.Core.Analysis.BasicBlocks;
using Translator.Core.Disassembly;
using Translator.Core.Ir;
using Translator.Core.Lifting;
using Translator.Core.Loading;

namespace Translator.Core.Translation;

// Only for a traversal whose entry clears the complete visited set. The caller
// must audit that lifetime contract; instruction verification alone cannot infer it.
public sealed record GuestTraversalMarker(uint Begin, IReadOnlyList<uint> ExpectedWords,
    string Source, string Player, string Destination);

public sealed partial class GuestLayoutRelocations
{
    private readonly GuestTraversalMarker[] _traversalMarkers;
    private static uint MarkerEnd(GuestTraversalMarker site) => checked(site.Begin+(uint)site.ExpectedWords.Count*4);

    private void ValidateMarkerDefinitions(GuestLayoutReference[] references)
    {
        foreach(var site in _traversalMarkers)
        {
            var end=MarkerEnd(site);
            if(site.Begin%4!=0 || site.ExpectedWords.Count is < 2 or > 16 ||
                !IsGpr(site.Source) || !IsGpr(site.Player) || !IsGpr(site.Destination) ||
                site.Source==site.Player || Players<OriginalPlayers)
                throw new InvalidDataException("Invalid per-traversal marker definition.");
            if(references.Any(reference=>reference.Instruction>=site.Begin && reference.Instruction<end) ||
                _arrayCopies.Any(copy=>copy.Begin<end && CopyEnd(copy)>site.Begin) ||
                _arrayFills.Any(fill=>fill.Begin<end && FillEnd(fill)>site.Begin) ||
                _rankTables.Any(table=>table.Lookups.Any(lookup=>lookup.Instruction>=site.Begin && lookup.Instruction<end)) ||
                _boundedAccesses.Any(bound=>bound.Instruction>=site.Begin && bound.Instruction<end) ||
                _resampledCursors.Any(cursor=>cursor.Instruction>=site.Begin && cursor.Instruction<end) ||
                _traversalMarkers.Count(other=>other.Begin<end && MarkerEnd(other)>site.Begin)!=1)
                throw new InvalidDataException("Overlapping per-traversal marker rewrites.");
        }
    }

    private static void CheckMarkerShift(GuestTraversalMarker site,PpcInstruction instruction,IReadOnlyList<IrInstruction> nodes)
    {
        if(instruction.Word!=site.ExpectedWords[^1] || nodes.Count!=1 || nodes[0] is not IrBinary {
            Op:"ppc_slw",Destination:var destination,Left.RegisterName:var source,Right.RegisterName:var player} ||
            destination!=site.Destination || source!=site.Source || player!=site.Player)
            throw new InvalidDataException($"Unproven per-traversal marker shift at 0x{instruction.Address:X8}.");
    }

    private void ValidateMarkers(ProgramImage image)
    {
        foreach(var site in _traversalMarkers)
        {
            for(var index=0;index<site.ExpectedWords.Count;index++)
            {
                var pc=checked(site.Begin+(uint)index*4);
                var word=BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(pc,4),4));
                if(word!=site.ExpectedWords[index])throw new InvalidDataException($"Per-traversal marker word mismatch at 0x{pc:X8}.");
                var instruction=PpcDecoder.Decode(pc,word);
                var nodes=new PpcLifter().Lift(new[] {instruction})[0].Ir;
                if(index==0)
                {
                    if(nodes.Count!=1 || nodes[0] is not IrAssign {Destination:var register,Value.Constant:1} || register!=site.Source)
                        throw new InvalidDataException("Per-traversal marker must begin with a unit assignment.");
                }
                else if(index==site.ExpectedWords.Count-1)CheckMarkerShift(site,instruction,nodes);
                else foreach(var node in nodes)
                {
                    // Keep intervening pure register operations and scalar reads exactly
                    // where they were. They may not redefine either shift input.
                    var destination=node switch {IrLoad load=>load.Destination,IrBinary {Op:"sext"} binary=>binary.Destination,_=>null};
                    if(destination is null || destination==site.Source || destination==site.Player)
                        throw new InvalidDataException("Per-traversal marker has unproven intervening effects.");
                }
            }
        }
    }

    private IReadOnlyList<IrInstruction>? ApplyMarker(PpcInstruction instruction,IReadOnlyList<IrInstruction> nodes)
    {
        foreach(var site in _traversalMarkers)
        {
            if(instruction.Address!=MarkerEnd(site)-4)continue;
            CheckMarkerShift(site,instruction,nodes);
            return Players==OriginalPlayers ? nodes : new IrInstruction[] {new IrAssign(site.Destination,IrValue.Register(site.Source))};
        }
        return null;
    }

    private void ValidateMarkerControlFlow(IReadOnlyList<PpcInstruction> instructions,IReadOnlyList<BasicBlock> blocks)
    {
        foreach(var site in _traversalMarkers)
        {
            var end=MarkerEnd(site);var inside=instructions.Count(instruction=>instruction.Address>=site.Begin && instruction.Address<end);
            if(inside!=0 && (inside!=site.ExpectedWords.Count || blocks.Any(block=>block.StartAddress>site.Begin && block.StartAddress<end)))
                throw new InvalidDataException($"Per-traversal marker span is partial or has an interior entry at 0x{site.Begin:X8}.");
        }
    }
}
