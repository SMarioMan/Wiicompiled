#include "abi_bridge.h"
#include "guest_function_imports.h"
#include "memory.h"
#include "mkw_guest_layout.h"

#include <algorithm>
#include <bit>

// Keep the original avatar widgets and animations. Expanded rooms use balanced
// rows of at most twelve, with the authored 50-unit horizontal spacing. The
// original updater still owns joins, messages, selection and slide animations.
extern "C" void FriendRoomManager_UpdateExpanded(CpuContext* ctx) {
    const uint32_t page = ctx->gpr[3];
    MKW_GUEST_NEXT(805DA140)(ctx);
    const auto& layout = MkwGuestLayout::kLayout;
    if (layout.playerCapacity <= 12) return;
    const uint32_t count = Memory::Read32(page + layout.friendRoomPlayerCount);
    if (!count || count > layout.playerCapacity) return;
    const uint32_t rows = (count + 11) / 12;
    uint32_t index = 0;
    for (uint32_t row = 0; row < rows; ++row) {
        const uint32_t columns = count / rows + (row < count % rows);
        for (uint32_t column = 0; column < columns; ++column, ++index) {
            const uint32_t slot = Memory::Read8(page + layout.friendRoomPlayerIds + index);
            if (slot >= layout.playerCapacity * 2) continue;
            const uint32_t control = page + layout.friendRoomControls + slot * layout.friendRoomControlStride;
            // FriendMatchingPlayer's fixed root-pane pointer and slide target;
            // these offsets are confirmed by Load and AfterControlUpdate.
            const uint32_t pane = Memory::Read32(control + 0x194);
            if (!pane) continue;
            const float x = 50.f * (static_cast<float>(column) - (columns - 1) * .5f);
            const float y = 140.f - row * 100.f;
            Memory::Write32(control + 0x198, std::bit_cast<uint32_t>(x));
            Memory::Write32(pane + 0x30, std::bit_cast<uint32_t>(y));
        }
    }
}
REGISTER_NATIVE_WRAPPER_AS(0x805DA140, FriendRoomManager_UpdateExpanded,
                           "FriendRoomManager_UpdateExpanded");
