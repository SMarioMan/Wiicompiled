#include "abi_bridge.h"
#include "guest_function_imports.h"
#include "memory.h"
#include "mkw_guest_layout.h"

// Verified RMCE01 RaceScene::CreateAndInitInstances: Audio::GameHeap type 2
// requests 7 MiB + 0x100 at 8054E2B8, from the scene's secondary allocation slot.
// Its child heap is retained at scene+0xC70 and destroyed by GameScene::Reset.
// Keep original creation/destruction and their real parent ownership; borrow no
// individual allocations and introduce no fallback allocation/free map.
extern "C" void ExpHeap_CreateBattleSoundHeap(CpuContext* ctx) {
    constexpr uint32_t battleBytes = 7u * 1024u * 1024u + 0x100u;
    constexpr uint32_t primaryOwner = 0xC88, secondarySlot = 0xC98;
    if (GuestRegion::Current() == GuestRegion::Id::NtscU &&
        MkwGuestLayout::kLayout.playerCapacity > 12 &&
        ctx->lr == MKW_GADDR(805542DC) && ctx->gpr[3] == battleBytes && ctx->gpr[31]) {
        const auto scene = ctx->gpr[31];
        const auto primary = Memory::Read32(scene + primaryOwner);
        if (primary && primary != ctx->gpr[4] &&
            ctx->gpr[4] == Memory::Read32(scene + secondarySlot)) {
            const CpuContext saved = *ctx;
            ctx->gpr[4] = primary;
            MKW_GUEST_NEXT(80226AC8)(ctx);
            if (ctx->gpr[3]) return;
            // A busy/smaller MEM1 owner may reject the request. Original Create
            // cleans up failed construction; retry the untouched original call.
            *ctx = saved;
        }
    }
    MKW_GUEST_NEXT(80226AC8)(ctx);
}
REGISTER_NATIVE_WRAPPER_AS(0x80226AC8, ExpHeap_CreateBattleSoundHeap,
                           "ExpHeap_CreateBattleSoundHeap");
