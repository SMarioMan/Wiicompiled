#include "abi_bridge.h"
#include "guest_function_imports.h"
#include "memory.h"
#include "mkw_guest_layout.h"

#include <algorithm>
#include <utility>
#include <vector>
#include <stdexcept>

// USA 80529160 accumulates the prior scores into two 16-bit stack totals.
// Expanded VS series can overflow those totals without any invalid access.
extern "C" void Scenario_ExpandedTeamWinner(CpuContext* ctx) {
    const auto& layout = MkwGuestLayout::kLayout;
    const auto data = Memory::Read32(MKW_GADDR(809BD728));
    const auto count = data ? Memory::Read8(data + layout.racedataRace + 4) : 0;
    if (count <= 12) {
        MKW_GUEST_NEXT(8052DCA8)(ctx);
        return;
    }
    if (count > layout.playerCapacity)
        throw std::runtime_error("Team winner racer count exceeds guest layout capacity");
    uint32_t totals[2]{};
    const auto scenario = ctx->gpr[3];
    for (uint32_t i = 0; i < count; ++i) {
        const auto player = scenario + layout.scenarioPlayers + i * layout.playerStride;
        const auto team = Memory::Read32(player + 0xcc);
        if (team > 1) throw std::runtime_error("Invalid team in team winner calculation");
        totals[team] += Memory::Read16(player + 0xd8);
    }
    // Original ties choose team zero.
    ctx->gpr[3] = totals[0] < totals[1] ? 1 : 0;
}
REGISTER_NATIVE_WRAPPER_AS(0x8052DCA8, Scenario_ExpandedTeamWinner, "Scenario_ExpandedTeamWinner");

// These retail helpers unroll two groups of six players. Keep those groups and
// their register/return behavior, then apply the same operations to later slots.
extern "C" void Scenario_ResetExpandedScores(CpuContext* ctx) {
    const auto scenario = ctx->gpr[3];
    MKW_GUEST_NEXT(8052E668)(ctx);
    const auto& layout = MkwGuestLayout::kLayout;
    for (uint32_t i = 12; i < layout.playerCapacity; ++i) {
        const auto player = scenario + layout.scenarioPlayers + i * layout.playerStride;
        Memory::Write16(player + 0xd8, 0);
        Memory::Write16(player + 0xde, 0);
        Memory::Write8(player + 0xe0, i + 1);
        Memory::Write8(player + 0xe1, i + 1);
    }
}
REGISTER_NATIVE_WRAPPER_AS(0x8052E668, Scenario_ResetExpandedScores, "Scenario_ResetExpandedScores");

extern "C" void Scenario_AdvanceExpandedScores(CpuContext* ctx) {
    const auto scenario = ctx->gpr[3];
    MKW_GUEST_NEXT(8052E770)(ctx);
    const auto& layout = MkwGuestLayout::kLayout;
    for (uint32_t i = 12; i < layout.playerCapacity; ++i) {
        const auto player = scenario + layout.scenarioPlayers + i * layout.playerStride;
        Memory::Write8(player + 0xe1, Memory::Read8(player + 0xe2));
        Memory::Write16(player + 0xd8, Memory::Read16(player + 0xda));
    }
}
REGISTER_NATIVE_WRAPPER_AS(0x8052E770, Scenario_AdvanceExpandedScores, "Scenario_AdvanceExpandedScores");

// Online setup clears previous ranks before this helper compacts the active
// field. Its retail scratch, scans and writeback cover exactly twelve slots.
extern "C" void Scenario_ReindexExpandedPreviousRanks(CpuContext* ctx) {
    const auto scenario = ctx->gpr[3];
    const auto& layout = MkwGuestLayout::kLayout;
    const auto mode = Memory::Read32(scenario + layout.scenarioSettings + 8);
    if (layout.playerCapacity <= 12 || mode < 7 || mode > 10) {
        MKW_GUEST_NEXT(8052F1E0)(ctx);
        return;
    }
    std::vector<std::pair<uint32_t, uint32_t>> order;
    for (uint32_t i = 0; i < layout.playerCapacity; ++i) {
        const auto player = scenario + layout.scenarioPlayers + i * layout.playerStride;
        const auto rank = Memory::Read8(player + 0xe1);
        if (Memory::Read32(player + 0x10) != 5 && rank <= layout.playerCapacity)
            order.emplace_back(rank ? rank : layout.playerCapacity + 1, i);
    }
    // Retain the original helper's register/return behavior and any unrelated
    // effects, then replace its twelve-slot result from the original ranks.
    MKW_GUEST_NEXT(8052F1E0)(ctx);
    for (uint32_t i = 0; i < layout.playerCapacity; ++i)
        Memory::Write8(scenario + layout.scenarioPlayers + i * layout.playerStride + 0xe1, i + 1);
    std::stable_sort(order.begin(), order.end(), [](const auto& a, const auto& b) { return a.first < b.first; });
    uint32_t next = 1;
    for (const auto& entry : order)
        Memory::Write8(scenario + layout.scenarioPlayers + entry.second * layout.playerStride + 0xe1, next++);
}
REGISTER_NATIVE_WRAPPER_AS(0x8052F1E0, Scenario_ReindexExpandedPreviousRanks, "Scenario_ReindexExpandedPreviousRanks");
