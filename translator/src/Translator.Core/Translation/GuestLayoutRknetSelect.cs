using System.Text;
using Translator.Core.Disassembly;
using Translator.Core.Ir;

namespace Translator.Core.Translation;

public sealed partial class GuestLayoutRelocations
{
    private IReadOnlyList<IrInstruction> RewriteRknetSelect(
        PpcInstruction instruction, IReadOnlyList<IrInstruction> nodes)
    {
        IrValue R(string name) => IrValue.Register(name);
        var discard = $"addr_layout_rknet_select_{instruction.Address:X8}_loc";
        switch (instruction.Address)
        {
            case 0x80657f8c or 0x80658838 when nodes.Count == 1 && nodes[0] == new IrAssign("r4", IrValue.Imm(-0x7f640000)):
                return new[] { nodes[0], new IrCall(discard, "layout_rknet_select_clear_all", new[] { R("r3") }) };
            case 0x80658374 when nodes.Count == 1 && nodes[0] == new IrAssign("r29", IrValue.Imm(0)):
                return new[] { nodes[0], new IrCall(discard, "layout_rknet_select_clear_all", new[] { R("r31") }) };
            case 0x806591e8 when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r0"), R("r25"), "or"):
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_select_set", new[] { R("r18"), R("r19"), IrValue.Imm(0), R("r0") }) };
            case 0x80659320 when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r0"), R("r25"), "or"):
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_select_set", new[] { R("r18"), R("r19"), IrValue.Imm(1), R("r0") }) };
            case 0x80659690 when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r0"), R("r26"), "or"):
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_select_set", new[] { R("r24"), R("r25"), IrValue.Imm(2), R("r0") }) };
            case 0x806597f8 when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r0"), R("r26"), "or"):
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_select_set", new[] { R("r24"), R("r25"), IrValue.Imm(3), R("r0") }) };
            case 0x806599b8 when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r0"), R("r26"), "or"):
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_select_set", new[] { R("r24"), R("r25"), IrValue.Imm(4), R("r0") }) };
            case 0x80658e24 when nodes.Count == 1 && nodes[0] == new IrSetCrField(0, R("r5"), IrValue.Imm(0), false):
                return new IrInstruction[] {
                    new IrAssign("r6", R("r3")),
                    new IrCall(discard, "layout_rknet_select_any", new[] { R("r3"), IrValue.Imm(2) }),
                    new IrSetCrField(0, R(discard), IrValue.Imm(0), false) };
            case 0x80658e94 when nodes.Count == 1 && nodes[0] == new IrSetCrField(0, R("r5"), IrValue.Imm(0), false):
                return new IrInstruction[] {
                    new IrAssign("r6", R("r3")),
                    new IrCall(discard, "layout_rknet_select_any", new[] { R("r3"), IrValue.Imm(4) }),
                    new IrSetCrField(0, R(discard), IrValue.Imm(0), false) };
            case 0x80658f04 when nodes.Count == 1 && nodes[0] == new IrSetCrField(0, R("r5"), IrValue.Imm(0), false):
                return new IrInstruction[] {
                    new IrAssign("r6", R("r3")),
                    new IrCall(discard, "layout_rknet_select_any", new[] { R("r3"), IrValue.Imm(3) }),
                    new IrSetCrField(0, R(discard), IrValue.Imm(0), false) };
            case 0x806594d0 when nodes.Count == 1 && nodes[0] == new IrSetCrField(0, R("r5"), IrValue.Imm(0), false):
                return new IrInstruction[] {
                    new IrCall(discard, "layout_rknet_select_any", new[] { R("r28"), IrValue.Imm(4) }),
                    new IrSetCrField(0, R(discard), IrValue.Imm(0), false) };
            case 0x8065969c when nodes.Count == 1 && nodes[0] == new IrSetCrField(0, R("r4"), IrValue.Imm(0), false):
                return new IrInstruction[] {
                    new IrCall(discard, "layout_rknet_select_any", new[] { R("r24"), IrValue.Imm(2) }),
                    new IrSetCrField(0, R(discard), IrValue.Imm(0), false) };
            case 0x80659804 when nodes.Count == 1 && nodes[0] == new IrSetCrField(0, R("r4"), IrValue.Imm(0), false):
                return new IrInstruction[] {
                    new IrCall(discard, "layout_rknet_select_any", new[] { R("r24"), IrValue.Imm(3) }),
                    new IrSetCrField(0, R(discard), IrValue.Imm(0), false) };
            case 0x806598c4 when nodes.Count == 1 && nodes[0] == new IrSetCrField(0, R("r3"), IrValue.Imm(0), false):
                return new IrInstruction[] {
                    new IrCall(discard, "layout_rknet_select_any", new[] { R("r24"), IrValue.Imm(4) }),
                    new IrSetCrField(0, R(discard), IrValue.Imm(0), false) };
            case 0x806599c4 when nodes.Count == 1 && nodes[0] == new IrSetCrField(0, R("r0"), IrValue.Imm(0), false):
                return new IrInstruction[] {
                    new IrCall(discard, "layout_rknet_select_any", new[] { R("r24"), IrValue.Imm(1) }),
                    new IrSetCrField(0, R(discard), IrValue.Imm(0), false) };
            case 0x80658434 when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r0"), R("r3"), "sub"):
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_select_all_diff", new[] { R("r31"), R("r13"), IrValue.Imm(0), IrValue.Imm(1) }) };
            case 0x806587b0 when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r0"), R("r3"), "sub"):
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_select_all_diff", new[] { R("r30"), R("r13"), IrValue.Imm(0), IrValue.Imm(1) }) };
            case 0x806592dc when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r0"), R("r3"), "sub"):
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_select_all_diff", new[] { R("r18"), R("r13"), IrValue.Imm(0), IrValue.Imm(1) }) };
            case 0x806593b4 when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r0"), R("r3"), "sub"):
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_select_all_diff", new[] { R("r28"), R("r13"), IrValue.Imm(0), IrValue.Imm(1) }) };
            case 0x806595d0 when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r0"), R("r3"), "sub"):
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_select_all_diff", new[] { R("r24"), R("r13"), IrValue.Imm(0), IrValue.Imm(1) }) };
            case 0x80659994 when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r0"), R("r3"), "sub"):
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_select_all_diff", new[] { R("r24"), R("r13"), IrValue.Imm(0), IrValue.Imm(1) }) };
            case 0x80658e80 when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r0"), R("r3"), "sub"):
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_select_all_diff", new[] { R("r6"), R("r13"), IrValue.Imm(2), IrValue.Imm(2) }) };
            case 0x80658ef0 when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r0"), R("r3"), "sub"):
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_select_all_diff", new[] { R("r6"), R("r13"), IrValue.Imm(4), IrValue.Imm(4) }) };
            case 0x80658f64 when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r0"), R("r3"), "sub"):
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_select_all_diff", new[] { R("r6"), R("r13"), IrValue.Imm(3), IrValue.Imm(3) }) };
            case 0x8065952c when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r0"), R("r3"), "sub"):
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_select_all_diff", new[] { R("r28"), R("r13"), IrValue.Imm(4), IrValue.Imm(4) }) };
            case 0x806596ec when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r0"), R("r3"), "sub"):
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_select_all_diff", new[] { R("r24"), R("r13"), IrValue.Imm(2), IrValue.Imm(2) }) };
            case 0x80659858 when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r0"), R("r3"), "sub"):
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_select_all_diff", new[] { R("r24"), R("r13"), IrValue.Imm(3), IrValue.Imm(3) }) };
            case 0x806598ec when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r0"), R("r5"), "sub"):
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_select_all_diff", new[] { R("r24"), R("r13"), IrValue.Imm(4), IrValue.Imm(4) }) };
            default:
                throw new InvalidDataException($"Unproven RKNet SELECT bitflag operation at 0x{instruction.Address:X8}.");
        }
    }

    private void EmitRknetSelectHelpers(HashSet<string> calls, StringBuilder output)
    {
        if (!calls.Any(call => call.StartsWith("layout_rknet_select_", StringComparison.Ordinal))) return;
        var nodes = _expanded.Symbol("DWCMatch.peers.count");
        var banks = _expanded.Symbol("RKNetSelectHandler.flags.count");
        var flagsOff = _expanded.Symbol("RKNetSelectHandler.flags.offset");
        var teamsOff = _expanded.Symbol("RKNetSelectHandler.send.offset+RKNetSelectPacket.teams.offset");
        var teamsWords = _expanded.Symbol("RKNetSelectPacket.teams.count");
        var sendFlagsOff = _expanded.Symbol("RKNetSelectHandler.send.offset+RKNetSelectPacket.flags.offset");
        var ctrlSubs = _expanded.Symbol("RKNetController.subs.offset");
        var ctrlStride = _expanded.Symbol("RKNetSub.size");
        var ctrlCur = _expanded.Symbol("RKNetController.currentSub.offset");
        output.AppendLine($$"""
            #ifndef WIICOMPILED_LAYOUT_RKNET_SELECT
            #define WIICOMPILED_LAYOUT_RKNET_SELECT
            #include "ppc_runtime.h"
            namespace {
                constexpr uint32_t rknet_select_banks = {{banks}}u, rknet_select_nodes = {{nodes}}u;
                static inline uint32_t rknet_select_word(uint32_t sel, uint32_t bank, uint32_t slot) {
                    return sel + {{flagsOff}}u + bank * 24u + slot * 4u;
                }
                static inline uint32_t layout_rknet_select_clear_all(uint32_t sel) {
                    if (!sel) return 0u;
                    for (uint32_t b = 1; b < rknet_select_banks; ++b)
                        for (uint32_t s = 0; s < 5u; ++s)
                            Memory::Write32(rknet_select_word(sel, b, s), 0u);
                    return 0u;
                }
                static inline uint32_t layout_rknet_select_set(uint32_t sel, uint32_t aid, uint32_t slot, uint32_t low) {
                    if (aid < 32u) return low | (1u << aid);
                    if (aid < rknet_select_banks * 32u) {
                        const uint32_t addr = rknet_select_word(sel, aid / 32u, slot);
                        Memory::Write32(addr, Memory::Read32(addr) | (1u << (aid % 32u)));
                    }
                    return low;
                }
                static inline uint32_t layout_rknet_select_any(uint32_t sel, uint32_t slot) {
                    if (!sel) return 0u;
                    for (uint32_t b = 0; b < rknet_select_banks; ++b)
                        if (Memory::Read32(rknet_select_word(sel, b, slot))) return 1u;
                    return 0u;
                }
                static inline uint32_t layout_rknet_select_all_diff(uint32_t sel, uint32_t sda, uint32_t slot, uint32_t selfMode) {
                    uint32_t members[rknet_select_banks]{};
                    const uint32_t ctrl = Memory::Read32(0x809bd918u);
                    if (!ctrl) return 0u;
                    const uint32_t cur = Memory::Read32(ctrl + {{ctrlCur}}u);
                    const uint32_t sub = ctrl + {{ctrlSubs}}u + cur * {{ctrlStride}}u;
                    const uint32_t localAid = Memory::Read8(sub + 0x21u);
                    members[0] = Memory::Read32(sub + 0x10u);
                    if (Memory::Read32(sda - 0x6910u)) {
                        const uint32_t match = Memory::Read32(sda - 0x68f4u);
                        if (match) {
                            const uint32_t count = Memory::Read32(match + 0x30u);
                            if (count <= rknet_select_nodes) {
                                for (uint32_t i = 0; i < count; ++i) {
                                    const uint32_t aid = Memory::Read8(match + 0x38u + i * 0x30u + 0x16u);
                                    if (aid >= 32u && aid < rknet_select_banks * 32u)
                                        members[aid / 32u] |= 1u << (aid % 32u);
                                }
                            }
                        }
                    }
                    bool includeSelf = (selfMode == 1u);
                    if (selfMode == 2u) {
                        for (uint32_t w = 0; w < {{teamsWords}}u; ++w)
                            if (Memory::Read32(sel + {{teamsOff}}u + w * 4u)) { includeSelf = true; break; }
                    } else if (selfMode == 3u) {
                        includeSelf = static_cast<int8_t>(Memory::Read8(sel + {{sendFlagsOff}}u)) != -1;
                    } else if (selfMode == 4u) {
                        includeSelf = Memory::Read8(sel + 0x1eu) != 0x43u;
                    }
                    for (uint32_t b = 0; b < rknet_select_banks; ++b) {
                        uint32_t bits = Memory::Read32(rknet_select_word(sel, b, slot));
                        if (includeSelf && localAid < rknet_select_banks * 32u && localAid / 32u == b)
                            bits |= 1u << (localAid % 32u);
                        if ((members[b] & bits) != members[b]) return 1u;
                    }
                    return 0u;
                }
            }
            #endif
            """);
    }
}
