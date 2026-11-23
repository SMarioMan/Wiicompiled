using System.Text;
using Translator.Core.Disassembly;
using Translator.Core.Ir;

namespace Translator.Core.Translation;

public sealed partial class GuestLayoutRelocations
{
    private IReadOnlyList<IrInstruction> RewriteRknetRh1(
        PpcInstruction instruction, IReadOnlyList<IrInstruction> nodes)
    {
        IrValue R(string name) => IrValue.Register(name);
        var discard = $"addr_layout_rknet_rh1_{instruction.Address:X8}_loc";
        switch (instruction.Address)
        {
            case 0x8065b188 or 0x8065b350 or 0x8065c2f4 when nodes.Count == 1 && nodes[0] == new IrStore(new IrAddress("r3", 0), R("r31"), 1):
                return new[] { nodes[0], new IrCall(discard, "layout_rknet_rh1_clear_all", new[] { R("r3") }) };
            case 0x8065b7d4 when nodes.Count == 1 && nodes[0] == new IrBinary("r4", R("r7"), R("r8"), "and"):
                return new IrInstruction[] {
                    new IrCall(discard, "layout_rknet_rh1_clear_aid_high", new[] { R("r3"), R("r4") }),
                    nodes[0] };
            case 0x8065c8e4 when nodes.Count == 1 && nodes[0] == new IrBinary("r3", R("r3"), R("r0"), "or"):
                return new IrInstruction[] { new IrCall("r3", "layout_rknet_rh1_set", new[] { R("r27"), R("r29"), IrValue.Imm(0), R("r3") }) };
            case 0x8065c904 when nodes.Count == 1 && nodes[0] == new IrBinary("r3", R("r3"), R("r0"), "or"):
                return new IrInstruction[] { new IrCall("r3", "layout_rknet_rh1_set", new[] { R("r27"), R("r29"), IrValue.Imm(1), R("r3") }) };
            case 0x8065c930 when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r3"), R("r0"), "or"):
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_rh1_set", new[] { R("r27"), R("r29"), IrValue.Imm(2), R("r3") }) };
            case 0x8065b56c or 0x8065b890 when nodes.Count == 1 && nodes[0] == new IrSetCrField(0, R("r5"), IrValue.Imm(0), false):
                return new IrInstruction[] {
                    new IrCall(discard, "layout_rknet_rh1_any", new[] { R("r31"), IrValue.Imm(1) }),
                    new IrSetCrField(0, R(discard), IrValue.Imm(0), false) };
            case 0x8065ca14 when nodes.Count == 1 && nodes[0] == new IrSetCrField(0, R("r5"), IrValue.Imm(0), false):
                return new IrInstruction[] {
                    new IrCall(discard, "layout_rknet_rh1_any", new[] { R("r30"), IrValue.Imm(1) }),
                    new IrSetCrField(0, R(discard), IrValue.Imm(0), false) };
            case 0x8065b59c or 0x8065b8c0 when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r0"), R("r3"), "sub"):
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_rh1_all_diff", new[] { R("r31"), R("r13"), IrValue.Imm(1) }) };
            case 0x8065ca44 when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r0"), R("r3"), "sub"):
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_rh1_all_diff", new[] { R("r30"), R("r13"), IrValue.Imm(1) }) };
            default:
                throw new InvalidDataException($"Unproven RKNet RH1 bitflag operation at 0x{instruction.Address:X8}.");
        }
    }

    private void EmitRknetRh1Helpers(HashSet<string> calls, StringBuilder output)
    {
        if (!calls.Any(call => call.StartsWith("layout_rknet_rh1_", StringComparison.Ordinal))) return;
        var nodes = _expanded.Symbol("DWCMatch.peers.count");
        var banks = (_expanded.Players + 31) / 32;
        var highOff = _expanded.Symbol("RKNetRH1Handler.highFlags.offset");
        var ctrlSubs = _expanded.Symbol("RKNetController.subs.offset");
        var ctrlStride = _expanded.Symbol("RKNetSub.size");
        var ctrlCur = _expanded.Symbol("RKNetController.currentSub.offset");
        output.AppendLine($$"""
            #ifndef WIICOMPILED_LAYOUT_RKNET_RH1
            #define WIICOMPILED_LAYOUT_RKNET_RH1
            #include "ppc_runtime.h"
            namespace {
                constexpr uint32_t rknet_rh1_banks = {{banks}}u, rknet_rh1_nodes = {{nodes}}u;
                static inline uint32_t rknet_rh1_word(uint32_t rh1, uint32_t bank, uint32_t slot) {
                    return rh1 + (bank ? {{highOff}}u + (bank - 1u) * 12u + slot * 4u : 4u + slot * 4u);
                }
                static inline uint32_t layout_rknet_rh1_clear_all(uint32_t rh1) {
                    if (!rh1) return 0u;
                    for (uint32_t b = 1; b < rknet_rh1_banks; ++b)
                        for (uint32_t s = 0; s < 3u; ++s)
                            Memory::Write32(rknet_rh1_word(rh1, b, s), 0u);
                    return 0u;
                }
                static inline uint32_t layout_rknet_rh1_clear_aid_high(uint32_t rh1, uint32_t aid) {
                    if (!rh1 || aid < 32u || aid >= rknet_rh1_banks * 32u) return 0u;
                    const uint32_t b = aid / 32u, bit = ~(1u << (aid % 32u));
                    for (uint32_t s = 0; s < 3u; ++s) {
                        const uint32_t addr = rknet_rh1_word(rh1, b, s);
                        Memory::Write32(addr, Memory::Read32(addr) & bit);
                    }
                    return 0u;
                }
                static inline uint32_t layout_rknet_rh1_set(uint32_t rh1, uint32_t aid, uint32_t slot, uint32_t low) {
                    if (aid < 32u) return low | (1u << aid);
                    if (aid < rknet_rh1_banks * 32u) {
                        const uint32_t addr = rknet_rh1_word(rh1, aid / 32u, slot);
                        Memory::Write32(addr, Memory::Read32(addr) | (1u << (aid % 32u)));
                    }
                    return low;
                }
                static inline uint32_t layout_rknet_rh1_any(uint32_t rh1, uint32_t slot) {
                    if (!rh1) return 0u;
                    for (uint32_t b = 0; b < rknet_rh1_banks; ++b)
                        if (Memory::Read32(rknet_rh1_word(rh1, b, slot))) return 1u;
                    return 0u;
                }
                static inline uint32_t layout_rknet_rh1_all_diff(uint32_t rh1, uint32_t sda, uint32_t slot) {
                    uint32_t members[rknet_rh1_banks]{};
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
                            if (count <= rknet_rh1_nodes) {
                                for (uint32_t i = 0; i < count; ++i) {
                                    const uint32_t aid = Memory::Read8(match + 0x38u + i * 0x30u + 0x16u);
                                    if (aid >= 32u && aid < rknet_rh1_banks * 32u)
                                        members[aid / 32u] |= 1u << (aid % 32u);
                                }
                            }
                        }
                    }
                    for (uint32_t b = 0; b < rknet_rh1_banks; ++b) {
                        uint32_t bits = Memory::Read32(rknet_rh1_word(rh1, b, slot));
                        if (localAid < rknet_rh1_banks * 32u && localAid / 32u == b)
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
