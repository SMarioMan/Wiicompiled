using System.Globalization;
using System.Text;

namespace Translator.Core.Translation;

public sealed partial class GuestLayoutRelocations
{
    /// <summary>C-compatible constants for native glue and source mods using this exact guest ABI.</summary>
    public string EmitNativeConstants()
    {
        var source = new StringBuilder("#pragma once\n// Generated from the resolved guest layout; do not edit.\n");
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Define(string name, uint value)
        {
            if (!names.Add(name)) throw new InvalidDataException($"Guest layout C constant name collision: {name}.");
            source.Append("#define ").Append(name).Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).AppendLine("u");
        }
        static string Identifier(string value)
        {
            if (value.Length == 0 || !(char.IsAsciiLetter(value[0]) || value[0] == '_') ||
                value.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
                throw new InvalidDataException($"Guest layout name is not a C identifier: {value}.");
            return value.ToUpperInvariant();
        }
        Define("MKW_GUEST_LAYOUT_EXPLICIT", 1);
        Define("MKW_LAYOUT_PLAYER_CAPACITY", checked((uint)Players));
        Define("MKW_LAYOUT_ORIGINAL_PLAYER_CAPACITY", checked((uint)OriginalPlayers));
        foreach (var (name, type) in _expanded.Types.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var prefix = "MKW_LAYOUT_"+Identifier(name);
            Define(prefix+"_SIZE", checked((uint)type.Size));
            Define(prefix+"_ALIGNMENT", checked((uint)type.Alignment));
            foreach (var (fieldName, field) in type.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var member = prefix+"_"+Identifier(fieldName);
                Define(member+"_OFFSET", checked((uint)field.Offset));
                Define(member+"_SIZE", checked((uint)field.Size));
                Define(member+"_COUNT", checked((uint)field.Count));
                Define(member+"_STRIDE", checked((uint)field.Stride));
                Define(member+"_END", checked((uint)(field.Offset+field.Size)));
            }
        }
        if (_rankTables.Length != 0) Define("MKW_LAYOUT_RACE_WINNER_POINTS", RankPoints(0, Players, 1));
        return source.ToString();
    }
}
