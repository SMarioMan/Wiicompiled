using System.Buffers.Binary;
using System.Text;
using Translator.Core.Disassembly;
using Translator.Core.Ir;
using Translator.Core.Lifting;
using Translator.Core.Loading;

namespace Translator.Core.Translation;

// A register can serve both as an authored loop count and as an unrelated state
// constant. Convert only its transfer to CTR, retaining the source register and
// retaining unexpected runtime counts rather than silently replacing them.
public sealed record GuestCountConversion(uint Instruction,uint ExpectedWord,string Input,string Count);
public sealed partial class GuestLayoutRelocations
{
    private readonly GuestCountConversion[] _countConversions;
    private static string CountHelper(GuestCountConversion site)=>$"layout_count_{site.Instruction:X8}";
    private void ValidateCountConversions()
    {
        if(_countConversions.Select(s=>s.Instruction).Distinct().Count()!=_countConversions.Length)
            throw new InvalidDataException("Duplicate register count conversion.");
        foreach(var site in _countConversions)
            if(site.Instruction%4!=0 || !IsGpr(site.Input) || site.Input is "r1" or "r2" ||
                _original.Symbol(site.Count)<1 || _expanded.Symbol(site.Count)<1 || HasOtherWordRewrite(site.Instruction) ||
                _wordComparisons.Any(s=>site.Instruction>=s.Begin && site.Instruction<=WordComparePc(s)) ||
                _wordTables.Any(t=>t.Loads.Any(s=>s.Instruction==site.Instruction)))
                throw new InvalidDataException("Invalid or overlapping register count conversion.");
    }
    private static void CheckCountConversion(GuestCountConversion site,PpcInstruction instruction,IReadOnlyList<IrInstruction> nodes)
    {
        if(instruction.Word!=site.ExpectedWord || nodes.Count!=1 || nodes[0] is not IrAssign {Destination:"ctr",Value.RegisterName:var input} || input!=site.Input)
            throw new InvalidDataException($"Unproven register count transfer at 0x{site.Instruction:X8}.");
    }
    private void ValidateCountConversionImage(ProgramImage image)
    {
        foreach(var site in _countConversions){var word=BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(site.Instruction,4),4));
            var instruction=PpcDecoder.Decode(site.Instruction,word);CheckCountConversion(site,instruction,new PpcLifter().Lift(new[]{instruction})[0].Ir);}
    }
    private IReadOnlyList<IrInstruction>? ApplyCountConversion(PpcInstruction instruction,IReadOnlyList<IrInstruction> nodes)
    {
        var site=_countConversions.FirstOrDefault(s=>s.Instruction==instruction.Address);if(site is null)return null;
        CheckCountConversion(site,instruction,nodes);if(_original.Symbol(site.Count)==_expanded.Symbol(site.Count))return nodes;
        return new IrInstruction[]{new IrCall("ctr",CountHelper(site),new[]{IrValue.Register(site.Input)})};
    }
    private void EmitCountConversionHelpers(HashSet<string> calls,StringBuilder output)
    {
        foreach(var site in _countConversions){var helper=CountHelper(site);if(!calls.Contains(helper))continue;
            output.AppendLine($$"""
                #ifndef WIICOMPILED_{{helper}}
                #define WIICOMPILED_{{helper}}
                #include <cstdint>
                static inline uint32_t {{helper}}(uint32_t count) {
                    return count=={{_original.Symbol(site.Count)}}u ? {{_expanded.Symbol(site.Count)}}u : count;
                }
                #endif
                """);}
    }
}
