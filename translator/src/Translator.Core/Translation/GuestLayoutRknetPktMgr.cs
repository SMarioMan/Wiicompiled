using System.Text;
using Translator.Core.Disassembly;
using Translator.Core.Ir;

namespace Translator.Core.Translation;

public sealed partial class GuestLayoutRelocations
{
    private IReadOnlyList<IrInstruction> RewriteRknetPktMgr(
        PpcInstruction instruction, IReadOnlyList<IrInstruction> nodes)
    {
        IrValue R(string name) => IrValue.Register(name);
        var compare0 = new IrSetCrField(0, R("r0"), IrValue.Imm(0), false);
        var discard = $"addr_layout_rknet_pktmgr_{instruction.Address:X8}_loc";
        switch (instruction.Address)
        {
            case 0x8064edd4 or 0x8064f9a0 when nodes.Count == 1 && nodes[0] == new IrStore(new IrAddress("r3", 18), R("r0"), 2):
                return new[] { nodes[0], new IrCall(discard, "layout_rknet_pktmgr_clear_all", new[] { R("r3") }) };
            case 0x8064eea8 when nodes.Count == 1 && nodes[0] == new IrStore(new IrAddress("r31", 4), R("r30"), 4):
                return new[] { nodes[0], new IrCall(discard, "layout_rknet_pktmgr_clear_all", new[] { R("r31") }) };
            case 0x8065134c when nodes.Count == 1 && nodes[0] == new IrBinary("r5", R("r5"), R("r0"), "or"):
                return new IrInstruction[] { new IrCall("r5", "layout_rknet_pktmgr_set", new[] { R("r3"), R("r4"), IrValue.Imm(0), R("r5") }) };
            case 0x80651364 when nodes.Count == 1 && nodes[0] == new IrBinary("r5", R("r5"), R("r0"), "or"):
                return new IrInstruction[] { new IrCall("r5", "layout_rknet_pktmgr_set", new[] { R("r3"), R("r4"), IrValue.Imm(2), R("r5") }) };
            case 0x8065139c when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r5"), R("r0"), "or"):
                return new IrInstruction[] { new IrCall("r0", "layout_rknet_pktmgr_set", new[] { R("r3"), R("r4"), IrValue.Imm(1), R("r5") }) };
            case 0x8064f43c or 0x8064fb74 or 0x8065063c when nodes.Count == 2 &&
                nodes[0] == new IrBinary("r0", R("r0"), R("r3"), "and") && nodes[1] == compare0:
                return new IrInstruction[] {
                    new IrCall("r0", "layout_rknet_pktmgr_test", new[] { R("r29"), R("r4"), IrValue.Imm(2) }),
                    compare0 };
            case 0x8064fca4 when nodes.Count == 1 && nodes[0] == new IrBinary("r0", R("r0"), R("r8"), "sub"):
                return new IrInstruction[] {
                    new IrCall("r0", "layout_rknet_pktmgr_all_rh1_diff", new[] { R("r30"), R("r3"), R("r5"), R("r13") }) };
            default:
                throw new InvalidDataException($"Unproven RKNet PacketMgr bitflag operation at 0x{instruction.Address:X8}.");
        }
    }

    private void EmitRknetPktMgrHelpers(HashSet<string> calls, StringBuilder output)
    {
        if (!calls.Any(call => call.StartsWith("layout_rknet_pktmgr_", StringComparison.Ordinal))) return;
        var nodes = _expanded.Symbol("DWCMatch.peers.count");
        var capacity = _expanded.Players;
        var banks = (capacity + 31) / 32;
        var highOff = _expanded.Symbol("RKNetPacketMgr.highFlags.offset");
        var ctrlSubs = _expanded.Symbol("RKNetController.subs.offset");
        var ctrlStride = _expanded.Symbol("RKNetSub.size");
        var ctrlCur = _expanded.Symbol("RKNetController.currentSub.offset");
        var ctrlPlayerAids = _expanded.Symbol("RKNetController.playerAids.offset");
        var ctrlDiscPlayers = _expanded.Symbol("RKNetController.disconnectedPlayers.offset");
        output.AppendLine($$"""
            #ifndef WIICOMPILED_LAYOUT_RKNET_PKTMGR
            #define WIICOMPILED_LAYOUT_RKNET_PKTMGR
            #include "ppc_runtime.h"
            namespace {
                constexpr uint32_t rknet_pktmgr_banks = {{banks}}u, rknet_pktmgr_nodes = {{nodes}}u, rknet_pktmgr_cap = {{capacity}}u;
                static inline uint32_t rknet_pktmgr_word(uint32_t mgr, uint32_t bank, uint32_t slot) {
                    return mgr + (bank ? {{highOff}}u + (bank - 1u) * 12u + slot * 4u : 4u + slot * 4u);
                }
                static inline uint32_t layout_rknet_pktmgr_clear_all(uint32_t mgr) {
                    if (!mgr) return 0u;
                    for (uint32_t b = 1; b < rknet_pktmgr_banks; ++b)
                        for (uint32_t s = 0; s < 3u; ++s)
                            Memory::Write32(rknet_pktmgr_word(mgr, b, s), 0u);
                    return 0u;
                }
                static inline uint32_t layout_rknet_pktmgr_set(uint32_t mgr, uint32_t aid, uint32_t slot, uint32_t low) {
                    if (aid < 32u) return low | (1u << aid);
                    if (aid < rknet_pktmgr_banks * 32u) {
                        const uint32_t addr = rknet_pktmgr_word(mgr, aid / 32u, slot);
                        Memory::Write32(addr, Memory::Read32(addr) | (1u << (aid % 32u)));
                    }
                    return low;
                }
                static inline uint32_t layout_rknet_pktmgr_test(uint32_t mgr, uint32_t aid, uint32_t slot) {
                    if (!mgr || aid >= rknet_pktmgr_banks * 32u) return 0u;
                    const uint32_t word = Memory::Read32(rknet_pktmgr_word(mgr, aid / 32u, slot));
                    return (word >> (aid % 32u)) & 1u;
                }
                static inline uint32_t layout_rknet_pktmgr_all_rh1_diff(uint32_t mgr, uint32_t ctrl, uint32_t raceinfo, uint32_t sda) {
                    if (!mgr || !ctrl || !raceinfo) return 0u;
                    const uint32_t racedata = Memory::Read32(0x809b8f68u);
                    if (!racedata) return 0u;
                    const uint32_t playerCount = Memory::Read8(racedata + 0x24u);
                    const uint32_t cur = Memory::Read32(ctrl + {{ctrlCur}}u);
                    const uint32_t sub = ctrl + {{ctrlSubs}}u + cur * {{ctrlStride}}u;
                    const uint32_t localAid = Memory::Read8(sub + 0x21u);
                    const uint32_t lowAvail = Memory::Read32(sub + 0x10u);
                    const uint32_t playersPtr = Memory::Read32(raceinfo + 0x0cu);
                    uint32_t highAvail[rknet_pktmgr_banks]{};
                    if (Memory::Read32(sda - 0x6910u)) {
                        const uint32_t match = Memory::Read32(sda - 0x68f4u);
                        if (match) {
                            const uint32_t count = Memory::Read32(match + 0x30u);
                            if (count <= rknet_pktmgr_nodes) {
                                for (uint32_t i = 0; i < count; ++i) {
                                    const uint32_t aid = Memory::Read8(match + 0x38u + i * 0x30u + 0x16u);
                                    if (aid >= 32u && aid < rknet_pktmgr_banks * 32u)
                                        highAvail[aid / 32u] |= 1u << (aid % 32u);
                                }
                            }
                        }
                    }
                    for (uint32_t p = 0; p < playerCount && p < rknet_pktmgr_cap; ++p) {
                        const uint32_t discWord = Memory::Read32(ctrl + {{ctrlDiscPlayers}}u + (p / 32u) * 4u);
                        if ((discWord >> (p % 32u)) & 1u) continue;
                        const uint32_t player = Memory::Read32(playersPtr + p * 4u);
                        if (!player || (Memory::Read32(player + 0x38u) & 0x10u)) continue;
                        const uint32_t aid = Memory::Read8(ctrl + {{ctrlPlayerAids}}u + p);
                        if (aid == localAid || aid >= rknet_pktmgr_banks * 32u) continue;
                        const bool avail = aid < 32u ? ((lowAvail >> aid) & 1u) != 0u : ((highAvail[aid / 32u] >> (aid % 32u)) & 1u) != 0u;
                        if (avail && !layout_rknet_pktmgr_test(mgr, aid, 0u)) return 1u;
                    }
                    return 0u;
                }
            }
            #endif
            """);
    }
}
