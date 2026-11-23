using System.Text;
using Translator.Core.Disassembly;
using Translator.Core.Ir;

namespace Translator.Core.Translation;

public sealed partial class GuestLayoutRelocations
{
    private IReadOnlyList<IrInstruction> RewriteDwcSuspend(
        PpcInstruction instruction, IReadOnlyList<IrInstruction> nodes)
    {
        IrValue R(string name) => IrValue.Register(name);
        var compare = new IrSetCrField(0, R("r0"), IrValue.Imm(0), false);
        var banks = _expanded.Symbol("DWCMatch.peers.count") / 32;
        var discard = $"addr_layout_dwc_suspend_{instruction.Address:X8}_loc";
        switch (instruction.Address)
        {
            case 0x800d389c when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r4"), R("r0"), "andc"):
                return new IrInstruction[] { new IrCall("r0", "layout_dwc_suspend_clear_aid", new[] { R("r5"), R("r19"), R("r4") }) };
            case 0x800db344 when nodes.Count == 1 && nodes[0] == new IrStore(new IrAddress("r3", 28), R("r31"), 4):
                return new[] { nodes[0], new IrCall(discard, "layout_dwc_suspend_fill", new[] { R("r3"), R("r31") }) };
            case 0x800d7be0 when nodes.Count == 1 && nodes[0] == new IrStore(new IrAddress("r28", 28), R("r3"), 4):
                return new IrInstruction[] {
                    new IrCall("r3", "layout_dwc_suspend_set_members", new[] { R("r28") }),
                    nodes[0] };
            case 0x800d7bec when nodes.Count == 1 && nodes[0] == new IrStore(new IrAddress("r28", 28), R("r0"), 4):
                return new[] { nodes[0], new IrCall(discard, "layout_dwc_suspend_fill", new[] { R("r28"), R("r0") }) };
            case 0x800d49cc when nodes.Count == 2 &&
                nodes[0] == new IrBinary("r0", R("r30"), R("r3"), "xor") && nodes[1] == compare:
                return new IrInstruction[] {
                    new IrCall("r0", "layout_dwc_suspend_all_voted", new[] { R("r13") }),
                    compare };
            case 0x800d49e0 when nodes.Count == 2 &&
                nodes[0] == new IrBinary("r0", R("r30"), R("r3"), "and") && nodes[1] == compare:
                return new IrInstruction[] {
                    new IrCall("r0", "layout_dwc_suspend_any_voted", new[] { R("r13") }),
                    compare };
            case 0x800e6f44 when nodes.Count == 1 && nodes[0] == new IrLoad("r7", new IrAddress("r6", 28), 4):
                return new IrInstruction[] { new IrCall("r7", "layout_dwc_suspend_get_bit", new[] { R("r6"), R("r3") }) };
            case 0x800e6f54 when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r7"), R("r0"), "or"):
                return new IrInstruction[] { new IrCall("r0", "layout_dwc_suspend_set_bit", new[] { R("r6"), R("r3"), IrValue.Imm(1) }) };
            case 0x800e6f68 when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r7"), R("r0"), "andc"):
                return new IrInstruction[] { new IrCall("r0", "layout_dwc_suspend_set_bit", new[] { R("r6"), R("r3"), IrValue.Imm(0) }) };
            case 0x800e6f74 when nodes.Count == 1 && nodes[0] == new IrLoad("r8", new IrAddress("r4", 28), 4):
                return new IrInstruction[] { new IrCall("r8", "layout_dwc_suspend_get_bit", new[] { R("r4"), R("r3") }) };
            case 0x800e7188 or 0x800e72a0 when nodes.Count == 1 && nodes[0] == new IrAssign("r8", IrValue.Imm(4)):
                return new IrInstruction[] { new IrAssign("r8", IrValue.Imm(3 + banks)) };
            case 0x800e7194 or 0x800e7264 when nodes.Count == 1 && nodes[0] == new IrStore(new IrAddress("r1", 36), R("r0"), 4):
                return new[] { nodes[0], new IrCall(discard, "layout_dwc_suspend_pack_high", new[] { R("r1"), R("r13") }) };
            case 0x800de584 when nodes.Count == 1 && nodes[0] == new IrStore(new IrAddress("r3", 28), R("r20"), 4):
                return new[] { nodes[0], new IrCall(discard, "layout_dwc_suspend_unpack_high", new[] { R("r3"), R("r31"), R("r28") }) };
            default:
                throw new InvalidDataException($"Unproven SDK suspend operation at 0x{instruction.Address:X8}.");
        }
    }

    private void EmitDwcSuspendHelpers(HashSet<string> calls, StringBuilder output)
    {
        if (!calls.Any(call => call.StartsWith("layout_dwc_suspend_", StringComparison.Ordinal))) return;
        var capacity = _expanded.Symbol("DWCMatch.peers.count");
        var tail = _expanded.Symbol("DWCMatch.tail.offset");
        var highWords = _expanded.Symbol("DWCMatch.tail.offset+DWCMatchTail.timerAndSynAcks.offset");
        output.AppendLine($$"""
            #ifndef WIICOMPILED_LAYOUT_DWC_SUSPEND
            #define WIICOMPILED_LAYOUT_DWC_SUSPEND
            #include "ppc_runtime.h"
            namespace {
                constexpr uint32_t dwc_suspend_nodes = {{capacity}}u, dwc_suspend_tail = {{tail}}u;
                constexpr uint32_t dwc_suspend_banks = dwc_suspend_nodes / 32u;
                static inline uint32_t dwc_suspend_bank(uint32_t match, uint32_t bank) {
                    return match + (bank ? {{highWords}}u + bank * 8u + 4u : 0x1cu);
                }
                static inline uint32_t dwc_suspend_bswap(uint32_t v) {
                    return ((v & 0x000000ffu) << 24) | ((v & 0x0000ff00u) << 8) |
                           ((v & 0x00ff0000u) >> 8) | ((v & 0xff000000u) >> 24);
                }
                static inline uint32_t dwc_suspend_my_aid(uint32_t sda, uint32_t match) {
                    const uint32_t control = Memory::Read32(sda - 0x6910u);
                    if (!control) return 0;
                    const uint32_t myPid = Memory::Read32(control + 0x64u);
                    const uint32_t count = Memory::Read32(match + 0x30u);
                    if (count > dwc_suspend_nodes) return 0;
                    for (uint32_t i = 0; i < count; ++i)
                        if (Memory::Read32(match + 0x38u + i * 0x30u) == myPid)
                            return Memory::Read8(match + 0x38u + i * 0x30u + 0x16u);
                    return 0;
                }
                static inline bool dwc_suspend_exclude_self(uint32_t sda, uint32_t match) {
                    const uint32_t control = Memory::Read32(sda - 0x6910u);
                    if (!control) return false;
                    const uint32_t state = Memory::Read32(match + dwc_suspend_tail + 0xe4u);
                    if (state == 0u || (state >= 8u && state <= 10u) ||
                        Memory::Read8(match + 0x0du) == 0u || Memory::Read8(match + 0x16u) != 2u)
                        return false;
                    if (Memory::Read32(match + 0x30u) == 0u || Memory::Read8(match + 0x38u + 0x16u) == 0xffu)
                        return false;
                    return state != 0x0cu && state != 0x14u;
                }
                static inline void dwc_suspend_eval(uint32_t sda, uint32_t (&members)[dwc_suspend_banks], uint32_t (&votes)[dwc_suspend_banks]) {
                    for (uint32_t b = 0; b < dwc_suspend_banks; ++b) { members[b] = 0; votes[b] = 0; }
                    const uint32_t match = Memory::Read32(sda - 0x68f4u);
                    if (!match) return;
                    const uint32_t count = Memory::Read32(match + 0x30u);
                    if (count <= dwc_suspend_nodes) {
                        for (uint32_t i = 0; i < count; ++i) {
                            const uint32_t aid = Memory::Read8(match + 0x38u + i * 0x30u + 0x16u);
                            if (aid < dwc_suspend_nodes) members[aid / 32u] |= 1u << (aid % 32u);
                        }
                    }
                    for (uint32_t b = 0; b < dwc_suspend_banks; ++b)
                        votes[b] = Memory::Read32(dwc_suspend_bank(match, b)) & members[b];
                    if (dwc_suspend_exclude_self(sda, match)) {
                        const uint32_t myAid = dwc_suspend_my_aid(sda, match);
                        if (myAid < dwc_suspend_nodes) votes[myAid / 32u] &= ~(1u << (myAid % 32u));
                    }
                }
                static inline uint32_t layout_dwc_suspend_clear_aid(uint32_t control, uint32_t rawAid, uint32_t low) {
                    const uint32_t aid = rawAid & 0xffu;
                    if (aid < 32u) return low & ~(1u << aid);
                    if (aid < dwc_suspend_nodes) {
                        const uint32_t word = dwc_suspend_bank(control + 0x360u, aid / 32u);
                        Memory::Write32(word, Memory::Read32(word) & ~(1u << (aid % 32u)));
                    }
                    return low;
                }
                static inline uint32_t layout_dwc_suspend_fill(uint32_t match, uint32_t value) {
                    for (uint32_t b = 1; b < dwc_suspend_banks; ++b)
                        Memory::Write32(dwc_suspend_bank(match, b), value);
                    return 0u;
                }
                static inline uint32_t layout_dwc_suspend_set_members(uint32_t match) {
                    uint32_t members[dwc_suspend_banks]{};
                    const uint32_t count = Memory::Read32(match + 0x30u);
                    if (count <= dwc_suspend_nodes) {
                        for (uint32_t i = 0; i < count; ++i) {
                            const uint32_t aid = Memory::Read8(match + 0x38u + i * 0x30u + 0x16u);
                            if (aid < dwc_suspend_nodes) members[aid / 32u] |= 1u << (aid % 32u);
                        }
                    }
                    for (uint32_t b = 1; b < dwc_suspend_banks; ++b)
                        Memory::Write32(dwc_suspend_bank(match, b), members[b]);
                    return members[0];
                }
                static inline uint32_t layout_dwc_suspend_all_voted(uint32_t sda) {
                    uint32_t members[dwc_suspend_banks], votes[dwc_suspend_banks];
                    dwc_suspend_eval(sda, members, votes);
                    for (uint32_t b = 0; b < dwc_suspend_banks; ++b)
                        if (votes[b] != members[b]) return 1u;
                    return 0u;
                }
                static inline uint32_t layout_dwc_suspend_any_voted(uint32_t sda) {
                    uint32_t members[dwc_suspend_banks], votes[dwc_suspend_banks];
                    dwc_suspend_eval(sda, members, votes);
                    for (uint32_t b = 0; b < dwc_suspend_banks; ++b)
                        if ((votes[b] & members[b]) != 0u) return 1u;
                    return 0u;
                }
                static inline uint32_t layout_dwc_suspend_get_bit(uint32_t match, uint32_t aid) {
                    if (aid >= dwc_suspend_nodes) return 0u;
                    return (Memory::Read32(dwc_suspend_bank(match, aid / 32u)) >> (aid % 32u)) & 1u;
                }
                static inline uint32_t layout_dwc_suspend_set_bit(uint32_t match, uint32_t aid, uint32_t set) {
                    const uint32_t low = Memory::Read32(match + 0x1cu);
                    if (aid < 32u) return set ? (low | (1u << aid)) : (low & ~(1u << aid));
                    if (aid < dwc_suspend_nodes) {
                        const uint32_t word = dwc_suspend_bank(match, aid / 32u);
                        const uint32_t cur = Memory::Read32(word);
                        Memory::Write32(word, set ? (cur | (1u << (aid % 32u))) : (cur & ~(1u << (aid % 32u))));
                    }
                    return low;
                }
                static inline uint32_t layout_dwc_suspend_pack_high(uint32_t sp, uint32_t sda) {
                    const uint32_t match = Memory::Read32(sda - 0x68f4u);
                    const bool excl = match && dwc_suspend_exclude_self(sda, match);
                    const uint32_t myAid = excl ? dwc_suspend_my_aid(sda, match) : dwc_suspend_nodes;
                    for (uint32_t b = 1; b < dwc_suspend_banks; ++b) {
                        uint32_t bits = match ? Memory::Read32(dwc_suspend_bank(match, b)) : 0u;
                        if (excl && myAid / 32u == b) bits &= ~(1u << (myAid % 32u));
                        Memory::Write32(sp + 0x28u + (b - 1u) * 4u, dwc_suspend_bswap(bits));
                    }
                    return 0u;
                }
                static inline uint32_t layout_dwc_suspend_unpack_high(uint32_t match, uint32_t packet, uint32_t wordCount) {
                    for (uint32_t b = 1; b < dwc_suspend_banks; ++b) {
                        const uint32_t bits = wordCount >= 4u + b
                            ? dwc_suspend_bswap(Memory::Read32(packet + 0x10u + (b - 1u) * 4u))
                            : 0u;
                        Memory::Write32(dwc_suspend_bank(match, b), bits);
                    }
                    return 0u;
                }
            }
            #endif
            """);
    }
}
