#include "abi_bridge.h"
#include "guest_function_imports.h"
#include "mkw_guest_layout.h"

// Expanded Race.bmg files reserve these IDs; the original range is followed by
// unrelated score/control strings, so adding the rank to 0x514 is unsafe >12.
extern "C" void GetPositionMessage_Expanded(CpuContext* ctx) {
    const uint32_t rank = ctx->gpr[3];
    if (rank > 12 && rank <= MkwGuestLayout::kLayout.playerCapacity) {
        ctx->gpr[3] = 0x70000000u + rank;
        return;
    }
    MKW_GUEST_NEXT(80833764)(ctx);
}
REGISTER_NATIVE_WRAPPER_AS(0x80833764, GetPositionMessage_Expanded,
                           "GetPositionMessage_Expanded");
