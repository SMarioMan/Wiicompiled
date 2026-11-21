#include "abi_bridge.h"
#include "guest_function_imports.h"
#include "memory.h"
#include "mkw_guest_layout.h"

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
