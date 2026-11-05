using Translator.Core.Disassembly;
using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Translation;

namespace Translator.Core.Analysis;

public sealed record GuestLayoutCandidate(GuestLayoutReference Reference, int Before, int After, string ReceiverType);
public sealed record GuestLayoutAuditResult(IReadOnlyList<GuestLayoutCandidate> Candidates,
    int UnresolvedPointerOperations, string Note);

/// <summary>
/// Finds review candidates from declared receiver/global pointer types. It never applies patches.
/// Facts are local to a basic block; unknown operations and guest calls discard affected facts.
/// Dynamic indices, interior-pointer changes and unproven control-flow merges are not guessed.
/// </summary>
public sealed class GuestLayoutAudit
{
    private sealed record Fact(uint? Constant = null, string? Type = null, int Offset = 0);
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
        // A receiver that is never written and crosses no guest calls is safe
        // to seed at every block (e.g. the original Scenario assignment).
        var nodes = function.LinearIr.Blocks.SelectMany(b => b.Instructions).ToArray();
        var invariantReceiver = !nodes.Any(i => Defines(i) == "r3" ||
            i is IrIndirectCall or IrUndefined || i is IrCall call && !PreservesGprs(call.Target));
        foreach (var block in function.LinearIr.Blocks)
        {
            var facts = new Dictionary<string, Fact>(StringComparer.Ordinal);
            if (_sda is { } sda) facts["r13"] = new Fact(Constant: sda);
            if (_sda2 is { } sda2) facts["r2"] = new Fact(Constant: sda2);
            if (receiverType is not null && (invariantReceiver || block.Label == function.LinearIr.EntryLabel))
                facts["r3"] = new Fact(Type: receiverType);
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
                if (basis?.Type is not { } type) return;
                // An interior pointer can also be an array cursor. Rebasing a
                // cursor's final stride would skip the new elements; require review.
                if (basis.Offset != 0) { unresolved++; return; }
                var path = Locate(type, offset);
                if (path is null) { unresolved++; return; }
                if (checked(_original.Symbol(path.Value.Symbol) + path.Value.Addend) != offset)
                    throw new InvalidDataException("Layout audit produced an inconsistent original field path.");
                var after = checked(_expanded.Symbol(path.Value.Symbol) + path.Value.Addend);
                if (after == offset) return;
                var site = new GuestLayoutReference(pc, words[pc], index, operand, path.Value.Symbol, path.Value.Addend);
                candidates.Add(new GuestLayoutCandidate(site, offset, after, type));
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
                            result = Narrow(type, checked(left.Offset + (int)immediate));
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
                        else if (left?.Type is not null || right?.Type is not null) unresolved++;
                        Set(binary.Destination, result);
                        break;
                    }
                    case IrLoad load:
                        Propose(pc, index, "offset", facts.GetValueOrDefault(load.Address.Base), load.Address.Offset);
                        var address = Address(load.Address);
                        Set(load.Destination, load.Destination.StartsWith("r", StringComparison.Ordinal) &&
                            load.SizeBytes == 4 && address?.Constant is { } location &&
                            _globals.TryGetValue(location, out var pointedType) ? new Fact(Type: pointedType) : null);
                        break;
                    case IrStore store:
                        Propose(pc, index, "offset", facts.GetValueOrDefault(store.Address.Base), store.Address.Offset);
                        break;
                    case IrCall call:
                        if (call.Target == "PPC_UpdateCarryAdd" && call.Arguments.Count == 3 &&
                            call.Arguments[1].Constant is { } carryOffset && carryOffset is >= int.MinValue and <= int.MaxValue)
                            Propose(pc, index, "argument:1", Value(call.Arguments[0]), (int)carryOffset);
                        if (!PreservesGprs(call.Target))
                        {
                            facts.Remove("r0");
                            for (var register = 3; register <= 12; register++) facts.Remove($"r{register}");
                        }
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
        }
        return new GuestLayoutAuditResult(candidates, unresolved,
            "Review candidates only. No patches were applied; unknown aliases, dynamic indices, bounds and inter-block facts need separate audit.");
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

    private static bool PreservesGprs(string helper) => helper is
        "PPC_UpdateCarryAdd" or "PPC_UpdateCarrySub" or "PPC_UpdateCarryShiftRight" or "PPC_GetCarry";
}
