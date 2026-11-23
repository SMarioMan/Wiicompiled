using System.Buffers.Binary;
using Translator.Core.Disassembly;
using Translator.Core.Ir;
using Translator.Core.Lifting;
using Translator.Core.Loading;

namespace Translator.Core.Translation;

/// <summary>
/// Checked byte-domain operations whose signed or unsigned range is exceeded in an expanded layout.
/// Derived loads retain the guest header ABI; selected index masks and local
/// ordinal arrays widen in translated IR, before SSA and register analysis.
/// </summary>
public sealed record GuestByteOperation(uint Instruction, uint ExpectedWord, string Kind, string Count,
    string? Field = null, string? Source = null, int Factor = 1, int Elements = 0, int AvailableBytes = 0);

public sealed partial class GuestLayoutRelocations
{
    private readonly GuestByteOperation[] _byteOperations;

    private void ValidateByteOperations()
    {
        var seen = new HashSet<uint>();
        foreach (var site in _byteOperations)
        {
            if (site.Instruction % 4 != 0 || !seen.Add(site.Instruction) || HasOtherWordRewrite(site.Instruction) ||
                _wordComparisons.Any(s=>site.Instruction>=s.Begin && site.Instruction<=WordComparePc(s)) ||
                _wordTables.Any(t=>t.Loads.Any(s=>s.Instruction==site.Instruction)) ||
                _countConversions.Any(s=>s.Instruction==site.Instruction) ||
                _original.Symbol(site.Count) is < 1 or > 255 || _expanded.Symbol(site.Count) < _original.Symbol(site.Count))
                throw new InvalidDataException("Invalid or overlapping byte-domain operation.");
            if (site.Kind == "derived-count")
            {
                if (site.Field is null || site.Source is null || site.Factor is < 1 or > 65535 || site.Elements != 0 || site.AvailableBytes != 0 ||
                    _original.Symbol(site.Field) < 0 || _expanded.Symbol(site.Source) < 0)
                    throw new InvalidDataException("Derived byte counts require checked source/field offsets and a positive factor.");
            }
            else if (site.Kind is "scratch-load16" or "scratch-store16")
            {
                if (site.Field is not null || site.Source is not null || site.Factor != 1 ||
                    site.Elements is < 1 or > 255 || site.AvailableBytes < site.Elements * 2 ||
                    _expanded.Symbol(site.Count) > ushort.MaxValue)
                    throw new InvalidDataException("Wide local ordinal storage exceeds its proven range or scratch bounds.");
            }
            else if (site.Kind is not ("index32" or "offset32" or "rank8" or "recipient-aid" or "recipient-test" or "legacy-aid-bit") ||
                     site.Field is not null || site.Source is not null ||
                     site.Factor != 1 || site.Elements != 0 || site.AvailableBytes != 0)
                throw new InvalidDataException("Invalid byte-domain index operation.");
            if (site.Kind == "rank8" && _expanded.Symbol(site.Count) > 254)
                throw new InvalidDataException("Unsigned byte ranks retain 0xFF as the none sentinel.");
            if (site.Kind is "recipient-aid" or "recipient-test" or "legacy-aid-bit" && _original.Symbol(site.Count) != 32)
                throw new InvalidDataException("A forwarded recipient or legacy AID bit requires a 32-bit stock mask.");
        }
    }

    private static IReadOnlyList<IrInstruction> RewriteByteOperation(GuestByteOperation site, PpcInstruction instruction,
        IReadOnlyList<IrInstruction> nodes, int sourceOffset, int fieldOffset)
    {
        InvalidDataException Invalid() => new($"Unproven {site.Kind} instruction/IR at 0x{site.Instruction:X8}.");
        if (instruction.Word != site.ExpectedWord) throw Invalid();
        var word = instruction.Word;
        var rt = $"r{(word >> 21) & 31}";
        var ra = $"r{(word >> 16) & 31}";
        if (site.Kind is "recipient-aid" or "recipient-test" or "legacy-aid-bit")
        {
            var index = IrValue.Register($"r{(word >> 11) & 31}");
            var source = IrValue.Register(rt);
            if (site.Kind is "recipient-aid" or "legacy-aid-bit")
            {
                if (word >> 26 != 31 || (word & 2047) != 48 ||
                    nodes.Count != 1 || nodes[0] != new IrBinary(ra, source, index, "ppc_slw")) throw Invalid();
                if (site.Kind == "recipient-aid") return new IrInstruction[] { new IrAssign(ra, index) };
                // PPC slw ignores shift bits above bit 5. Reject high IDs before
                // using the low word so AID 64 cannot alias AID 0.
                var valid = $"addr_layout_aid_{instruction.Address:X8}_valid_loc";
                return new IrInstruction[] {
                    new IrBinary(valid, index, IrValue.Imm(5), "shr"),
                    new IrCall(valid, "PPC_Cntlzw", new[] { IrValue.Register(valid) }),
                    new IrBinary(valid, IrValue.Register(valid), IrValue.Imm(5), "shr"),
                    new IrBinary(valid, source, IrValue.Register(valid), "mul"),
                    new IrBinary(ra, IrValue.Register(valid), index, "ppc_slw") };
            }
            var condition = new IrSetCrField(0, IrValue.Register(ra), IrValue.Imm(0), false);
            if (word >> 26 != 31 || (word & 2047) != 57 || nodes.Count != 2 ||
                nodes[0] != new IrBinary(ra, source, index, "and") || nodes[1] != condition) throw Invalid();
            // The existing branches expect a nonzero result and CR0.GT for a match.
            return new IrInstruction[] {
                new IrBinary(ra, source, index, "xor"),
                new IrCall(ra, "PPC_Cntlzw", new[] { IrValue.Register(ra) }),
                new IrBinary(ra, IrValue.Register(ra), IrValue.Imm(5), "shr"),
                condition };
        }
        if (site.Kind == "rank8")
        {
            if (word >> 26 != 31 || (word >> 1 & 1023) != 954 || (word & 1) != 0 ||
                nodes.Count != 1 || nodes[0] is not IrBinary { Op: "sext" } sign ||
                sign.Destination != ra || sign.Left.RegisterName != rt || sign.Right.Constant != 8) throw Invalid();
            var raw = $"addr_layout_byte_rank_{instruction.Address:X8}_raw_loc";
            var sentinel = $"addr_layout_byte_rank_{instruction.Address:X8}_none_loc";
            // Preserve the game's -1 sentinel while making valid one-based
            // positions 128..254 positive. No guest flags or other GPRs change.
            return new IrInstruction[] {
                new IrBinary(raw, IrValue.Register(rt), IrValue.Imm(255), "and"),
                new IrBinary(sentinel, IrValue.Register(raw), IrValue.Imm(1), "add"),
                new IrBinary(sentinel, IrValue.Register(sentinel), IrValue.Imm(256), "divu"),
                new IrBinary(sentinel, IrValue.Imm(0), IrValue.Register(sentinel), "sub"),
                new IrBinary(ra, IrValue.Register(raw), IrValue.Register(sentinel), "or") };
        }
        if (site.Kind == "derived-count")
        {
            if (word >> 26 != 34 || ra == "r0" || nodes.Count != 1 ||
                nodes[0] is not IrLoad { SizeBytes: 1 } load || load.Destination != rt ||
                load.Address.Base != ra || load.Address.Offset != fieldOffset) throw Invalid();
            return new IrInstruction[] { load with { Address = new IrAddress(ra, sourceOffset) },
                new IrBinary(rt, IrValue.Register(rt), IrValue.Imm(site.Factor), "mul") };
        }
        if (site.Kind is "index32" or "offset32")
        {
            var shift = site.Kind == "index32" ? 0 : 3;
            var mask = 255 << shift;
            if (word >> 26 != 21 || (word & 1) != 0 || (word >> 11 & 31) != shift ||
                (word >> 6 & 31) != 24-shift || (word >> 1 & 31) != 31-shift ||
                nodes.Count != (shift == 0 ? 1 : 2) ||
                nodes[^1] is not IrBinary { Op: "and" } masked || masked.Destination != ra || masked.Right.Constant != mask)
                throw Invalid();
            if (shift == 0 && masked.Left.RegisterName != rt || shift != 0 &&
                (nodes[0] is not IrBinary { Op: "rotl" } rotated || rotated.Left.RegisterName != rt ||
                 rotated.Right.Constant != shift || masked.Left.RegisterName != rotated.Destination)) throw Invalid();
            return shift == 0 ? new IrInstruction[] { new IrAssign(ra, IrValue.Register(rt)) } :
                new IrInstruction[] { new IrBinary(ra, IrValue.Register(rt), IrValue.Imm(shift), "shl") };
        }
        var write = site.Kind == "scratch-store16";
        var rb = $"r{(word >> 11) & 31}";
        if (word >> 26 != 31 || (word >> 1 & 1023) != (write ? 215u : 87u) || ra == "r0" ||
            nodes.Count != 2 || nodes[0] is not IrBinary { Op: "add" } address ||
            address.Left.RegisterName != ra || address.Right.RegisterName != rb) throw Invalid();
        var scaled = $"addr_layout_byte_index_{instruction.Address:X8}_loc";
        var result = new List<IrInstruction> {
            new IrBinary(scaled, IrValue.Register(rb), IrValue.Imm(1), "shl"),
            address with { Right = IrValue.Register(scaled) }
        };
        if (write)
        {
            if (nodes[1] is not IrStore { SizeBytes: 1 } store || store.Source.RegisterName != rt ||
                store.Address != new IrAddress(address.Destination, 0)) throw Invalid();
            result.Add(store with { SizeBytes = 2 });
        }
        else
        {
            if (nodes[1] is not IrLoad { SizeBytes: 1 } load || load.Destination != rt ||
                load.Address != new IrAddress(address.Destination, 0)) throw Invalid();
            result.Add(load with { SizeBytes = 2 });
        }
        return result;
    }

    private void ValidateByteOperationImage(ProgramImage image)
    {
        foreach (var site in _byteOperations)
        {
            var word = BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(site.Instruction, 4), 4));
            var instruction = PpcDecoder.Decode(site.Instruction, word);
            _ = RewriteByteOperation(site, instruction, new PpcLifter().Lift(new[] { instruction })[0].Ir,
                site.Source is null ? 0 : _expanded.Symbol(site.Source), site.Field is null ? 0 : _original.Symbol(site.Field));
        }
    }

    private IReadOnlyList<IrInstruction>? ApplyByteOperation(PpcInstruction instruction, IReadOnlyList<IrInstruction> nodes)
    {
        var site = _byteOperations.FirstOrDefault(s=>s.Instruction==instruction.Address);
        if (site is null) return null;
        var widened = RewriteByteOperation(site, instruction, nodes, site.Source is null ? 0 : _expanded.Symbol(site.Source),
            site.Field is null ? 0 : _original.Symbol(site.Field));
        var originalLimit = site.Kind switch
        {
            "recipient-aid" or "recipient-test" or "legacy-aid-bit" => 32,
            "rank8" => 127,
            _ => byte.MaxValue
        };
        return _expanded.Symbol(site.Count) <= originalLimit ? nodes : widened;
    }
}
