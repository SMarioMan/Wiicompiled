using System.Globalization;
using Translator.Core.Parsing.Kamek;

namespace Translator.Core.Parsing.BrainSlug;

/// <summary>A build-time replacement for a module's runtime instruction writes.</summary>
public sealed record BrainSlugStaticPatch(string Operation, string Address, string Value, string? Condition = null);

public static class BrainSlugStaticPatches
{
    public static BrainSlugLinkResult Apply(BrainSlugLinkResult linked, IEnumerable<BrainSlugStaticPatch> patches,
        Func<string, uint?> resolveImport, Func<uint, uint> readGameInstruction)
    {
        // References use module:name, game:name or a hexadecimal literal, optionally followed
        // by +0xOFFSET. Keep module operands relative until Kamek places the image.
        (uint Value, bool Relative) Resolve(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Empty static patch reference.");
            var parts = text.Split('+');
            var offset = parts.Skip(1).Aggregate(0u, (sum, part) => checked(sum + Hex(part)));
            var name = parts[0];
            var relative = name.StartsWith("module:", StringComparison.Ordinal);
            var value = relative ? linked.SymbolOffsets.TryGetValue(name[7..], out var symbol) ? symbol :
                    throw new InvalidDataException($"Unknown module symbol '{name}'.") :
                name.StartsWith("game:", StringComparison.Ordinal) ? resolveImport(name[5..]) ??
                    throw new InvalidDataException($"Unknown game symbol '{name}'.") : Hex(name);
            value = checked(value + offset);
            if (relative && value >= linked.Chunk.CodeSize) throw new InvalidDataException("Static patch reference leaves module image.");
            return (value, relative);
        }
        var commands = linked.Chunk.Commands.ToList();
        foreach (var patch in patches)
        {
            if (patch is null) throw new InvalidDataException("Null static patch record.");
            var destination = Resolve(patch.Address);
            var value = Resolve(patch.Value);
            var id = patch.Operation switch
            {
                "Write32" or "Copy32" or "BranchConditional" => KamekCommandId.Write32,
                "Write16" or "Copy16" => KamekCommandId.Write16,
                "Addr32" or "CaptureBranchTarget" => KamekCommandId.Addr32,
                "Addr16Hi" => KamekCommandId.Addr16Hi,
                "Addr16Ha" => KamekCommandId.Addr16Ha,
                "Addr16Lo" => KamekCommandId.Addr16Lo,
                "Branch" => KamekCommandId.Branch,
                "BranchLink" => KamekCommandId.BranchLink,
                _ => throw new InvalidDataException($"Unsupported static patch operation '{patch.Operation}'.")
            };
            var width = id is KamekCommandId.Write16 or KamekCommandId.Addr16Hi or KamekCommandId.Addr16Ha or KamekCommandId.Addr16Lo ? 2u : 4u;
            if (destination.Value % width != 0 ||
                (destination.Relative ? (ulong)destination.Value + width > linked.Chunk.CodeSize : destination.Value < 0x80000000))
                throw new InvalidDataException("Invalid static patch destination.");
            if (patch.Operation == "BranchConditional")
            {
                // Both endpoints must move together when Kamek places the module.
                if (destination.Relative != value.Relative || (!value.Relative && value.Value < 0x80000000))
                    throw new InvalidDataException("A conditional branch requires endpoints in the same image.");
                var template = Hex(patch.Condition ?? "");
                var delta = (long)value.Value - destination.Value;
                if ((template & 0xFC00FFFEu) != 0x40000000u)
                    throw new InvalidDataException("A conditional branch template must have an empty relative displacement.");
                if ((delta & 3) != 0 || delta is < -0x8000 or > 0x7FFC)
                    throw new InvalidDataException("A conditional branch requires an aligned target in rel14 range.");
                if (value.Relative)
                {
                    if ((ulong)value.Value + 4 > linked.Chunk.CodeSize)
                        throw new InvalidDataException("A conditional branch target leaves the module image.");
                }
                else
                {
                    // Use the linker's original-game reader to verify executable endpoints.
                    readGameInstruction(destination.Value);
                    readGameInstruction(value.Value);
                }
                value = (template | (unchecked((uint)delta) & 0xFFFCu), false);
            }
            else if (patch.Condition is not null)
                throw new InvalidDataException("A branch condition is only valid for BranchConditional.");
            if (patch.Operation is "Copy32" or "Copy16" or "CaptureBranchTarget")
            {
                if (value.Relative || value.Value < 0x80000000 || value.Value % (patch.Operation == "Copy16" ? 2u : 4u) != 0)
                    throw new InvalidDataException("Static capture requires an aligned original game address.");
                var word = readGameInstruction(value.Value & ~3u);
                value = patch.Operation switch
                {
                    "Copy32" => (word, false),
                    "Copy16" => ((word >> ((value.Value & 2) == 0 ? 16 : 0)) & 0xFFFF, false),
                    _ when (word & 0xFC000003) == 0x48000001 =>
                        (unchecked(value.Value + (uint)((int)(word << 6) >> 6 & ~3)), false),
                    _ => throw new InvalidDataException("Captured call is not a relative branch-and-link.")
                };
            }
            if (id is KamekCommandId.Write32 or KamekCommandId.Write16)
            {
                if (value.Relative || (width == 2 && value.Value > 0xFFFF))
                    throw new InvalidDataException("Scalar writes require a value that fits their field.");
            }
            else if (!value.Relative && value.Value < 0x80000000)
                throw new InvalidDataException("Pointer/branch patch target is outside guest address space.");
            commands.Add(new KamekCommand(0, 0, id, !destination.Relative, destination.Value, [value.Value]));
        }
        var chunk = linked.Chunk;
        var size = checked((uint)(KamekChunk.HeaderSize + chunk.CodeBlob.Length + commands.Sum(c => c.AddressIsAbsolute ? 12 : 8)));
        return linked with { Chunk = new KamekChunk(chunk.Index, chunk.FileOffset, chunk.BssSize, chunk.CodeSize,
            chunk.CtorStart, chunk.CtorEnd, size, chunk.CodeBlob, commands) };
    }

    private static uint Hex(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
        uint.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number) ? number :
        throw new InvalidDataException($"Expected hexadecimal literal, got '{value}'.");
}
