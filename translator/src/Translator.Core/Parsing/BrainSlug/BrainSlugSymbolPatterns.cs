using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Translator.Core.Parsing.Dol;

namespace Translator.Core.Parsing.BrainSlug;

/// <summary>Resolves SDK aliases absent from the function map using BrainSlug's masked signatures.</summary>
public sealed class BrainSlugSymbolPatterns
{
    private sealed record Pattern(int Offset, byte[] Values, byte[] Masks, int AnchorOffset, byte[] Anchor);
    private readonly Dictionary<string, List<Pattern>> _patterns = new(StringComparer.Ordinal);

    public static BrainSlugSymbolPatterns Load(string directory)
    {
        var result = new BrainSlugSymbolPatterns();
        foreach (var file in Directory.EnumerateFiles(directory, "*.xml").Order(StringComparer.Ordinal))
        {
            using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            result.Add(XDocument.Load(reader));
        }
        return result;
    }

    public static BrainSlugSymbolPatterns Parse(string xml)
    {
        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var result = new BrainSlugSymbolPatterns();
        result.Add(XDocument.Load(reader));
        return result;
    }

    public uint? Resolve(string name, IEnumerable<DolSection> sections)
    {
        if (!_patterns.TryGetValue(name, out var patterns)) return null;
        var matches = new HashSet<uint>();
        foreach (var section in sections.Where(s => s.IsExecutable && s.HasData))
        foreach (var pattern in patterns)
        {
            var bytes = section.Data.Span;
            var cursor = 0;
            while (cursor <= bytes.Length - pattern.Anchor.Length)
            {
                var found = bytes[cursor..].IndexOf(pattern.Anchor);
                if (found < 0) break;
                var anchor = cursor + found;
                cursor = anchor + 1;
                var start = anchor - pattern.AnchorOffset;
                if (start < 0 || start + pattern.Values.Length > bytes.Length) continue;
                var matched = true;
                for (var i = 0; i < pattern.Values.Length; i++)
                    if ((bytes[start + i] & pattern.Masks[i]) != pattern.Values[i]) { matched = false; break; }
                if (!matched) continue;
                var candidate = (long)section.VirtualAddress + start - pattern.Offset;
                if (candidate < 0 || candidate > uint.MaxValue) continue;
                var address = (uint)candidate;
                if ((address & 3) != 0 || !section.Range.Contains(address)) continue;
                matches.Add(address);
                if (matches.Count > 1)
                    throw new InvalidDataException($"BrainSlug signature for '{name}' is ambiguous: {string.Join(", ", matches.Select(a => $"0x{a:X8}"))}.");
            }
        }
        if (matches.Count == 0) throw new InvalidDataException($"BrainSlug signature for '{name}' did not match this DOL.");
        return matches.Single();
    }

    private void Add(XDocument document)
    {
        foreach (var symbol in document.Root?.Elements("symbol") ?? [])
        {
            var name = (string?)symbol.Attribute("name") ?? throw new InvalidDataException("Unnamed BrainSlug symbol.");
            var data = symbol.Element("data");
            if (data is null) continue; // Derived data symbols need their own relocation resolver.
            var text = string.Concat(data.Value.Where(c => !char.IsWhiteSpace(c)));
            if (text.Length == 0 || text.Length % 2 != 0)
                throw new InvalidDataException($"Invalid signature for '{name}'.");
            var values = new byte[text.Length / 2];
            var masks = new byte[values.Length];
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '?') continue;
                var digit = text[i] switch
                {
                    >= '0' and <= '9' => text[i] - '0',
                    >= 'A' and <= 'F' => text[i] - 'A' + 10,
                    >= 'a' and <= 'f' => text[i] - 'a' + 10,
                    _ => throw new InvalidDataException($"Invalid hex digit in signature '{name}'.")
                };
                var shift = i % 2 == 0 ? 4 : 0;
                masks[i / 2] |= (byte)(15 << shift);
                values[i / 2] |= (byte)(digit << shift);
            }
            var bestStart = 0;
            var bestLength = 0;
            for (var i = 0; i < masks.Length;)
            {
                if (masks[i] != 255) { i++; continue; }
                var start = i;
                while (i < masks.Length && masks[i] == 255) i++;
                if (i - start > bestLength) { bestStart = start; bestLength = i - start; }
            }
            if (bestLength == 0) throw new InvalidDataException($"Signature '{name}' has no concrete-byte anchor.");
            var offsetText = (string?)symbol.Attribute("offset") ?? "0";
            var negative = offsetText.StartsWith('-');
            if (negative) offsetText = offsetText[1..];
            int offset;
            try
            {
                var magnitude = offsetText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? uint.Parse(offsetText[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    : uint.Parse(offsetText, CultureInfo.InvariantCulture);
                offset = checked((int)((negative ? -1L : 1L) * magnitude));
            }
            catch (Exception error) when (error is FormatException or OverflowException)
            { throw new InvalidDataException($"Invalid signature offset for '{name}'.", error); }
            if (!_patterns.TryGetValue(name, out var list)) _patterns.Add(name, list = []);
            list.Add(new Pattern(offset, values, masks, bestStart, values.AsSpan(bestStart, bestLength).ToArray()));
        }
    }
}
