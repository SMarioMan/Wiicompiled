using Translator.Core.Disassembly;
using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Translator.Core.Analysis.Ssa;
using System.Globalization;

namespace Translator.Core.Analysis;

public sealed record GuestLayoutCandidate(GuestLayoutReference Reference, int Before, int After, string ReceiverType, int IndexStride = 0);
public sealed record GuestLayoutAuditResult(IReadOnlyList<GuestLayoutCandidate> Candidates,
    int UnresolvedPointerOperations, string Note);

/// <summary>
/// Finds review candidates from declared receiver/global pointer types. It never applies patches.
/// Facts cross control-flow edges only when every reachable predecessor agrees.
/// Unknown operations discard affected facts; scaled indices require a declared, unchanged array stride.
/// </summary>
public sealed class GuestLayoutAudit
{
    private sealed record Fact(uint? Constant = null, string? Type = null, int Offset = 0, int IndexStride = 0, int Rotation = 0);
    private readonly Dictionary<string, GuestLayoutType> _definitions;
    private readonly GuestLayouts _original;
    private readonly GuestLayouts _expanded;
    private readonly IReadOnlyDictionary<uint, string> _globals;
    private readonly uint? _sda;
    private readonly uint? _sda2;

    public GuestLayoutAudit(IEnumerable<GuestLayoutType> definitions, int originalPlayers, int players,
        IReadOnlyDictionary<uint, string>? globalPointers = null, uint? sda = null, uint? sda2 = null)
    {
        var types = definitions.ToArray();
        _definitions = types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _original = new GuestLayouts(types, originalPlayers);
        _expanded = new GuestLayouts(types, players);
        _globals = globalPointers ?? new Dictionary<uint, string>();
        foreach (var type in _globals.Values) _ = _original.Types[type];
        _sda = sda;
        _sda2 = sda2;
    }

    public GuestLayoutAuditResult Analyze(FunctionDiscoveryResult function, string? receiverType = null)
    {
        if (receiverType is not null) _ = _original.Types[receiverType];
        var words = function.Instructions.ToDictionary(i => i.Address, i => i.Word);
        var candidates = new List<GuestLayoutCandidate>();
        var unresolved = 0;
        var nodes = function.LinearIr.Blocks.SelectMany(b => b.Instructions).ToArray();
        var invariantReceiver = !nodes.Any(i => Defines(i) == "r3" ||
            i is IrIndirectCall or IrUndefined || i is IrCall call && (CallWrites(call) & 8) != 0);
        var cfg = IrCfg.Build(function.LinearIr);
        var inputs = new Dictionary<string, Dictionary<string, Fact>>(StringComparer.OrdinalIgnoreCase);
        var outputs = new Dictionary<string, Dictionary<string, Fact>>(StringComparer.OrdinalIgnoreCase);
        var closedControlFlow = !nodes.Any(node => node is IrIndirectJump or IrUndefined);
        if (closedControlFlow)
        {
            // Start unvisited edges at top. Collect suggestions only after convergence:
            // a back edge may invalidate an initially plausible receiver or constant.
            var changed = true; var rounds = 0;
            while (changed)
            {
                if (++rounds > 10000) throw new InvalidDataException("Guest pointer analysis did not converge.");
                changed = false;
                foreach (var block in function.LinearIr.Blocks)
                {
                    var incoming = cfg.Predecessors(block.Label).Where(outputs.ContainsKey)
                        .Select(label => outputs[label]).ToList();
                    if (block.Label.Equals(function.LinearIr.EntryLabel, StringComparison.OrdinalIgnoreCase))
                        incoming.Add(Seeds(entry: true, invariant: false));
                    if (incoming.Count == 0) continue;
                    var facts = new Dictionary<string, Fact>(incoming[0], StringComparer.Ordinal);
                    foreach (var predecessor in incoming.Skip(1))
                        foreach (var name in facts.Keys.ToArray())
                            if (!predecessor.TryGetValue(name, out var fact) || fact != facts[name]) facts.Remove(name);
                    inputs[block.Label] = facts;
                    var next = RunBlock(block, facts, collect: false);
                    if (!outputs.TryGetValue(block.Label, out var previous) || previous.Count != next.Count ||
                        previous.Any(pair => !next.TryGetValue(pair.Key, out var fact) || fact != pair.Value))
                    {
                        outputs[block.Label] = next;
                        changed = true;
                    }
                }
            }
        }
        foreach (var block in function.LinearIr.Blocks)
            RunBlock(block, inputs.GetValueOrDefault(block.Label) ?? Seeds(
                block.Label.Equals(function.LinearIr.EntryLabel, StringComparison.OrdinalIgnoreCase), invariant: true), collect: true);
        return new GuestLayoutAuditResult(candidates, unresolved,
            "Review candidates only. No patches were applied; bounds, interior pointers and unknown control flow need separate audit.");

        Dictionary<string, Fact> Seeds(bool entry, bool invariant)
        {
            var facts = new Dictionary<string, Fact>(StringComparer.Ordinal);
            bool Stable(string register, int index) => entry || !nodes.Any(node =>
                Defines(node) == register || node is IrUndefined || node is IrCall call && (CallWrites(call) & (1u << index)) != 0);
            if (_sda is { } sda && Stable("r13", 13)) facts["r13"] = new Fact(Constant: sda);
            if (_sda2 is { } sda2 && Stable("r2", 2)) facts["r2"] = new Fact(Constant: sda2);
            if (receiverType is not null && (entry || invariant && invariantReceiver))
                facts["r3"] = new Fact(Type: receiverType);
            return facts;
        }

        Dictionary<string, Fact> RunBlock(IrBasicBlock block, Dictionary<string, Fact> input, bool collect)
        {
            var facts = new Dictionary<string, Fact>(input, StringComparer.Ordinal);
            var pending = new List<IrInstruction>();
            foreach (var node in block.Instructions)
            {
                if (node is IrTracePpc trace)
                {
                    for (var index = 0; index < pending.Count; index++) Process(pending[index], trace.Address, index);
                    pending.Clear();
                }
                else if (node is IrUndefined) facts.Clear();
                else if (node is IrBranch or IrJump or IrReturn or IrIndirectJump or IrJumpTable)
                {
                    // These occur after their trace, not before it. They have
                    // no relocatable field operand and cannot seed a new block.
                }
                else pending.Add(node);
            }

            Fact? Value(IrValue value) => value.Kind == "const" && value.Constant is { } constant
                ? new Fact(Constant: unchecked((uint)constant))
                : value.RegisterName is { } name ? facts.GetValueOrDefault(name) : null;
            Fact? Address(IrAddress address)
            {
                var basis = facts.GetValueOrDefault(address.Base);
                return basis?.Constant is { } constant ? new Fact(Constant: unchecked(constant + (uint)address.Offset)) : null;
            }
            void Set(string name, Fact? fact)
            {
                if (string.IsNullOrEmpty(name)) return;
                if (fact is null) facts.Remove(name); else facts[name] = fact;
            }
            void Propose(uint pc, int index, string operand, Fact? basis, int offset)
            {
                if (!collect) return;
                if (basis?.Type is not { } type) return;
                // An interior pointer can also be an array cursor. Rebasing a
                // cursor's final stride would skip the new elements; require review.
                if (basis.Offset != 0) { unresolved++; return; }
                var path = basis.IndexStride == 0 || IsIndexedArrayField(type, offset, basis.IndexStride)
                    ? Locate(type, offset) : null;
                if (path is null) { unresolved++; return; }
                if (checked(_original.Symbol(path.Value.Symbol) + path.Value.Addend) != offset)
                    throw new InvalidDataException("Layout audit produced an inconsistent original field path.");
                var after = checked(_expanded.Symbol(path.Value.Symbol) + path.Value.Addend);
                if (after == offset) return;
                var site = new GuestLayoutReference(pc, words[pc], index, operand, path.Value.Symbol, path.Value.Addend);
                candidates.Add(new GuestLayoutCandidate(site, offset, after, type, basis.IndexStride));
            }
            void Process(IrInstruction node, uint pc, int index)
            {
                switch (node)
                {
                    case IrAssign assign:
                        Set(assign.Destination, Value(assign.Value));
                        break;
                    case IrBinary binary:
                    {
                        var left = Value(binary.Left); var right = Value(binary.Right);
                        Fact? result = null;
                        if (binary.Op == "add" && left?.Type is { } type && binary.Right.Constant is { } immediate &&
                            immediate is >= int.MinValue and <= int.MaxValue)
                        {
                            Propose(pc, index, "right", left, (int)immediate);
                            result = left.IndexStride == 0 ? Narrow(type, checked(left.Offset + (int)immediate)) : null;
                        }
                        else if (left?.Constant is { } a && right?.Constant is { } b)
                        {
                            uint? constant = binary.Op switch
                            {
                                "add" => unchecked(a+b), "sub" => unchecked(a-b), "or" => a|b,
                                "and" => a&b, "mul" => unchecked(a*b), _ => null
                            };
                            if (constant is { } value) result = new Fact(Constant: value);
                        }
                        else if (binary.Op == "mul" &&
                            (left?.Constant is { } scaleLeft && right is null && binary.Right.RegisterName is not null && IsArrayStride(scaleLeft) ||
                             right?.Constant is { } scaleRight && left is null && binary.Left.RegisterName is not null && IsArrayStride(scaleRight)))
                        {
                            result = new Fact(IndexStride: checked((int)(left?.Constant ?? right!.Constant!.Value)));
                        }
                        else if (binary.Op is "shl" or "rotl" && left is null && binary.Left.RegisterName is not null &&
                            right?.Constant is > 0 and < 31 && IsArrayStride(1u << (int)right.Constant.Value))
                            result = binary.Op == "shl" ? new Fact(IndexStride: 1 << (int)right.Constant.Value)
                                : new Fact(Rotation: (int)right.Constant.Value);
                        else if (binary.Op == "and" && left is { Rotation: > 0 } && right?.Constant is { } mask &&
                            (mask & ((1u << left.Rotation) - 1)) == 0)
                            result = new Fact(IndexStride: 1 << left.Rotation);
                        else if (binary.Op == "add")
                        {
                            var pointer = left?.Type is not null ? left : right?.Type is not null ? right : null;
                            var scaled = pointer == left ? right : left;
                            if (pointer is { Offset: 0, IndexStride: 0 } && scaled is { Type: null, Constant: null, IndexStride: > 0 })
                                result = pointer with { IndexStride = scaled.IndexStride };
                            else if (collect && pointer is not null) unresolved++;
                        }
                        else if (collect && (left?.Type is not null || right?.Type is not null)) unresolved++;
                        Set(binary.Destination, result);
                        break;
                    }
                    case IrLoad load:
                        Propose(pc, index, "offset", facts.GetValueOrDefault(load.Address.Base), load.Address.Offset);
                        var address = Address(load.Address);
                        string? pointedType = null;
                        if (load.Destination.StartsWith("r", StringComparison.Ordinal) && load.SizeBytes == 4)
                        {
                            if (address?.Constant is { } location) _globals.TryGetValue(location, out pointedType);
                            var basis = facts.GetValueOrDefault(load.Address.Base);
                            if (pointedType is null && basis is { Type: not null, Offset: 0 } &&
                                (basis.IndexStride == 0 || IsIndexedArrayField(basis.Type, load.Address.Offset, basis.IndexStride)))
                                pointedType = PointerAt(basis.Type, load.Address.Offset);
                        }
                        Set(load.Destination, pointedType is null ? null : new Fact(Type: pointedType));
                        break;
                    case IrStore store:
                        Propose(pc, index, "offset", facts.GetValueOrDefault(store.Address.Base), store.Address.Offset);
                        break;
                    case IrCall call:
                        if (call.Target == "PPC_UpdateCarryAdd" && call.Arguments.Count == 3 &&
                            call.Arguments[1].Constant is { } carryOffset && carryOffset is >= int.MinValue and <= int.MaxValue)
                            Propose(pc, index, "argument:1", Value(call.Arguments[0]), (int)carryOffset);
                        var writes = CallWrites(call);
                        for (var register = 0; register < 32; register++)
                            if ((writes & (1u << register)) != 0) facts.Remove($"r{register}");
                        Set(call.Destination, null);
                        break;
                    case IrIndirectCall:
                        facts.Remove("r0");
                        for (var register = 3; register <= 12; register++) facts.Remove($"r{register}");
                        break;
                    default:
                        if (Defines(node) is { } destination) Set(destination, null);
                        break;
                }
            }
            return facts;
        }
    }

    private (string Symbol, int Addend)? Locate(string type, int offset)
    {
        var shape = _original.Types[type];
        if (offset == shape.Size) return ($"{type}.size", 0);
        if (offset < 0 || offset >= shape.Size || shape.Fields.Count == 0) return null;
        var field = shape.Fields.FirstOrDefault(p => offset >= p.Value.Offset && offset < p.Value.Offset+p.Value.Size);
        if (field.Value is null) return null; // ABI padding has no declared field identity.
        var definition = _definitions[type].Fields!.Single(f => f.Name == field.Key);
        var relative = offset-field.Value.Offset;
        var element = relative/field.Value.Stride;
        var inner = relative%field.Value.Stride;
        var prefix = $"{type}.{field.Key}.offset";
        for (var index = 0; index < element; index++) prefix += $"+{type}.{field.Key}.stride";
        var nested = Locate(definition.Type, inner);
        return nested is { } child ? ($"{prefix}+{child.Symbol}", child.Addend) : (prefix, inner);
    }

    // Only infer an index scale when a declared array keeps that stride.
    private bool IsArrayStride(uint scale) => scale <= int.MaxValue && _definitions.Values.Any(type =>
        type.Fields is not null && type.Fields.Any(field => (field.PerPlayer || _original.Types[type.Name].Fields[field.Name].Count > 1) &&
            _original.Types[type.Name].Fields[field.Name].Stride == scale &&
            _expanded.Types[type.Name].Fields[field.Name].Stride == scale));

    private bool IsIndexedArrayField(string type, int offset, int stride)
    {
        var shape = _original.Types[type];
        if (offset < 0 || offset >= shape.Size) return false;
        foreach (var field in shape.Fields)
        {
            if (offset < field.Value.Offset || offset >= field.Value.Offset + field.Value.Size) continue;
            var definition = _definitions[type].Fields!.Single(f => f.Name == field.Key);
            if ((definition.PerPlayer || field.Value.Count > 1) && field.Value.Stride == stride &&
                _expanded.Types[type].Fields[field.Key].Stride == stride) return true;
            return IsIndexedArrayField(definition.Type, (offset - field.Value.Offset) % field.Value.Stride, stride);
        }
        return false;
    }

    private string? PointerAt(string type, int offset)
    {
        var shape = _original.Types[type];
        foreach (var field in shape.Fields)
        {
            if (offset < field.Value.Offset || offset >= field.Value.Offset + field.Value.Size) continue;
            var definition = _definitions[type].Fields!.Single(member => member.Name == field.Key);
            var inner = (offset - field.Value.Offset) % field.Value.Stride;
            if (definition.PointerTo is not null) return inner == 0 ? definition.PointerTo : null;
            return PointerAt(definition.Type, inner);
        }
        return null;
    }

    private Fact? Narrow(string type, int offset)
    {
        var shape = _original.Types[type];
        if (offset < 0 || offset > shape.Size) return null;
        foreach (var field in shape.Fields)
        {
            if (field.Value.Count != 1 || offset < field.Value.Offset || offset >= field.Value.Offset+field.Value.Size) continue;
            var definition = _definitions[type].Fields!.Single(f => f.Name == field.Key);
            if (_original.Types[definition.Type].Fields.Count != 0)
                return Narrow(definition.Type, offset-field.Value.Offset);
        }
        return new Fact(Type: type, Offset: offset);
    }

    private static string? Defines(IrInstruction instruction) => instruction switch
    {
        IrAssign n => n.Destination, IrBinary n => n.Destination, IrLoad n => n.Destination,
        IrCall n => n.Destination, IrIndirectCall n => n.Destination, IrPhi n => n.Destination,
        _ => null
    };

    private static uint CallWrites(IrCall call) => call.Target.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
        uint.TryParse(call.Target.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)
        ? 0x1ff9u // Guest ABI: r0 and r3-r12 are volatile.
        : GuestHelperEffectCatalog.Analyze(call).GprWriteMask;
}
