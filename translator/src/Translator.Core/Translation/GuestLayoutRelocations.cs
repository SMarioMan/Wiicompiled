using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using Translator.Core.Disassembly;
using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Lifting;

namespace Translator.Core.Translation;

public sealed record GuestLayoutReference(uint Instruction, uint ExpectedWord, int Node,
    string Operand, string Symbol, int Addend = 0);

/// <summary>
/// Checked semantic relocations applied before SSA, inlining, ABI analysis and C++ emission.
/// Expanded values need not fit the original instruction's immediate encoding. Guest PCs remain
/// stable identities; C++ labels and symbols determine the expanded native control flow.
/// </summary>
public sealed class GuestLayoutRelocations
{
    private readonly GuestLayouts _original;
    private readonly GuestLayouts _expanded;
    private readonly IReadOnlyDictionary<uint, GuestLayoutReference[]> _references;
    public int OriginalPlayers => _original.Players;
    public int Players => _expanded.Players;
    public bool Complete { get; }

    public GuestLayoutRelocations(IEnumerable<GuestLayoutType> types, int originalPlayers, int players,
        IEnumerable<GuestLayoutReference> references, bool complete = false)
    {
        var definitions = types.ToArray();
        _original = new GuestLayouts(definitions, originalPlayers);
        _expanded = new GuestLayouts(definitions, players);
        Complete = complete;
        var sites = references.ToArray();
        if (sites.Select(r => (r.Instruction, r.Node, r.Operand)).Distinct().Count() != sites.Length)
            throw new InvalidDataException("Duplicate guest layout relocation operand.");
        foreach (var site in sites)
        {
            if (site.Instruction % 4 != 0 || site.Node < 0)
                throw new InvalidDataException("Invalid guest layout relocation site.");
            _ = checked(_original.Symbol(site.Symbol) + site.Addend);
            _ = checked(_expanded.Symbol(site.Symbol) + site.Addend);
        }
        _references = sites.GroupBy(r => r.Instruction).ToDictionary(g => g.Key, g => g.ToArray());
    }

    public void ValidateImage(ProgramImage image)
    {
        foreach (var (address, sites) in _references)
        {
            var word = BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(address, 4), 4));
            if (sites.Any(site => site.ExpectedWord != word))
                throw new InvalidDataException($"Guest layout instruction mismatch at 0x{address:X8} (got 0x{word:X8}).");
            var origin = PpcDecoder.Decode(address, word);
            var lifted = new PpcLifter().Lift(new[] { origin });
            _ = Apply(origin, lifted[0].Ir);
        }
    }

    public object Describe() => new
    {
        Format = "guest-layout-plan-v1", OriginalPlayers, Players, Complete,
        Types = _expanded.Types,
        References = _references.OrderBy(p => p.Key).SelectMany(p => p.Value).Select(site => new
        {
            Instruction = $"0x{site.Instruction:X8}", ExpectedWord = $"0x{site.ExpectedWord:X8}",
            site.Node, site.Operand, site.Symbol, site.Addend,
            Before = checked(_original.Symbol(site.Symbol) + site.Addend),
            After = checked(_expanded.Symbol(site.Symbol) + site.Addend)
        })
    };

    public IReadOnlyList<IrInstruction> Apply(PpcInstruction origin, IReadOnlyList<IrInstruction> instructions)
    {
        if (!_references.TryGetValue(origin.Address, out var sites)) return instructions;
        var result = instructions.ToArray();
        foreach (var site in sites)
        {
            if (origin.Word != site.ExpectedWord || site.Node >= result.Length)
                throw new InvalidDataException($"Guest layout instruction mismatch at 0x{origin.Address:X8}.");
            var expected = checked(_original.Symbol(site.Symbol) + site.Addend);
            var replacement = checked(_expanded.Symbol(site.Symbol) + site.Addend);
            IrValue Replace(IrValue value)
            {
                if (value.Kind != "const" || value.Constant != expected) throw Mismatch();
                return IrValue.Imm(replacement);
            }
            int ReplaceOffset(int value)
            {
                if (value != expected) throw Mismatch();
                return replacement;
            }
            InvalidDataException Mismatch() => new($"Guest layout operand mismatch at 0x{origin.Address:X8}: {site.Operand}, {site.Symbol}.");
            result[site.Node] = (site.Operand, result[site.Node]) switch
            {
                ("value", IrAssign node) => node with { Value = Replace(node.Value) },
                ("right", IrBinary node) => node with { Right = Replace(node.Right) },
                ("right", IrSetCrField node) => node with { Right = Replace(node.Right) },
                ("offset", IrLoad node) => node with { Address = node.Address with { Offset = ReplaceOffset(node.Address.Offset) } },
                ("offset", IrStore node) => node with { Address = node.Address with { Offset = ReplaceOffset(node.Address.Offset) } },
                _ => throw Mismatch()
            };
        }
        return result;
    }

    public static GuestLayoutRelocations Read(string path, int players)
    {
        var file = JsonSerializer.Deserialize<LayoutFile>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Empty guest layout file.");
        if (file.Version != 1) throw new InvalidDataException("Unsupported guest layout format.");
        return new GuestLayoutRelocations(file.Types, file.OriginalPlayers, players,
            file.References.Select(r => new GuestLayoutReference(Hex(r.Instruction), Hex(r.ExpectedWord),
                r.Node, r.Operand, r.Symbol, r.Addend)), file.Complete);
    }

    private static uint Hex(string value) => uint.Parse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? value.AsSpan(2) : value.AsSpan(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    private sealed record LayoutFile(int Version, int OriginalPlayers, GuestLayoutType[] Types,
        ReferenceFile[] References, bool Complete = false);
    private sealed record ReferenceFile(string Instruction, string ExpectedWord, int Node,
        string Operand, string Symbol, int Addend = 0);
}
