using System.Buffers.Binary;
using Translator.Core.Analysis.BasicBlocks;
using Translator.Core.Disassembly;
using Translator.Core.Ir;
using Translator.Core.Lifting;
using Translator.Core.Loading;

namespace Translator.Core.Translation;

public sealed record GuestFillArray(string Array, string Stride, string Count, int Width, string? ValueRegister = null, int StartIndex = 0);
public sealed record GuestFillConstant(string Register, uint Value);
public sealed record GuestFillAddress(string Register, string Array);
public sealed record GuestFillCopy(string Register, string Source);
public sealed record GuestArrayFill(uint Begin, IReadOnlyList<uint> ExpectedWords,
    string BaseRegister, string ValueRegister, IReadOnlyList<GuestFillArray> Arrays, IReadOnlyList<GuestFillAddress>? Addresses = null, IReadOnlyList<GuestFillConstant>? Constants = null,
    IReadOnlyList<GuestFillCopy>? Copies = null);

public sealed partial class GuestLayoutRelocations
{
    private readonly GuestArrayFill[] _arrayFills;
    private static uint FillEnd(GuestArrayFill fill) => checked(fill.Begin + (uint)fill.ExpectedWords.Count * 4);
    private static bool IsGpr(string register) => register.Length >= 2 && register[0] == 'r' &&
        int.TryParse(register.AsSpan(1), out var index) && index is >= 0 and < 32;

    private static bool IsFpr(string register) => register.Length >= 2 && register[0] == 'f' &&
        int.TryParse(register.AsSpan(1), out var index) && index is >= 0 and < 32;

    private void ValidateFillDefinitions(GuestLayoutReference[] references)
    {
        foreach (var fill in _arrayFills)
        {
            if (fill.Begin % 4 != 0 || fill.ExpectedWords.Count == 0 || fill.Arrays.Count == 0 ||
                !IsGpr(fill.BaseRegister) || fill.BaseRegister == "r0" || !(IsGpr(fill.ValueRegister) || IsFpr(fill.ValueRegister)))
                throw new InvalidDataException("Invalid guest array-fill definition.");
            var values = fill.Arrays.Select(array => array.ValueRegister ?? fill.ValueRegister).Append(fill.ValueRegister).ToHashSet();
            if (values.Any(register => !(IsGpr(register) || IsFpr(register))))
                throw new InvalidDataException("Invalid guest fill value register.");
            var addresses = fill.Addresses ?? Array.Empty<GuestFillAddress>();
            if (addresses.Select(address => address.Register).Distinct().Count() != addresses.Count ||
                addresses.Any(address => !IsGpr(address.Register) || address.Register == fill.BaseRegister ||
                    values.Contains(address.Register) ||
                    !(address.Register == "r0" || int.Parse(address.Register.AsSpan(1)) is >= 3 and <= 12) ||
                    !fill.Arrays.Any(array => array.Array == address.Array)))
                throw new InvalidDataException("Guest fill address setup must use distinct volatile registers and declared arrays.");
            var constants=fill.Constants ?? Array.Empty<GuestFillConstant>();
            if(constants.Select(item=>item.Register).Distinct().Count()!=constants.Count ||
                constants.Any(item=>!IsGpr(item.Register) || item.Register==fill.BaseRegister || values.Contains(item.Register) ||
                    !(item.Register=="r0" || int.Parse(item.Register.AsSpan(1)) is >=3 and <=12) ||
                    addresses.Any(address=>address.Register==item.Register)))
                throw new InvalidDataException("Guest fill constants require distinct volatile registers independent of the stores.");
            var copies = fill.Copies ?? Array.Empty<GuestFillCopy>();
            if (copies.Select(copy => copy.Register).Distinct().Count() != copies.Count ||
                copies.Any(copy => !IsGpr(copy.Register) || copy.Register is "r1" or "r2" ||
                    copy.Register == fill.BaseRegister || values.Contains(copy.Register) || copy.Source != fill.BaseRegister ||
                    addresses.Any(address => address.Register == copy.Register) || constants.Any(item => item.Register == copy.Register)))
                throw new InvalidDataException("Guest fill copies must preserve the unchanged base in distinct registers independent of stores and setup.");
            foreach (var layout in new[] { _original, _expanded })
            {
                var intervals = new List<(int Begin, int End)>();
                foreach (var array in fill.Arrays)
                {
                    var begin = layout.Symbol(array.Array); var stride = layout.Symbol(array.Stride);
                    var count = layout.Symbol(array.Count);
                    if (begin < 0 || count <= 0 || array.StartIndex < 0 || array.StartIndex >= count ||
                        (IsFpr(array.ValueRegister ?? fill.ValueRegister) ? array.Width is not (4 or 8) : array.Width is not (1 or 2 or 4)) ||
                        stride < array.Width || stride % array.Width != 0 || begin % array.Width != 0)
                        throw new InvalidDataException("Guest array-fill requires aligned scalar array fields.");
                    for (var index=array.StartIndex;index<count;index++)
                    {
                        var offset=checked(begin+stride*index);
                        intervals.Add((offset,checked(offset+array.Width)));
                    }
                }
                intervals.Sort((left,right)=>left.Begin.CompareTo(right.Begin));
                for(var index=1;index<intervals.Count;index++)
                    if(intervals[index].Begin<intervals[index-1].End)
                        throw new InvalidDataException("Overlapping guest fill array fields.");
            }
            if (fill.Arrays.Any(array => _expanded.Symbol(array.Count) < _original.Symbol(array.Count)) ||
                fill.Arrays.Select(array => _original.Symbol(array.Count)).Distinct().Count() != 1 ||
                fill.Arrays.Select(array => _expanded.Symbol(array.Count)).Distinct().Count() != 1)
                throw new InvalidDataException("Guest fill arrays must grow together.");
            var endPc = FillEnd(fill);
            if (references.Any(site => site.Instruction >= fill.Begin && site.Instruction < endPc) ||
                _arrayCopies.Any(copy => copy.Begin < endPc && CopyEnd(copy) > fill.Begin) ||
                _rankTables.Any(table => table.Lookups.Any(site => site.Instruction >= fill.Begin && site.Instruction < endPc)) ||
                _boundedAccesses.Any(site => site.Instruction >= fill.Begin && site.Instruction < endPc) ||
                _arrayFills.Count(other => other.Begin < endPc && FillEnd(other) > fill.Begin) != 1)
                throw new InvalidDataException("Overlapping guest array-fill relocations.");
        }
    }

    private void ValidateFills(ProgramImage image)
    {
        foreach (var fill in _arrayFills)
        {
            var expected = FillStores(fill, _original).ToArray();
            var observed = new List<IrStore>();
            var observedAddresses = new List<IrBinary>();
            var observedConstants = new List<IrAssign>();
            var observedCopies = new List<GuestFillCopy>();
            for (var index = 0; index < fill.ExpectedWords.Count; index++)
            {
                var pc = checked(fill.Begin + (uint)index * 4);
                var word = BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(pc, 4), 4));
                if (word != fill.ExpectedWords[index]) throw new InvalidDataException($"Guest array-fill word mismatch at 0x{pc:X8}.");
                foreach (var node in new PpcLifter().Lift(new[] { PpcDecoder.Decode(pc, word) })[0].Ir)
                {
                    if (node is IrComment) continue;
                    if (node is IrStore store) observed.Add(store);
                    else if (node is IrAssign { Value.RegisterName: { } source } assignmentCopy)
                        observedCopies.Add(new(assignmentCopy.Destination, source));
                    else if (node is IrBinary { Op: "or", Left.RegisterName: { } left, Right.RegisterName: { } right } binaryCopy && left == right)
                        observedCopies.Add(new(binaryCopy.Destination, left));
                    else if (node is IrBinary address) observedAddresses.Add(address);
                    else if (node is IrAssign constant) observedConstants.Add(constant);
                    else throw new InvalidDataException("Guest array-fill span contains effects other than declared scalar stores and addresses.");
                }
            }
            if (!observed.SequenceEqual(expected) || !observedAddresses.SequenceEqual(FillAddresses(fill, _original)) ||
                !observedConstants.SequenceEqual(FillConstants(fill)) || !observedCopies.SequenceEqual(fill.Copies ?? Array.Empty<GuestFillCopy>()))
                throw new InvalidDataException($"Unproven ordered guest array fill at 0x{fill.Begin:X8}.");
        }
    }

    private static IEnumerable<IrStore> FillStores(GuestArrayFill fill, GuestLayouts layout)
    {
        for (var index = 0; index < layout.Symbol(fill.Arrays[0].Count); index++)
            foreach (var array in fill.Arrays)
                if (index >= array.StartIndex) yield return new IrStore(new(fill.BaseRegister, checked(layout.Symbol(array.Array) + index * layout.Symbol(array.Stride))),
                    IrValue.Register(array.ValueRegister ?? fill.ValueRegister), array.Width);
    }

    private static IEnumerable<IrBinary> FillAddresses(GuestArrayFill fill, GuestLayouts layout) =>
        (fill.Addresses ?? Array.Empty<GuestFillAddress>()).Select(address =>
            new IrBinary(address.Register, IrValue.Register(fill.BaseRegister), IrValue.Imm(layout.Symbol(address.Array)), "add"));

    private static IEnumerable<IrAssign> FillConstants(GuestArrayFill fill) =>
        (fill.Constants ?? Array.Empty<GuestFillConstant>()).Select(item=>
            new IrAssign(item.Register,IrValue.Imm(unchecked((int)item.Value))));

    private IReadOnlyList<IrInstruction>? ApplyFill(PpcInstruction origin, IReadOnlyList<IrInstruction> instructions)
    {
        foreach (var fill in _arrayFills)
        {
            if (origin.Address < fill.Begin || origin.Address >= FillEnd(fill)) continue;
            if (origin.Word != fill.ExpectedWords[checked((int)(origin.Address-fill.Begin)/4)])
                throw new InvalidDataException($"Guest array-fill instruction mismatch at 0x{origin.Address:X8}.");
            if (Players == OriginalPlayers) return instructions;
            return origin.Address == fill.Begin ? FillStores(fill, _expanded).Cast<IrInstruction>().Concat(FillAddresses(fill, _expanded)).Concat(FillConstants(fill))
                .Concat((fill.Copies ?? Array.Empty<GuestFillCopy>()).Select(copy => new IrAssign(copy.Register, IrValue.Register(copy.Source)))).ToArray() : Array.Empty<IrInstruction>();
        }
        return null;
    }

    private void ValidateFillControlFlow(IReadOnlyList<PpcInstruction> instructions, IReadOnlyList<BasicBlock> blocks)
    {
        foreach (var fill in _arrayFills)
        {
            var end = FillEnd(fill); var inside = instructions.Count(instruction => instruction.Address >= fill.Begin && instruction.Address < end);
            if (inside != 0 && (inside != fill.ExpectedWords.Count || blocks.Any(block => block.StartAddress > fill.Begin && block.StartAddress < end)))
                throw new InvalidDataException($"Guest array-fill span is partial or has an interior branch target at 0x{fill.Begin:X8}.");
        }
    }
}
