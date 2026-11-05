namespace Translator.Core.Loading;

/// <summary>PowerPC ABI layouts, independent of the host compiler's pointer size and alignment.</summary>
public sealed record GuestLayoutField(string Name, string Type, int Count = 1, bool PerPlayer = false);
public sealed record GuestLayoutType(string Name, int Alignment, int Size = 0,
    IReadOnlyList<GuestLayoutField>? Fields = null);
public sealed record ResolvedGuestField(int Offset, int Size, int Count, int Stride);
public sealed record ResolvedGuestType(int Alignment, int Size,
    IReadOnlyDictionary<string, ResolvedGuestField> Fields);

/// <summary>
/// Calculates one contiguous array for the requested capacity and relocates all following fields.
/// Padding and arithmetic are explicit guest ABI facts; host sizeof/offsetof are never used.
/// </summary>
public sealed class GuestLayouts
{
    public int Players { get; }
    public IReadOnlyDictionary<string, ResolvedGuestType> Types { get; }

    public GuestLayouts(IEnumerable<GuestLayoutType> definitions, int players)
    {
        if (players is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(players));
        Players = players;
        var source = definitions.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var resolved = new Dictionary<string, ResolvedGuestType>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        ResolvedGuestType Resolve(string name)
        {
            if (resolved.TryGetValue(name, out var previous)) return previous;
            if (!source.TryGetValue(name, out var type))
                throw new InvalidDataException($"Unknown guest layout type: {name}.");
            if (!visiting.Add(name)) throw new InvalidDataException($"Recursive guest layout: {name}.");
            if (type.Alignment < 1 || (type.Alignment & (type.Alignment - 1)) != 0)
                throw new InvalidDataException($"Invalid guest alignment: {name}.");
            var fields = new Dictionary<string, ResolvedGuestField>(StringComparer.Ordinal);
            var size = type.Size;
            if (type.Fields is { } members)
            {
                if (size != 0 || members.Count == 0)
                    throw new InvalidDataException($"Compound layout {name} must specify fields and no scalar size.");
                foreach (var member in members)
                {
                    var element = Resolve(member.Type);
                    if (member.Count < 1 || element.Alignment > type.Alignment)
                        throw new InvalidDataException($"Invalid field alignment/count: {name}.{member.Name}.");
                    var count = checked(member.Count * (member.PerPlayer ? players : 1));
                    var offset = Align(size, element.Alignment);
                    var bytes = checked(element.Size * count);
                    fields.Add(member.Name, new ResolvedGuestField(offset, bytes, count, element.Size));
                    size = checked(offset + bytes);
                }
            }
            if (size < 1 || size % type.Alignment != 0 && type.Fields is null)
                throw new InvalidDataException($"Invalid scalar guest size: {name}.");
            var result = new ResolvedGuestType(type.Alignment, Align(size, type.Alignment), fields);
            resolved.Add(name, result);
            visiting.Remove(name);
            return result;
        }
        foreach (var name in source.Keys) Resolve(name);
        Types = resolved;
    }

    public int Symbol(string name)
    {
        if (name.Contains('+'))
            return name.Split('+').Aggregate(0, (sum, term) => checked(sum + Symbol(term.Trim())));
        if (name.Contains('/') || name.StartsWith("ceil(", StringComparison.Ordinal))
        {
            var ceiling = name.StartsWith("ceil(", StringComparison.Ordinal);
            if (ceiling && !name.EndsWith(')')) throw new InvalidDataException($"Invalid guest ceiling expression: {name}.");
            var quotient = (ceiling ? name[5..^1] : name).Split('/');
            if (quotient.Length != 2 || !int.TryParse(quotient[1], out var divisor) || divisor <= 0)
                throw new InvalidDataException($"Invalid guest layout divisor: {name}.");
            var dividend = Symbol(quotient[0].Trim());
            if (ceiling) return checked((int)(((long)dividend+divisor-1)/divisor));
            if (dividend % divisor != 0)
                throw new InvalidDataException($"Guest layout expression requires an exact quotient: {name} ({dividend}/{divisor}).");
            return dividend / divisor;
        }
        if (name == "players") return Players;
        var parts = name.Split('.');
        if (parts.Length == 2 && parts[1] == "size" && Types.TryGetValue(parts[0], out var type))
            return type.Size;
        if (parts.Length == 3 && Types.TryGetValue(parts[0], out type) &&
            type.Fields.TryGetValue(parts[1], out var field))
            return parts[2] switch
            {
                "offset" => field.Offset, "size" => field.Size, "count" => field.Count,
                "stride" => field.Stride, "end" => checked(field.Offset + field.Size),
                _ => throw new InvalidDataException($"Unknown guest layout symbol: {name}.")
            };
        throw new InvalidDataException($"Unknown guest layout symbol: {name}.");
    }

    private static int Align(int value, int alignment) =>
        checked((value + alignment - 1) / alignment * alignment);
}
