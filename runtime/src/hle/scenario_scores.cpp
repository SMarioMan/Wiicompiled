#include "abi_bridge.h"
#include "guest_function_imports.h"
#include "memory.h"
#include "mkw_guest_layout.h"

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
