using System.Text;
using System.Text.Json;

namespace Translator.Core.Translation;

public sealed partial class GuestLayoutRelocations
{
    public object DescribeRetiredAccessMap() => new {
        Format = "retired-layout-map-v1", OriginalPlayers, Players,
        Types = _expanded.Types.OrderBy(x => x.Key, StringComparer.Ordinal).Select(pair => new {
            Name = pair.Key, OldSize = _original.Types[pair.Key].Size, NewSize = pair.Value.Size,
            Fields = pair.Value.Fields.OrderBy(x => x.Value.Offset).Select(field => {
                var old = _original.Types[pair.Key].Fields[field.Key];
                return new {
                    Name = field.Key, Old = old, New = field.Value,
                    Moved = old.Offset != field.Value.Offset || old.Stride != field.Value.Stride,
                    OverlappingCurrentFields = pair.Value.Fields.Where(other =>
                        other.Value.Offset < old.Offset + old.Size && other.Value.Offset + other.Value.Size > old.Offset)
                        .Select(other => other.Key).ToArray()
                };
            }).ToArray()
        }).ToArray(),
        References = _references.Values.SelectMany(x => x).OrderBy(x => x.Instruction).ToArray()
    };

    private string EmitRetiredAccessRegistration()
    {
        var source = new StringBuilder("\n#if defined(__cplusplus) && defined(MKW_ENABLE_LAYOUT_ACCESS_AUDIT)\n");
        source.AppendLine("#include \"guest_abi/layout_access_audit.h\"");
        source.AppendLine("inline void RegisterGeneratedLayoutAccessAuditMap() {");
        foreach (var (name, expanded) in _expanded.Types.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (expanded.Fields.Count == 0) continue;
            var original = _original.Types[name];
            var identifier = "audit_fields_" + name;
            source.AppendLine($"    static constexpr LayoutAccessAudit::Field {identifier}[] = {{");
            foreach (var (fieldName, field) in expanded.Fields.OrderBy(x => x.Value.Offset))
            {
                var old = original.Fields[fieldName];
                var perRacer = old.Count == OriginalPlayers && field.Count == Players ? "true" : "false";
                source.AppendLine($"        {{{JsonSerializer.Serialize(fieldName)}, {old.Offset}u, {old.Size}u, {old.Count}u, {old.Stride}u, {field.Offset}u, {field.Size}u, {field.Count}u, {field.Stride}u, {perRacer}}},");
            }
            source.AppendLine("    };");
            source.AppendLine($"    LayoutAccessAudit::RegisterType({{{JsonSerializer.Serialize(name)}, {original.Size}u, {expanded.Size}u, {identifier}}});");
        }
        foreach (var reference in _references.Values.SelectMany(x => x).OrderBy(x => x.Instruction))
        {
            if (reference.Operand == "offset" && reference.Scale == -1 && reference.Addend == 0 &&
                (reference.ExpectedWord & 0xFFFF0000u) == 0x94210000u &&
                reference.Symbol.EndsWith(".size", StringComparison.Ordinal))
            {
                var frame = reference.Symbol[..^5];
                if (_expanded.Types.TryGetValue(frame, out var stackType) && stackType.Fields.Count != 0)
                    source.AppendLine($"    LayoutAccessAudit::BindStackSite(0x{reference.Instruction:X8}u, {JsonSerializer.Serialize(frame)});");
            }
            // A declared load/store offset provides field intent. Arithmetic,
            // composite symbols and end pointers require separate provenance;
            // they must not be guessed into high-confidence bindings.
            var parts = reference.Symbol.Split('.');
            var opcode = reference.ExpectedWord >> 26;
            if (reference.Operand != "offset" || reference.Scale != 1 || parts.Length != 3 || parts[2] != "offset" ||
                opcode is not (32 or 34 or 36 or 38 or 40 or 42 or 44 or 48 or 50 or 52 or 54) ||
                !_original.Types.TryGetValue(parts[0], out var type) || !type.Fields.TryGetValue(parts[1], out var field) ||
                reference.Addend < 0 || reference.Addend >= field.Size) continue;
            source.AppendLine($"    LayoutAccessAudit::BindSite({{.pc=0x{reference.Instruction:X8}u, .type={JsonSerializer.Serialize(parts[0])}, .field={JsonSerializer.Serialize(parts[1])}, .rule=\"manifest:0x{reference.Instruction:X8}\"}});");
        }
        source.AppendLine("}\n#endif");
        return source.ToString();
    }
}
