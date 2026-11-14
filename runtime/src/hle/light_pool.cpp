#include "abi_bridge.h"
#include "guest_function_imports.h"
#include "memory.h"
#include "mkw_guest_layout.h"

#include <cstdint>
#include <unordered_map>

namespace {
// Verified NTSC-U LightMgrHolder lists: acquired at +0, available at +0xC.
// nw4r::ut::List is {head, tail, u16 count, u16 linkOffset}.
struct Shares {
    uint64_t generation = 0, cursor = 0;
    std::unordered_map<uint32_t, uint32_t> additionalOwners;
};
std::unordered_map<uint32_t, Shares> holders;
uint64_t nextGeneration = 1;

bool ExpandedRace() {
    const auto& layout = MkwGuestLayout::kLayout;
    if (layout.playerCapacity <= 12) return false;
    const auto data = Memory::Read32(MKW_GADDR(809BD728));
    return data && Memory::Read8(data + layout.racedataRace + 4) > 12;
}
}

// Use the base constructor, called by the race/menu/Mii subclasses. Starting
// another holder must not discard the ownership of an already live holder.
// NTSC-U: 8055CCA4; source identity is PAL, resolved by the existing region map.
extern "C" void LightHolder_ResetSharing(CpuContext* ctx) {
    holders.erase(ctx->gpr[3]);
    MKW_GUEST_NEXT(80561024)(ctx);
}
REGISTER_NATIVE_WRAPPER_AS(0x80561024, LightHolder_ResetSharing, "LightHolder_ResetSharing");

// NTSC-U 8055D018 first moves an available light into the acquired list. Only
// when that original operation fails do expanded races share an acquired light.
// No light-set identifier, original allocation or guest pool extent is enlarged.
extern "C" void LightHolder_AcquireExpanded(CpuContext* ctx) {
    const auto holder = ctx->gpr[3];
    MKW_GUEST_NEXT(80561398)(ctx);
    if (ctx->gpr[3] || !ExpandedRace()) return;
    const uint32_t count = Memory::Read16(holder + 8);
    if (!count) return;
    auto& shares = holders[holder];
    if (!shares.generation) shares.generation = nextGeneration++;
    const auto offset = Memory::Read16(holder + 10);
    auto light = Memory::Read32(holder);
    for (uint32_t step = static_cast<uint32_t>(shares.cursor++ % count); step && light; --step)
        light = Memory::Read32(light + offset + 4);
    if (light) ++shares.additionalOwners[light];
    ctx->gpr[3] = light;
}
REGISTER_NATIVE_WRAPPER_AS(0x80561398, LightHolder_AcquireExpanded, "LightHolder_AcquireExpanded");

// NTSC-U 8055D084 returns a light to the available list and clears its acquired
// flag. Defer that operation until its final owner releases it, in any order.
extern "C" void LightHolder_ReleaseExpanded(CpuContext* ctx) {
    const auto holder = holders.find(ctx->gpr[3]);
    if (holder != holders.end()) {
        const auto light = holder->second.additionalOwners.find(ctx->gpr[4]);
        if (light != holder->second.additionalOwners.end()) {
            if (!--light->second) holder->second.additionalOwners.erase(light);
            return;
        }
    }
    MKW_GUEST_NEXT(80561404)(ctx);
}
REGISTER_NATIVE_WRAPPER_AS(0x80561404, LightHolder_ReleaseExpanded, "LightHolder_ReleaseExpanded");

// NTSC-U race-holder destructor 8055DB40. Preserve references throughout any
// guest teardown, then discard only this lifetime's bookkeeping. A constructor
// reusing the same address during a callback owns a different generation.
extern "C" void RaceLightHolder_RetireSharing(CpuContext* ctx) {
    const auto base = ctx->gpr[3];
    const auto existing = holders.find(base);
    const auto generation = existing == holders.end() ? 0 : existing->second.generation;
    MKW_GUEST_NEXT(80561EC0)(ctx);
    const auto current = holders.find(base);
    if (current != holders.end() && current->second.generation == generation) holders.erase(current);
}
REGISTER_NATIVE_WRAPPER_AS(0x80561EC0, RaceLightHolder_RetireSharing, "RaceLightHolder_RetireSharing");
