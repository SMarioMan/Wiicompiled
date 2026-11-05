using Translator.Core.Loading;

namespace Translator.Core.Mods;

/// <summary>Header identity and guest ABI attached to the generated function set.</summary>
public sealed record GuestLayoutBuildMetadata(int Players, int OriginalPlayers, bool Complete,
    bool Experimental, string HeaderPath, string HeaderSha256, string ManifestSha256)
{
    internal void Validate(string source)
    {
        static bool Digest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
        if (Players is < 1 or > 65535 || OriginalPlayers is < 1 or > 65535 ||
            !Complete && Players != OriginalPlayers && !Experimental ||
            string.IsNullOrWhiteSpace(HeaderPath) || Path.IsPathRooted(HeaderPath) || HeaderPath.Contains(':') ||
            HeaderPath.IndexOfAny(new[] { '\r', '\n', '\0', '"' }) >= 0 ||
            HeaderPath.Split('/', '\\').Any(component => component == "..") ||
            !Digest(HeaderSha256) || !Digest(ManifestSha256))
            throw new InvalidDataException($"Invalid guest layout build identity in '{source}'.");
    }

    public string ResolveAndVerifyHeader(string functionsDirectory)
    {
        Validate(functionsDirectory);
        var path = Path.GetFullPath(Path.Combine(functionsDirectory, HeaderPath.Replace('\\', '/')));
        if (!File.Exists(path) || !string.Equals(ChecksumUtilities.Sha256HexOfFile(path), HeaderSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Guest layout header is missing or differs from its translation metadata: {path}");
        return path;
    }
}
