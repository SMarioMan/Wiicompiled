#include "abi_bridge.h"
#include "guest_function_imports.h"
#include "memory.h"
#include "mkw_guest_layout.h"

#include <algorithm>
#include <array>
#include <limits>

namespace {
constexpr uint32_t kCharacters = 24, kCpu = 1, kNone = 5;
uint32_t MenuPlayers() {
    const auto& l = MkwGuestLayout::kLayout;
    const auto data = Memory::Read32(MKW_GADDR(809BD728));
    return data ? data + l.racedataMenu + l.scenarioPlayers : 0;
}
}

// The original helper unrolls exactly twelve records. Keep its retail behavior
// at retail capacity; expanded menus must count the same storage as the race.
extern "C" void GetActiveRacedataPlayers_Expanded(CpuContext* ctx) {
    const auto& l = MkwGuestLayout::kLayout;
    if (l.playerCapacity <= 12) { MKW_GUEST_NEXT(80860500)(ctx); return; }
    const auto players = MenuPlayers();
    uint32_t count = 0;
    if (players) for (uint32_t i = 0; i < l.playerCapacity; ++i)
        count += Memory::Read32(players + i * l.playerStride + 0x10) != kNone;
    ctx->gpr[3] = count;
}
REGISTER_NATIVE_WRAPPER_AS(0x80860500, GetActiveRacedataPlayers_Expanded,
                           "GetActiveRacedataPlayers_Expanded");

// Use the game's eligibility, weight-class and random functions, but avoid the
// twelve-entry stack scratch and rejection loop. Prefer unused characters;
// after exhausting the pool, repeat the least-used characters and weights.
extern "C" void CharacterSelect_AssignExpandedCpuCharacters(CpuContext* ctx) {
    const auto& l = MkwGuestLayout::kLayout;
    if (l.playerCapacity <= 12) { MKW_GUEST_NEXT(8083EC28)(ctx); return; }
    const auto players = MenuPlayers();
    if (!players) return;
    const CpuContext saved = *ctx;
    std::array<uint32_t, kCharacters> uses{}, weights{};
    std::array<bool, kCharacters> eligible{};
    std::array<uint32_t, 3> weightUses{};
    for (uint32_t ch = 0; ch < kCharacters; ++ch) {
        ctx->gpr[3] = ch; MKW_GUEST_FUNC(8081CD3C)(ctx);
        weights[ch] = ctx->gpr[3];
        ctx->gpr[3] = ch; MKW_GUEST_FUNC(8081D020)(ctx);
        eligible[ch] = ctx->gpr[3] && weights[ch] < weightUses.size();
    }
    // Reserve every non-CPU participant, including sparse online/local slots.
    for (uint32_t i = 0; i < l.playerCapacity; ++i) {
        const auto player = players + i * l.playerStride;
        const auto type = Memory::Read32(player + 0x10), ch = Memory::Read32(player + 0x0c);
        if (type != kNone && type != kCpu && ch < kCharacters) {
            ++uses[ch]; if (weights[ch] < weightUses.size()) ++weightUses[weights[ch]];
        }
    }
    if (std::none_of(eligible.begin(), eligible.end(), [](bool value) { return value; })) {
        *ctx = saved; return;
    }
    // The game Random object occupies sixteen bytes. Give its calls a normal
    // guest stack frame without placing scratch over the caller's save area.
    const uint32_t frame = (saved.gpr[1] - 128u) & ~15u, rng = frame + 32u;
    ctx->gpr[1] = frame; ctx->gpr[3] = rng; MKW_GUEST_FUNC(80555464)(ctx);
    for (uint32_t i = 0; i < l.playerCapacity; ++i) {
        const auto player = players + i * l.playerStride;
        if (Memory::Read32(player + 0x10) != kCpu) continue;
        uint32_t minUses = std::numeric_limits<uint32_t>::max(), minWeight = minUses;
        for (uint32_t ch = 0; ch < kCharacters; ++ch) if (eligible[ch]) minUses = std::min(minUses, uses[ch]);
        for (uint32_t ch = 0; ch < kCharacters; ++ch) if (eligible[ch] && uses[ch] == minUses)
            minWeight = std::min(minWeight, weightUses[weights[ch]]);
        std::array<uint32_t, kCharacters> choices{}; uint32_t count = 0;
        for (uint32_t ch = 0; ch < kCharacters; ++ch)
            if (eligible[ch] && uses[ch] == minUses && weightUses[weights[ch]] == minWeight) choices[count++] = ch;
        ctx->gpr[3] = rng; ctx->gpr[4] = count; MKW_GUEST_FUNC(805555CC)(ctx);
        const auto ch = choices[ctx->gpr[3] % count];
        Memory::Write32(player + 0x0c, ch); ++uses[ch]; ++weightUses[weights[ch]];
    }
    ctx->gpr[3] = rng; ctx->gpr[4] = 0xffffffffu; MKW_GUEST_FUNC(80555538)(ctx);
    *ctx = saved;
}
REGISTER_NATIVE_WRAPPER_AS(0x8083EC28, CharacterSelect_AssignExpandedCpuCharacters,
                           "CharacterSelect_AssignExpandedCpuCharacters");
