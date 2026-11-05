using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using Translator.Core.Disassembly;
using Translator.Core.Ir;
using Translator.Core.Lifting;
using Translator.Core.Loading;

namespace Translator.Core.Translation;

public sealed record GuestBoundedAccess(uint Instruction, uint ExpectedWord, int Node, string Index,
    int IndexBias, int GroupWidth, uint Fallback = 0, string? IndexBase = null, string? IndexStride = null);

public sealed partial class GuestLayoutRelocations
{
    private readonly GuestBoundedAccess[] _boundedAccesses;
    private readonly ConcurrentDictionary<uint, (bool Write, int Width)> _boundShapes = new();
    private static string BoundHelper(GuestBoundedAccess site) => $"layout_bound_{site.Instruction:X8}";

    private void ValidateBoundDefinitions()
    {
        var seen = new HashSet<uint>();
        foreach (var site in _boundedAccesses)
        {
            if (site.Instruction % 4 != 0 || site.Node < 0 || !Gpr(site.Index) ||
                site.GroupWidth <= 0 || OriginalPlayers % site.GroupWidth != 0 || !seen.Add(site.Instruction) ||
                (site.IndexBase is null) != (site.IndexStride is null) || site.IndexBase is not null && !Gpr(site.IndexBase) ||
                _arrayCopies.Any(copy => site.Instruction >= copy.Begin && site.Instruction < CopyEnd(copy)) ||
                _rankTables.Any(table => table.Lookups.Any(lookup => lookup.Instruction == site.Instruction)))
                throw new InvalidDataException("Invalid or overlapping bounded guest access.");
            if (site.IndexStride is { } stride && (_original.Symbol(stride) <= 0 || _expanded.Symbol(stride) <= 0))
                throw new InvalidDataException("Bounded pointer indices require a positive element stride.");
        }
    }

    private void ValidateBoundedAccesses(ProgramImage image)
    {
        foreach (var site in _boundedAccesses)
        {
            var word = BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(site.Instruction, 4), 4));
            if (word != site.ExpectedWord) throw BoundMismatch(site);
            var instruction = PpcDecoder.Decode(site.Instruction, word);
            if (site.Node != 0 || instruction.Mnemonic is not ("lwz" or "lbz" or "lhz" or "stw" or "stb" or "sth"))
                throw BoundMismatch(site);
            var nodes = new PpcLifter().Lift(new[] { instruction })[0].Ir;
            var shape = BoundShape(site, nodes);
            _boundShapes[site.Instruction] = shape;
        }
    }

    private static InvalidDataException BoundMismatch(GuestBoundedAccess site) =>
        new($"Bounded guest access instruction/operand mismatch at 0x{site.Instruction:X8}.");

    private static (bool Write, int Width) BoundShape(GuestBoundedAccess site, IReadOnlyList<IrInstruction> nodes)
    {
        if (site.Node >= nodes.Count) throw BoundMismatch(site);
        return nodes[site.Node] switch {
            IrLoad load when Gpr(load.Destination) && load.SizeBytes is 1 or 2 or 4 => (false, load.SizeBytes),
            IrStore store when (store.SizeBytes is 1 or 2 or 4) &&
                (store.Source.Kind == "const" || store.Source.RegisterName is { } source && Gpr(source)) => (true, store.SizeBytes),
            _ => throw BoundMismatch(site)
        };
    }

    private IReadOnlyList<IrInstruction> ApplyBoundedAccess(PpcInstruction origin, IReadOnlyList<IrInstruction> nodes)
    {
        foreach (var site in _boundedAccesses)
        {
            if (origin.Address != site.Instruction) continue;
            if (origin.Word != site.ExpectedWord) throw BoundMismatch(site);
            _ = BoundShape(site, nodes);
            if (Players % site.GroupWidth == 0) return nodes;
            var result = nodes.ToArray();
            var address = result[site.Node] is IrLoad load ? load.Address : ((IrStore)result[site.Node]).Address;
            var arguments = new List<IrValue> { IrValue.Register(address.Base), IrValue.Imm(address.Offset),
                IrValue.Register(site.Index), site.IndexBase is { } root ? IrValue.Register(root) : IrValue.Imm(0) };
            var destination = "";
            if (result[site.Node] is IrStore store) arguments.Add(store.Source);
            else destination = ((IrLoad)result[site.Node]).Destination;
            result[site.Node] = new IrCall(destination, BoundHelper(site), arguments);
            return result;
        }
        return nodes;
    }

    internal string EmitBoundHelpers(IrFunction function)
    {
        var calls = function.Blocks.SelectMany(block => block.Instructions).OfType<IrCall>()
            .Select(call => call.Target).ToHashSet(StringComparer.Ordinal);
        var source = new StringBuilder();
        foreach (var site in _boundedAccesses)
        {
            var helper = BoundHelper(site);
            if (!calls.Contains(helper)) continue;
            if (!_boundShapes.TryGetValue(site.Instruction, out var shape))
                throw new InvalidDataException("Bounded accesses must be image-validated before emission.");
            var stride = site.IndexStride is { } symbol ? _expanded.Symbol(symbol) : 1;
            var type = shape.Write ? "void" : "uint32_t";
            var extra = shape.Write ? ", uint32_t value" : "";
            var reject = shape.Write ? "return;" : $"return {site.Fallback}u;";
            var operation = shape.Write ? $"Memory::Write{shape.Width*8}(base+uint32_t(offset), value);" :
                $"return Memory::Read{shape.Width*8}(base+uint32_t(offset));";
            source.AppendLine($$"""
                #ifndef WIICOMPILED_LAYOUT_BOUND_{{site.Instruction:X8}}
                #define WIICOMPILED_LAYOUT_BOUND_{{site.Instruction:X8}}
                #include "ppc_runtime.h"
                static inline {{type}} {{helper}}(uint32_t base, int32_t offset, uint32_t index, uint32_t root{{extra}}) {
                    const uint32_t relative = index-root;
                    if (relative % {{stride}}u != 0) { {{reject}} }
                    const int64_t logical = int64_t(relative/{{stride}}u)+({{site.IndexBias}});
                    if (logical < 0 || logical >= {{Players}}) { {{reject}} }
                    {{operation}}
                }
                #endif
                """);
        }
        return source.ToString();
    }
}
