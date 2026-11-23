using System.Text;
using Translator.Core.Disassembly;
using Translator.Core.Ir;

namespace Translator.Core.Translation;

public sealed partial class GuestLayoutRelocations
{
    private static IReadOnlyList<IrInstruction> RewriteDwcSynAcks(
        PpcInstruction instruction, IReadOnlyList<IrInstruction> nodes)
    {
        IrValue R(string name) => IrValue.Register(name);
        var compare = new IrSetCrField(0, R("r0"), IrValue.Imm(0), false);
        switch (instruction.Address)
        {
            case 0x800d99a0 when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r3"), R("r0"), "or"):
                return new IrInstruction[] { new IrCall("r0", "layout_dwc_syn_record", new[] { R("r6"), R("r27"), R("r3") }) };
            case 0x800d9a30 when nodes.Count == 1 && nodes[0] == new IrSetCrField(0, R("r6"), R("r0"), true):
                var complete = "addr_layout_dwc_syn_complete_loc";
                return new IrInstruction[] {
                    new IrCall(complete, "layout_dwc_syn_complete", new[] { R("r5"), R("r0") }),
                    compare with { Left = R(complete), Right = IrValue.Imm(1) } };
            case 0x800db07c or 0x800dfcfc when nodes.Count == 1 && nodes[0] == new IrLoad("r3", new IrAddress("r13", -0x68f4), 4):
                return new[] { nodes[0], new IrCall("r3", "layout_dwc_syn_clear", new[] { R("r3") }) };
            case 0x800e1260 or 0x800e12d4 when nodes.Count == 2 &&
                nodes[0] == new IrBinary("r0", R("r3"), R("r0"), "and") && nodes[1] == compare:
                // The second timeout check handles the temporary reservation node.
                var peer = instruction.Address == 0x800e1260 ? R("r4") : IrValue.Imm(0);
                return new IrInstruction[] {
                    new IrCall("r0", "layout_dwc_syn_test", new[] { R("r10"), peer, R("r3"), R("r0") }),
                    compare };
            default:
                throw new InvalidDataException($"Unproven SDK synchronization acknowledgement at 0x{instruction.Address:X8}.");
        }
    }

    private void EmitDwcSynAckHelpers(HashSet<string> calls, StringBuilder output)
    {
        if (!calls.Any(call => call.StartsWith("layout_dwc_syn_", StringComparison.Ordinal))) return;
        var capacity = _expanded.Symbol("DWCMatch.peers.count");
        var tail = _expanded.Symbol("DWCMatch.tail.offset");
        var highWords = _expanded.Symbol("DWCMatch.tail.offset+DWCMatchTail.timerAndSynAcks.offset");
        output.AppendLine($$"""
            #ifndef WIICOMPILED_LAYOUT_DWC_SYN_ACKS
            #define WIICOMPILED_LAYOUT_DWC_SYN_ACKS
            #include "ppc_runtime.h"
            namespace {
                constexpr uint32_t dwc_syn_nodes = {{capacity}}u, dwc_syn_tail = {{tail}}u;
                // Bank zero remains in the original field. The indexed area
                // starts at the last stock timer, so only banks 1+ use it.
                static inline uint32_t dwc_syn_ack_bank(uint32_t match, uint32_t bank) {
                    return match + (bank ? {{highWords}}u + bank * 4u : dwc_syn_tail + 0x118u);
                }
                static inline uint32_t layout_dwc_syn_clear(uint32_t match) {
                    for (uint32_t bank = 1; bank < dwc_syn_nodes / 32; ++bank)
                        Memory::Write32(dwc_syn_ack_bank(match, bank), 0);
                    return match;
                }
                static inline uint32_t layout_dwc_syn_record(uint32_t match, uint32_t aid, uint32_t low) {
                    if (aid < 32) return low | (1u << aid);
                    if (aid < dwc_syn_nodes) {
                        const uint32_t word = dwc_syn_ack_bank(match, aid / 32);
                        Memory::Write32(word, Memory::Read32(word) | (1u << (aid % 32)));
                    }
                    return low;
                }
                static inline uint32_t layout_dwc_syn_complete(uint32_t match, uint32_t low) {
                    const uint32_t count = Memory::Read32(match + 0x30u);
                    if (count > dwc_syn_nodes) return 0;
                    uint32_t expected[dwc_syn_nodes / 32]{};
                    auto mark = [&](uint32_t aid) {
                        if (aid < dwc_syn_nodes) expected[aid / 32] |= 1u << (aid % 32);
                    };
                    for (uint32_t i = 1; i < count; ++i)
                        mark(Memory::Read8(match + 0x38u + i * 0x30u + 0x16u));
                    mark(Memory::Read8(match + dwc_syn_tail + 0x16u));
                    if (expected[0] != low) return 0;
                    for (uint32_t bank = 1; bank < dwc_syn_nodes / 32; ++bank)
                        if (expected[bank] != Memory::Read32(dwc_syn_ack_bank(match, bank))) return 0;
                    return 1;
                }
                static inline uint32_t layout_dwc_syn_test(uint32_t match, uint32_t peer, uint32_t low, uint32_t legacyBit) {
                    if (Memory::Read32(match + dwc_syn_tail + 0xe4u) != 17u) return low & legacyBit;
                    const uint32_t aid = Memory::Read8((peer ? peer : match + dwc_syn_tail) + 0x16u);
                    if (aid >= dwc_syn_nodes) return 0;
                    const uint32_t bits = aid < 32 ? low : Memory::Read32(dwc_syn_ack_bank(match, aid / 32));
                    return bits & (1u << (aid % 32));
                }
            }
            #endif
            """);
    }
}
