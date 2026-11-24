#include "abi_bridge.h"
#include "guest_abi/guest_region.h"
#include "guest_function_imports.h"
#include "memory.h"
#include "mkw_guest_layout.h"

#include <algorithm>
#include <cstdint>

// Effects::Player::LoadEffects (PAL 0x8068F044 / USA 0x8068ABBC) selects one of
// 12 authored BREFF effect template variants (0..11) from this->playerIdx (+0x12E)
// and branches past the switch for playerIdx > 11, leaving the 5 item EGG::Effect*
// pointers (+0x2AC..+0x2BC) NULL and crashing in UpdateItemEffect for players 13+.
// Temporarily wrap playerIdx % 12 around LoadEffects so every player allocates
// its own 5 independent EGG::Effect instances from a valid template variant.
extern "C" void EffectsPlayer_LoadEffects_Expanded(CpuContext* ctx) {
    const auto& layout = MkwGuestLayout::kLayout;
    const uint32_t player = ctx->gpr[3];
    if (layout.playerCapacity <= 12 || !player) {
        MKW_GUEST_NEXT(8068F044)(ctx);
        return;
    }
    const uint8_t realIdx = Memory::Read8(player + 302u);
    if (realIdx >= 12u) {
        Memory::Write8(player + 302u, static_cast<uint8_t>(realIdx % 12u));
        MKW_GUEST_NEXT(8068F044)(ctx);
        Memory::Write8(player + 302u, realIdx);
    } else {
        MKW_GUEST_NEXT(8068F044)(ctx);
    }
}
REGISTER_NATIVE_WRAPPER_AS(0x8068F044, EffectsPlayer_LoadEffects_Expanded,
                           "EffectsPlayer_LoadEffects_Expanded");

// GMDataOnlineBalloonBattle (PAL 0x8053BBF4 / USA 0x805370AC) keeps fixed 12-slot
// event packet/state arrays (+0x44, +0xA4, +0xBC, +0xD4). In expanded rooms (>12
// players), looping up to Racedata::racesScenario.playerCount reads NULL past slot
// 11 and faults at NULL+0x86. Temporarily clamp playerCount to 12 for the duration
// of GMDataOnlineBalloonBattle::Update and restore it immediately on return.
extern "C" void GMDataOnlineBalloonBattle_Update_Expanded(CpuContext* ctx) {
    const auto& layout = MkwGuestLayout::kLayout;
    if (layout.playerCapacity <= 12) {
        MKW_GUEST_NEXT(8053BBF4)(ctx);
        return;
    }
    const uint32_t racedata = Memory::Read32(MKW_GADDR(809BD728));
    if (!racedata) {
        MKW_GUEST_NEXT(8053BBF4)(ctx);
        return;
    }
    const uint32_t countAddr = racedata + 36u;
    const uint8_t realCount = Memory::Read8(countAddr);
    if (realCount > 12u) {
        Memory::Write8(countAddr, 12u);
        MKW_GUEST_NEXT(8053BBF4)(ctx);
        Memory::Write8(countAddr, realCount);
    } else {
        MKW_GUEST_NEXT(8053BBF4)(ctx);
    }
}
REGISTER_NATIVE_WRAPPER_AS(0x8053BBF4, GMDataOnlineBalloonBattle_Update_Expanded,
                           "GMDataOnlineBalloonBattle_Update_Expanded");
