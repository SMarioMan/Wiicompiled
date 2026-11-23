using System.Text;
using Translator.Core.Disassembly;
using Translator.Core.Ir;

namespace Translator.Core.Translation;

public sealed partial class GuestLayoutRelocations
{
    private IReadOnlyList<IrInstruction> RewriteRknetUser(
        PpcInstruction instruction, IReadOnlyList<IrInstruction> nodes)
    {
        IrValue R(string name) => IrValue.Register(name);
        var compare0 = new IrSetCrField(0, R("r0"), IrValue.Imm(0), false);
        var discard = $"addr_layout_rknet_user_{instruction.Address:X8}_loc";
        switch (instruction.Address)
        {
            case 0x806606cc or 0x80660884 or 0x80660d40 when nodes.Count == 1 && nodes[0] == new IrStore(new IrAddress("r3", 0), R("r0"), 1):
                return new[] { nodes[0], new IrCall(discard, "layout_rknet_user_clear_all", new[] { R("r3") }) };
            case 0x80660840 or 0x80660934 when nodes.Count == 1 && nodes[0] == new IrStore(new IrAddress("r29", 0), R("r0"), 1):
                return new[] { nodes[0], new IrCall(discard, "layout_rknet_user_clear_all", new[] { R("r29") }) };
            case 0x806583a0 when nodes.Count == 1 && nodes[0] == new IrLoad("r3", new IrAddress("r30", -0x26a8), 4):
                return new[] { nodes[0], new IrCall(discard, "layout_rknet_user_clear_field", new[] { R("r3"), IrValue.Imm(1) }) };
            case 0x80660b54 when nodes.Count == 1 && nodes[0] == new IrAssign("r0", IrValue.Imm(1)):
                return new[] { nodes[0], new IrCall(discard, "layout_rknet_user_copy", new[] { R("r31"), IrValue.Imm(1), IrValue.Imm(2) }) };
            case 0x806609ac when nodes.Count == 1 && nodes[0] == new IrAssign("r0", IrValue.Imm(0)):
                return new[] { nodes[0], new IrCall(discard, "layout_rknet_user_move_2_to_0", new[] { R("r29") }) };
            case 0x80660f04 when nodes.Count == 1 && nodes[0] == new IrAssign("r0", IrValue.Imm(0)):
                return new[] { nodes[0], new IrCall(discard, "layout_rknet_user_move_2_to_0", new[] { R("r31") }) };
            case 0x80660a64 when nodes.Count == 1 && nodes[0] == new IrBinary("r6", R("r6"), R("r7"), "and"):
                return new[] { nodes[0], new IrCall(discard, "layout_rknet_user_clear_aid_high", new[] { R("r3"), R("r4") }) };
            case 0x805c5ca8 when nodes.Count == 2 && nodes[0] == new IrBinary("r0", R("r4"), R("r0"), "and") && nodes[1] == compare0:
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_user_test", new[] { R("r7"), R("r28"), IrValue.Imm(0) }), compare0 };
            case 0x80629080 when nodes.Count == 2 && nodes[0] == new IrBinary("r0", R("r4"), R("r0"), "and") && nodes[1] == compare0:
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_user_test", new[] { R("r5"), R("r3"), IrValue.Imm(0) }), compare0 };
            case 0x8062ab7c when nodes.Count == 2 && nodes[0] == new IrBinary("r0", R("r20"), R("r0"), "and") && nodes[1] == compare0:
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_user_test", new[] { R("r3"), R("r19"), IrValue.Imm(0) }), compare0 };
            case 0x8062ce4c when nodes.Count == 2 && nodes[0] == new IrBinary("r0", R("r3"), R("r0"), "and") && nodes[1] == compare0:
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_user_test", new[] { R("r5"), R("r4"), IrValue.Imm(0) }), compare0 };
            case 0x8062d3f8 when nodes.Count == 2 && nodes[0] == new IrBinary("r0", R("r4"), R("r0"), "and") && nodes[1] == compare0:
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_user_test", new[] { R("r3"), R("r28"), IrValue.Imm(0) }), compare0 };
            case 0x80660b84 when nodes.Count == 1 && nodes[0] == new IrAssign("r0", IrValue.Imm(1)):
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_user_test", new[] { R("r3"), R("r4"), IrValue.Imm(0) }) };
            case 0x80660b8c when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r0"), R("r4"), "ppc_slw"):
                return new IrInstruction[] { new IrAssign("r0", R("r0")) };
            case 0x80660b90 when nodes.Count == 1 && nodes[0] == new IrBinary("r3", R("r3"), R("r0"), "and"):
                return new IrInstruction[] { new IrAssign("r3", R("r0")) };
            case 0x80660e74 when nodes.Count == 2 && nodes[0] == new IrBinary("r0", R("r3"), R("r27"), "and") && nodes[1] == compare0:
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_user_test", new[] { R("r25"), R("r26"), IrValue.Imm(1) }), compare0 };
            case 0x80660e7c when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r3"), R("r27"), "or"):
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_user_set", new[] { R("r25"), R("r26"), IrValue.Imm(1), R("r3") }) };
            case 0x8065699c when nodes.Count == 1 && nodes[0] == new IrSetCrField(0, R("r6"), R("r0"), false):
                return new IrInstruction[] {
                    new IrCall(discard, "layout_rknet_user_all_others_diff", new[] { R("r3"), R("r13"), IrValue.Imm(0) }),
                    new IrSetCrField(0, R(discard), IrValue.Imm(0), false) };
            case 0x80660be0 when nodes.Count == 1 && nodes[0] == new IrSetCrField(1, R("r4"), IrValue.Imm(0), false):
                return new IrInstruction[] {
                    new IrCall(discard, "layout_rknet_user_has_others", new[] { R("r13") }),
                    new IrSetCrField(1, R(discard), IrValue.Imm(0), false) };
            case 0x80660bf0 when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r0"), R("r4"), "sub"):
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_user_all_others_diff", new[] { R("r3"), R("r13"), IrValue.Imm(0) }) };
            case 0x80660c18 when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r0"), R("r4"), "sub"):
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_user_all_others_diff", new[] { R("r3"), R("r13"), IrValue.Imm(1) }) };
            case 0x80660c74 when nodes.Count == 2 && nodes[0] == new IrBinary("r5", R("r5"), R("r0"), "andc") &&
                nodes[1] == new IrSetCrField(0, R("r5"), IrValue.Imm(0), false):
                return new IrInstruction[] {
                    new IrCall("r5", "layout_rknet_user_has_others", new[] { R("r13") }),
                    new IrSetCrField(0, R("r5"), IrValue.Imm(0), false) };
            case 0x80660c84 when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r0"), R("r5"), "sub"):
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_user_all_others_diff", new[] { R("r3"), R("r13"), IrValue.Imm(1) }) };
            default:
                throw new InvalidDataException($"Unproven RKNet USER bitflag operation at 0x{instruction.Address:X8}.");
        }
    }

    private void EmitRknetUserHelpers(HashSet<string> calls, StringBuilder output)
    {
        if (!calls.Any(call => call.StartsWith("layout_rknet_user_", StringComparison.Ordinal))) return;
        var nodes = _expanded.Symbol("DWCMatch.peers.count");
        var banks = _expanded.Symbol("RKNetUserHandler.aidBitflag.count");
        var off0 = _expanded.Symbol("RKNetUserHandler.aidBitflag.offset");
        var off1 = _expanded.Symbol("RKNetUserHandler.miisReceived.offset");
        var off2 = _expanded.Symbol("RKNetUserHandler.aidBitflag2.offset");
        var ctrlSubs = _expanded.Symbol("RKNetController.subs.offset");
        var ctrlStride = _expanded.Symbol("RKNetSub.size");
        var ctrlCur = _expanded.Symbol("RKNetController.currentSub.offset");
        output.AppendLine($$"""
            #ifndef WIICOMPILED_LAYOUT_RKNET_USER
            #define WIICOMPILED_LAYOUT_RKNET_USER
            #include "ppc_runtime.h"
            namespace {
                constexpr uint32_t rknet_user_banks = {{banks}}u, rknet_user_nodes = {{nodes}}u;
                static inline uint32_t rknet_user_base(uint32_t user, uint32_t slot) {
                    return user + (slot == 0u ? {{off0}}u : slot == 1u ? {{off1}}u : {{off2}}u);
                }
                static inline uint32_t layout_rknet_user_clear_field(uint32_t user, uint32_t slot) {
                    if (!user) return 0u;
                    const uint32_t base = rknet_user_base(user, slot);
                    for (uint32_t b = 1; b < rknet_user_banks; ++b) Memory::Write32(base + b * 4u, 0u);
                    return 0u;
                }
                static inline uint32_t layout_rknet_user_clear_all(uint32_t user) {
                    for (uint32_t s = 0; s < 3u; ++s) layout_rknet_user_clear_field(user, s);
                    return 0u;
                }
                static inline uint32_t layout_rknet_user_copy(uint32_t user, uint32_t src, uint32_t dst) {
                    const uint32_t sb = rknet_user_base(user, src), db = rknet_user_base(user, dst);
                    for (uint32_t b = 1; b < rknet_user_banks; ++b) Memory::Write32(db + b * 4u, Memory::Read32(sb + b * 4u));
                    return 0u;
                }
                static inline uint32_t layout_rknet_user_move_2_to_0(uint32_t user) {
                    layout_rknet_user_copy(user, 2u, 0u);
                    layout_rknet_user_clear_field(user, 2u);
                    return 0u;
                }
                static inline uint32_t layout_rknet_user_clear_aid_high(uint32_t user, uint32_t aid) {
                    if (aid < 32u || aid >= rknet_user_banks * 32u) return 0u;
                    const uint32_t b = aid / 32u, bit = ~(1u << (aid % 32u));
                    for (uint32_t s = 0; s < 3u; ++s) {
                        const uint32_t addr = rknet_user_base(user, s) + b * 4u;
                        Memory::Write32(addr, Memory::Read32(addr) & bit);
                    }
                    return 0u;
                }
                static inline uint32_t layout_rknet_user_test(uint32_t user, uint32_t aid, uint32_t slot) {
                    if (!user || aid >= rknet_user_banks * 32u) return 0u;
                    const uint32_t word = Memory::Read32(rknet_user_base(user, slot) + (aid / 32u) * 4u);
                    return (word >> (aid % 32u)) & 1u;
                }
                static inline uint32_t layout_rknet_user_set(uint32_t user, uint32_t aid, uint32_t slot, uint32_t low) {
                    if (aid < 32u) return low | (1u << aid);
                    if (aid < rknet_user_banks * 32u) {
                        const uint32_t addr = rknet_user_base(user, slot) + (aid / 32u) * 4u;
                        Memory::Write32(addr, Memory::Read32(addr) | (1u << (aid % 32u)));
                    }
                    return low;
                }
                static inline void rknet_user_other_members(uint32_t sda, uint32_t (&others)[rknet_user_banks]) {
                    for (uint32_t b = 0; b < rknet_user_banks; ++b) others[b] = 0u;
                    const uint32_t ctrl = Memory::Read32(0x809bd918u);
                    if (!ctrl) return;
                    const uint32_t cur = Memory::Read32(ctrl + {{ctrlCur}}u);
                    const uint32_t sub = ctrl + {{ctrlSubs}}u + cur * {{ctrlStride}}u;
                    const uint32_t localAid = Memory::Read8(sub + 0x21u);
                    others[0] = Memory::Read32(sub + 0x10u);
                    if (Memory::Read32(sda - 0x6910u)) {
                        const uint32_t match = Memory::Read32(sda - 0x68f4u);
                        if (match) {
                            const uint32_t count = Memory::Read32(match + 0x30u);
                            if (count <= rknet_user_nodes) {
                                for (uint32_t i = 0; i < count; ++i) {
                                    const uint32_t aid = Memory::Read8(match + 0x38u + i * 0x30u + 0x16u);
                                    if (aid >= 32u && aid < rknet_user_banks * 32u)
                                        others[aid / 32u] |= 1u << (aid % 32u);
                                }
                            }
                        }
                    }
                    if (localAid < rknet_user_banks * 32u)
                        others[localAid / 32u] &= ~(1u << (localAid % 32u));
                }
                static inline uint32_t layout_rknet_user_has_others(uint32_t sda) {
                    uint32_t others[rknet_user_banks];
                    rknet_user_other_members(sda, others);
                    for (uint32_t b = 0; b < rknet_user_banks; ++b) if (others[b]) return 1u;
                    return 0u;
                }
                static inline uint32_t layout_rknet_user_all_others_diff(uint32_t user, uint32_t sda, uint32_t slot) {
                    uint32_t others[rknet_user_banks];
                    rknet_user_other_members(sda, others);
                    const uint32_t base = rknet_user_base(user, slot);
                    for (uint32_t b = 0; b < rknet_user_banks; ++b)
                        if ((others[b] & Memory::Read32(base + b * 4u)) != others[b]) return 1u;
                    return 0u;
                }
            }
            #endif
            """);
    }
}
