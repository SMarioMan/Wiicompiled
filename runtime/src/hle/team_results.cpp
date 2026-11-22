#include "abi_bridge.h"
#include "guest_function_imports.h"
#include "memory.h"
#include "mkw_guest_layout.h"

#include <algorithm>

namespace {
void AdvanceScoreAnimation(uint32_t address, float target, float displayed, float stockStep, float step) {
    // Leave the final step and text update to the original routine.
    if (step > stockStep && displayed < target)
        Memory::WriteFloat32(address, std::min(target - stockStep, displayed + (step - stockStep)));
}
}

// USA 807EC990 animates one row at a time in half-point steps and the team
// total at one point per update. Keep that order and the original text and
// child-control updates, but scale the step for a larger field.
extern "C" void TeamResult_UpdateScoreAnimation(CpuContext* ctx) {
    const auto& layout = MkwGuestLayout::kTeamResults;
    if (layout.count > 6) {
        const auto result = ctx->gpr[3];
        const auto group = Memory::Read32(result + 0x64);
        const auto page = Memory::Read32(group + 0xc);
        if (Memory::Read32(page + 8) == 4) {
            uint32_t firstPending = 0;
            uint32_t visibleRows = 0;
            int64_t scoreSum = 0;
            for (uint32_t i = 0; i < layout.count; ++i) {
                const auto row = result + layout.players + i * layout.stride;
                if (Memory::Read32(row + 12) == UINT32_MAX) continue;
                ++visibleRows;
                const auto target = static_cast<int32_t>(Memory::Read32(row));
                scoreSum += std::max(target, 0);
                if (!firstPending && target > Memory::ReadFloat32(row + 4)) firstPending = row;
            }
            if (visibleRows > 6) {
                const auto teamTarget = static_cast<int32_t>(Memory::Read32(result + layout.teamScore));
                constexpr int64_t animationFrames = 240;
                const auto scoreBudget = std::max<int64_t>(scoreSum, teamTarget);
                const auto step = static_cast<float>((scoreBudget + animationFrames - 1) / animationFrames);
                if (firstPending) {
                    const auto target = static_cast<int32_t>(Memory::Read32(firstPending));
                    AdvanceScoreAnimation(firstPending + 4, target, Memory::ReadFloat32(firstPending + 4), 0.5f, step);
                }
                if (Memory::Read32(result + layout.resultTeamPoint)) {
                    const auto displayed = result + layout.currentScore;
                    AdvanceScoreAnimation(displayed, teamTarget, Memory::ReadFloat32(displayed), 1.0f, step);
                }
            }
        }
    }
    MKW_GUEST_NEXT(807F6FB8)(ctx);
}
REGISTER_NATIVE_WRAPPER_AS(0x807F6FB8, TeamResult_UpdateScoreAnimation, "TeamResult_UpdateScoreAnimation");

// USA 807ECBFC checks six score records in two unrolled groups of three,
// followed by the team total and child animations. Preserve that behavior and
// check the remaining records separately; widening the unroll overruns odd teams.
extern "C" void TeamResult_IsAnimating(CpuContext* ctx) {
    const auto result = ctx->gpr[3];
    MKW_GUEST_NEXT(807F7224)(ctx);
    if (ctx->gpr[3]) return;
    const auto& layout = MkwGuestLayout::kTeamResults;
    for (uint32_t i = 6; i < layout.count; ++i) {
        const auto row = result + layout.players + i * layout.stride;
        const auto score = static_cast<int32_t>(Memory::Read32(row));
        const auto animated = Memory::ReadFloat32(row + 4);
        // Match the original integer-to-double conversion followed by fsubs.
        if (static_cast<float>(static_cast<double>(score) - animated) > 0.0f) {
            ctx->gpr[3] = 1;
            return;
        }
    }
}
REGISTER_NATIVE_WRAPPER_AS(0x807F7224, TeamResult_IsAnimating, "TeamResult_IsAnimating");
