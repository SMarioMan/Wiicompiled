namespace Translator.Core.Parsing.Kamek;

public static class KamekPpcEncoding
{
    internal static uint EncodeConditionalBranch(uint from, uint to, uint template)
    {
        if ((template & 0xFC00FFFEu) != 0x40000000u)
            throw new InvalidDataException("A conditional branch template must have an empty relative displacement.");
        var delta = (long)to - from;
        if ((delta & 3) != 0 || delta is < -0x8000 or > 0x7FFC)
            throw new InvalidDataException("A conditional branch requires an aligned target in rel14 range.");
        return template | (unchecked((uint)delta) & 0xFFFCu);
    }

    public static uint EncodeBranch(uint from, uint to, bool link)
    {
        var delta = unchecked((int)to - (int)from);
        if ((delta & 0x3) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(to), $"PPC branch target 0x{to:X8} is not word aligned from 0x{from:X8}.");
        }

        if (delta < -0x02000000 || delta > 0x01FFFFFC)
        {
            throw new ArgumentOutOfRangeException(nameof(to), $"PPC branch target 0x{to:X8} is out of rel24 range from 0x{from:X8}.");
        }

        return 0x48000000u | (unchecked((uint)delta) & 0x03FFFFFCu) | (link ? 1u : 0u);
    }
}
