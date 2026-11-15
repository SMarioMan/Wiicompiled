#include "abi_bridge.h"
#include "guest_function_imports.h"
#include "memory.h"
#include "mkw_guest_layout.h"

// USA 8070ECA4..8070ECB4 reads a one-based position byte, sign extends it,
// computes 12-position and writes sequence variable zero for sounds E5/E6.
// Keep the authored twelve-position variable axis for expanded active rosters.
extern "C" void Audio_WriteExpandedRaceRankVariable(CpuContext* ctx) {
    const auto& layout=MkwGuestLayout::kLayout;
    if(GuestRegion::Current()==GuestRegion::Id::NtscU && layout.playerCapacity>12 &&
       ctx->lr==MKW_GADDR(8071675C) && ctx->gpr[5]==0){
        const auto data=Memory::Read32(MKW_GADDR(809BD728));
        const auto count=data?Memory::Read8(data+layout.racedataRace+4):0;
        // Invert the checked original producer, including its signed-byte wrap.
        const auto rank=uint32_t(12-int16_t(ctx->gpr[4]))&255u;
        if(count>12 && count<=layout.playerCapacity && rank>=1 && rank<=count)
            ctx->gpr[4]=11u-(rank-1u)*12u/count;
    }
    // Position zero and FF (none), smaller active rosters and other variables
    // retain the original value and winning translation's behavior.
    MKW_GUEST_NEXT(80717C48)(ctx);
}
REGISTER_NATIVE_WRAPPER_AS(0x80717C48,Audio_WriteExpandedRaceRankVariable,
                           "Audio_WriteExpandedRaceRankVariable");
