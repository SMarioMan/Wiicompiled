using System.Buffers.Binary;
using System.Collections.Concurrent;
using Translator.Core.Analysis.BasicBlocks;
using Translator.Core.Disassembly;
using Translator.Core.Ir;
using Translator.Core.Lifting;
using Translator.Core.Loading;

namespace Translator.Core.Translation;

public sealed record GuestArrayFieldCopy(int Source, int Destination, int Width);
public sealed record GuestArrayCopies(uint Begin, IReadOnlyList<uint> ExpectedWords,
    string BaseRegister, string Array, string Stride, string Count,
    IReadOnlyList<GuestArrayFieldCopy> Fields);

public sealed partial class GuestLayoutRelocations
{
    private readonly GuestArrayCopies[] _arrayCopies;
    private readonly ConcurrentDictionary<uint, Dictionary<string, CopyValue>> _copyScratch = new();
    private static uint CopyEnd(GuestArrayCopies copy) => checked(copy.Begin + (uint)copy.ExpectedWords.Count * 4);

    private void ValidateCopyDefinitions(GuestLayoutReference[] references)
    {
        if (_arrayCopies.Select(copy => copy.Begin).Distinct().Count() != _arrayCopies.Length)
            throw new InvalidDataException("Duplicate guest array-copy span.");
        foreach (var copy in _arrayCopies)
        {
            if (copy.Begin % 4 != 0 || copy.ExpectedWords.Count == 0 || copy.Fields.Count == 0 || copy.BaseRegister.Length < 2 ||
                !int.TryParse(copy.BaseRegister.AsSpan(1), out var register) ||
                copy.BaseRegister[0] != 'r' || register is < 3 or > 31)
                throw new InvalidDataException("Invalid guest array-copy definition.");
            foreach (var layout in new[] { _original, _expanded })
            {
                var stride = layout.Symbol(copy.Stride);
                var count = layout.Symbol(copy.Count);
                if (stride <= 0 || count <= 0 || layout.Symbol(copy.Array) < 0)
                    throw new InvalidDataException("Invalid guest array-copy shape.");
                _ = checked(layout.Symbol(copy.Array) + count * stride);
                foreach (var field in copy.Fields)
                    if (field.Width is not (1 or 2 or 4) || field.Source < 0 || field.Destination < 0 ||
                        field.Source > stride-field.Width || field.Destination > stride-field.Width)
                        throw new InvalidDataException("Guest array-copy field exceeds its element.");
            }
            var end = CopyEnd(copy);
            if (_expanded.Symbol(copy.Count) < _original.Symbol(copy.Count) ||
                copy.Fields.Any(source => copy.Fields.Any(destination =>
                    source.Source < destination.Destination+destination.Width &&
                    source.Source+source.Width > destination.Destination)))
                throw new InvalidDataException("Array-copy expansion requires nonoverlapping source/destination fields and cannot shrink capacity.");
            if (references.Any(site => site.Instruction >= copy.Begin && site.Instruction < end) ||
                _arrayCopies.Any(other => !ReferenceEquals(other, copy) && other.Begin < end && CopyEnd(other) > copy.Begin))
                throw new InvalidDataException("Overlapping guest array-copy relocations.");
        }
    }

    // A verified straight-line copy has no calls, flags, branches or other memory effects.
    // Retain original scratch-register outputs as well. No guest block identity moves.
    private void ValidateCopies(ProgramImage image)
    {
        foreach (var copy in _arrayCopies)
        {
            var facts = new Dictionary<string, CopyValue> { [copy.BaseRegister] = new("pointer", 0) };
            var observed = new List<(int Source, int Destination, int Width)>();
            var reads = new List<(int Source, int Width)>();
            CopyValue Value(IrValue value) => value.Kind == "const" ? new("constant", checked((int)(value.Constant ?? throw Invalid()))) :
                facts.TryGetValue(value.RegisterName!, out var fact) ? fact : throw Invalid();
            CopyValue Address(IrAddress address)
            {
                if (!facts.TryGetValue(address.Base, out var basis) || basis.Kind != "pointer") throw Invalid();
                return basis with { Offset = checked(basis.Offset + address.Offset) };
            }
            void Set(string destination, CopyValue value)
            {
                if (destination == copy.BaseRegister || !int.TryParse(destination.AsSpan(1), out var reg) ||
                    destination[0] != 'r' || reg is not (0 or >= 3 and <= 12)) throw Invalid();
                facts[destination] = value;
            }
            InvalidDataException Invalid() => new($"Unproven guest array copy at 0x{copy.Begin:X8}.");
            for (var index = 0; index < copy.ExpectedWords.Count; index++)
            {
                var pc = checked(copy.Begin+(uint)index*4);
                var word = BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(pc, 4), 4));
                if (word != copy.ExpectedWords[index]) throw Invalid();
                var instruction = PpcDecoder.Decode(pc, word);
                if (instruction.IsCall || instruction.IsReturn || instruction.IsConditionalBranch || instruction.IsUnconditionalBranch)
                    throw Invalid();
                foreach (var node in new PpcLifter().Lift(new[] { instruction })[0].Ir)
                    switch (node)
                    {
                        case IrComment: break;
                        case IrAssign assign: Set(assign.Destination, Value(assign.Value)); break;
                        case IrBinary binary:
                            var left = Value(binary.Left); var right = Value(binary.Right);
                            if (binary.Op == "add" && right.Kind == "constant" && left.Kind is "pointer" or "constant")
                                Set(binary.Destination, left with { Offset = checked(left.Offset+right.Offset) });
                            else if (binary.Op == "mul" && left.Kind == "constant" && right.Kind == "constant")
                                Set(binary.Destination, new("constant", checked(left.Offset*right.Offset)));
                            else throw Invalid();
                            break;
                        case IrLoad load:
                            var offset = Address(load.Address).Offset;
                            reads.Add((offset, load.SizeBytes));
                            Set(load.Destination, new("loaded", offset, load.SizeBytes)); break;
                        case IrStore store:
                            var source = Value(store.Source);
                            if (source.Kind != "loaded" || source.Width != store.SizeBytes) throw Invalid();
                            observed.Add((source.Offset, Address(store.Address).Offset, store.SizeBytes)); break;
                        default: throw Invalid();
                    }
            }
            var expected = new List<(int, int, int)>();
            for (var player = 0; player < _original.Symbol(copy.Count); player++)
                foreach (var field in copy.Fields)
                {
                    var element = checked(_original.Symbol(copy.Array)+player*_original.Symbol(copy.Stride));
                    expected.Add((checked(element+field.Source), checked(element+field.Destination), field.Width));
                }
            if (!observed.SequenceEqual(expected) || !reads.SequenceEqual(expected.Select(field => (field.Item1, field.Item3))))
                throw Invalid();
            _copyScratch[copy.Begin] = facts;
        }
    }

    public void ValidateCopyControlFlow(IReadOnlyList<PpcInstruction> instructions, IReadOnlyList<BasicBlock> blocks)
    {
        ValidateFillControlFlow(instructions, blocks);
        foreach (var copy in _arrayCopies)
        {
            var end = CopyEnd(copy);
            var inside = instructions.Count(i => i.Address >= copy.Begin && i.Address < end);
            if (inside == 0) continue;
            if (inside != copy.ExpectedWords.Count || blocks.Any(block => block.StartAddress > copy.Begin && block.StartAddress < end))
                throw new InvalidDataException($"Guest array-copy span is a partial function or has an interior branch target at 0x{copy.Begin:X8}.");
        }
    }

    private IReadOnlyList<IrInstruction> ExpandCopies(GuestArrayCopies copy)
    {
        var result = new List<IrInstruction>();
        for (var player = 0; player < _expanded.Symbol(copy.Count); player++)
            for (var index = 0; index < copy.Fields.Count; index++)
            {
                var field = copy.Fields[index];
                var element = checked(_expanded.Symbol(copy.Array)+player*_expanded.Symbol(copy.Stride));
                var value = $"layoutcopy_{copy.Begin:X8}_{player}_{index}";
                result.Add(new IrLoad(value, new(copy.BaseRegister, checked(element+field.Source)), field.Width));
                result.Add(new IrStore(new(copy.BaseRegister, checked(element+field.Destination)), IrValue.Register(value), field.Width));
            }
        if (!_copyScratch.TryGetValue(copy.Begin, out var scratch))
            throw new InvalidDataException("Guest array copies must be image-validated before application.");
        foreach (var (register, value) in scratch)
        {
            if (register == copy.BaseRegister) continue;
            switch (value.Kind)
            {
                case "constant": result.Add(new IrAssign(register, IrValue.Imm(value.Offset))); break;
                case "pointer": result.Add(new IrBinary(register, IrValue.Register(copy.BaseRegister), IrValue.Imm(value.Offset), "add")); break;
                case "loaded":
                    var offset = checked(_expanded.Symbol(copy.Array)+value.Offset-_original.Symbol(copy.Array));
                    result.Add(new IrLoad(register, new(copy.BaseRegister, offset), value.Width)); break;
                default: throw new InvalidDataException("Unknown proven array-copy scratch value.");
            }
        }
        return result;
    }

    private sealed record CopyValue(string Kind, int Offset, int Width = 0);
}
