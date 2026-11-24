#include "abi_bridge.h"
#include "guest_function_imports.h"
#include "memory.h"
#include "mkw_guest_layout.h"

#include <algorithm>
#include <bit>
#include <cmath>

// Keep the original avatar widgets and animations. Expanded rooms compute a
// balanced grid that scales dynamically to fit all players (up to 128) inside
// the safe viewport between the top room header and the bottom action buttons.
extern "C" void FriendRoomManager_UpdateExpanded(CpuContext* ctx) {
    const uint32_t page = ctx->gpr[3];
    MKW_GUEST_NEXT(805DA140)(ctx);
    const auto& layout = MkwGuestLayout::kLayout;
    if (layout.playerCapacity <= 12) return;
    const uint32_t count = Memory::Read32(page + layout.friendRoomPlayerCount);
    if (!count || count > layout.playerCapacity) return;

    constexpr float kAvailW = 730.f;
    constexpr float kAvailH = 250.f;
    constexpr float kCenterY = 18.f;
    constexpr float kBaseCellW = 68.f;
    constexpr float kBaseCellH = 96.f;

    uint32_t bestRows = 1;
    float bestScale = 0.f;
    if (count <= 6) {
        bestRows = 1;
        bestScale = 1.f;
    } else if (count <= 12) {
        bestRows = 2;
        bestScale = 1.f;
    } else {
        const uint32_t maxRows = std::min<uint32_t>(count, 10u);
        for (uint32_t r = 1; r <= maxRows; ++r) {
            const uint32_t maxCols = (count + r - 1) / r;
            const float sx = kAvailW / (static_cast<float>(maxCols) * kBaseCellW);
            const float sy = kAvailH / (static_cast<float>(r) * kBaseCellH);
            const float s = std::min(1.f, std::min(sx, sy));
            if (s > bestScale) {
                bestScale = s;
                bestRows = r;
            }
        }
    }
    const float scale = std::clamp(bestScale, 0.28f, 1.f);
    const uint32_t rows = bestRows;
    const uint32_t maxCols = (count + rows - 1) / rows;
    const float cellW = std::min(kBaseCellW * scale * 1.14f, kAvailW / static_cast<float>(maxCols));
    const float cellH = std::min(kBaseCellH * scale * 1.08f, kAvailH / static_cast<float>(rows));

    uint32_t index = 0;
    for (uint32_t row = 0; row < rows; ++row) {
        const uint32_t columns = count / rows + (row < count % rows ? 1u : 0u);
        for (uint32_t column = 0; column < columns; ++column, ++index) {
            const uint32_t slot = Memory::Read8(page + layout.friendRoomPlayerIds + index);
            if (slot >= layout.playerCapacity * 2) continue;
            const uint32_t control = page + layout.friendRoomControls + slot * layout.friendRoomControlStride;
            // FriendMatchingPlayer's fixed root-pane pointer and slide target;
            // these offsets are confirmed by Load and AfterControlUpdate.
            const uint32_t pane = Memory::Read32(control + 0x194);
            if (!pane) continue;
            const float x = cellW * (static_cast<float>(column) - (static_cast<float>(columns) - 1.f) * 0.5f);
            const float y = kCenterY + cellH * ((static_cast<float>(rows) - 1.f) * 0.5f - static_cast<float>(row));
            Memory::Write32(control + 0x198, std::bit_cast<uint32_t>(x));
            Memory::Write32(pane + 0x30, std::bit_cast<uint32_t>(y));
            Memory::Write32(pane + 0x44, std::bit_cast<uint32_t>(scale));
            Memory::Write32(pane + 0x48, std::bit_cast<uint32_t>(scale));
        }
    }
}
REGISTER_NATIVE_WRAPPER_AS(0x805DA140, FriendRoomManager_UpdateExpanded,
                           "FriendRoomManager_UpdateExpanded");
