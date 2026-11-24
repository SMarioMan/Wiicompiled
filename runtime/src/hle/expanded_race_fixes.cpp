#include "abi_bridge.h"
#include "guest_abi/guest_region.h"
#include "guest_function_imports.h"
#include "memory.h"
#include "mkw_guest_layout.h"

#include <algorithm>
#include <array>
#include <cstdint>
#include <cstring>

namespace {

// Host-side expanded respawn-seconds table for GMDataBattle (supports up to 128
// players; vanilla GMDataBattle only has int16_t respawnSeconds[12] at +0x1C).
uint32_t gBattleRespawnOwner = 0u;
std::array<int16_t, 128> gBattleRespawnSeconds{};

// Host-side 128-player kart-to-kart collision pair bitset. Vanilla MKW stores
// only a uint16_t checkedKartBitmask at Kart::Status+0x84 (1 << otherPlayerIdx),
// which overflows for players >= 16 and causes every colliding pair involving
// players 16..127 to be evaluated twice per frame (double impulse + 2x CPU).
std::array<std::array<uint64_t, 2>, 128> gCheckedKartPairs{};

inline bool TestAndSetKartPair(uint8_t a, uint8_t b) noexcept {
    if (a >= 128u || b >= 128u) {
        return false;
    }
    const uint64_t maskB = uint64_t{1} << (b & 63u);
    const uint32_t wordB = (b >> 6u) & 1u;
    if ((gCheckedKartPairs[a][wordB] & maskB) != 0u) {
        return true;
    }
    gCheckedKartPairs[a][wordB] |= maskB;
    const uint64_t maskA = uint64_t{1} << (a & 63u);
    const uint32_t wordA = (a >> 6u) & 1u;
    gCheckedKartPairs[b][wordA] |= maskA;
    return false;
}

inline float ReadGuestFloat32(uint32_t addr) {
    const uint32_t bits = Memory::Read32(addr);
    float value = 0.0f;
    std::memcpy(&value, &bits, sizeof(value));
    return value;
}

} // namespace

// ---------------------------------------------------------------------------
// 1. Effects::Player::LoadEffects (PAL 0x8068F044 / USA 0x8068ABBC)
// ---------------------------------------------------------------------------
// Selects one of 12 authored BREFF effect template variants (0..11) from
// this->playerIdx (+0x12E) and branches past the switch for playerIdx > 11,
// leaving the 5 item EGG::Effect* pointers (+0x2AC..+0x2BC) NULL and crashing
// in UpdateItemEffect for players 13+.
// Temporarily wrap playerIdx % 12 around LoadEffects so every player allocates
// its own 5 independent EGG::Effect instances from a valid template variant,
// then restore playerIdx and give each player a unique visibility group ID.
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
        Memory::Write32(player + 308u, static_cast<uint32_t>(realIdx) + 2u);
    } else {
        MKW_GUEST_NEXT(8068F044)(ctx);
    }
}
REGISTER_NATIVE_WRAPPER_AS(0x8068F044, EffectsPlayer_LoadEffects_Expanded,
                           "EffectsPlayer_LoadEffects_Expanded");

// ---------------------------------------------------------------------------
// 2. GMDataOnlineBalloonBattle::Update (PAL 0x8053BBF4 / USA 0x805370AC)
// ---------------------------------------------------------------------------
// Keeps fixed 12-slot event packet/state arrays (+0x44, +0xA4, +0xBC, +0xD4).
// In expanded rooms (>12 players), looping up to Racedata::racesScenario.playerCount
// reads NULL past slot 11 and faults at NULL+0x86. Temporarily clamp playerCount
// to 12 for the fixed 12-slot packet loop, restore realCount, and then re-run
// GMDataBattle::HandlePositionTracking (PAL 0x80538418) so players 12..127 are
// ranked properly.
extern "C" void GMDataOnlineBalloonBattle_Update_Expanded(CpuContext* ctx) {
    const auto& layout = MkwGuestLayout::kLayout;
    const uint32_t thisPtr = ctx->gpr[3];
    if (layout.playerCapacity <= 12 || !thisPtr) {
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
        ctx->gpr[3] = thisPtr;
        InvokeIndirectCpu(MKW_GADDR(80538418), ctx);
    } else {
        MKW_GUEST_NEXT(8053BBF4)(ctx);
    }
}
REGISTER_NATIVE_WRAPPER_AS(0x8053BBF4, GMDataOnlineBalloonBattle_Update_Expanded,
                           "GMDataOnlineBalloonBattle_Update_Expanded");

// ---------------------------------------------------------------------------
// 3. GMDataOnlineBalloonBattle::vf_0x28 (PAL 0x8053D3A0 / USA 0x80538858)
// ---------------------------------------------------------------------------
// Loops i = 0..playerCount-1, calls BattleBalloonManager::PopBalloons, and
// writes *(u32*)(this + 212 + i * 4) = 1. For i >= 12, that write overwrites
// embedded vtable pointers at this + 264..364 with 0x1! Clamp playerCount to 12
// around the original call, restore realCount, and pop balloons for i = 12..N-1
// without overflowing the 12-slot array.
extern "C" void GMDataOnlineBalloonBattle_vf_0x28_Expanded(CpuContext* ctx) {
    const auto& layout = MkwGuestLayout::kLayout;
    if (layout.playerCapacity <= 12) {
        MKW_GUEST_NEXT(8053D3A0)(ctx);
        return;
    }
    const uint32_t racedata = Memory::Read32(MKW_GADDR(809BD728));
    if (!racedata) {
        MKW_GUEST_NEXT(8053D3A0)(ctx);
        return;
    }
    const uint32_t countAddr = racedata + 36u;
    const uint8_t realCount = Memory::Read8(countAddr);
    if (realCount <= 12u) {
        MKW_GUEST_NEXT(8053D3A0)(ctx);
        return;
    }
    Memory::Write8(countAddr, 12u);
    MKW_GUEST_NEXT(8053D3A0)(ctx);
    Memory::Write8(countAddr, realCount);

    const uint32_t balloonMgr = Memory::Read32(MKW_GADDR(809C4748));
    if (!balloonMgr) {
        return;
    }
    const uint32_t limit = std::min<uint32_t>(realCount, layout.playerCapacity);
    for (uint32_t i = 12u; i < limit; ++i) {
        const uint8_t curBalloons = Memory::Read8(balloonMgr + i * 24u + 10244u);
        ctx->gpr[3] = balloonMgr;
        ctx->gpr[4] = i;
        ctx->gpr[5] = 0u;
        ctx->gpr[6] = 1u;
        ctx->gpr[7] = 0u;
        ctx->gpr[8] = curBalloons;
        ctx->gpr[9] = 0u;
        InvokeIndirectCpu(MKW_GADDR(80869FD0), ctx);
    }
}
REGISTER_NATIVE_WRAPPER_AS(0x8053D3A0, GMDataOnlineBalloonBattle_vf_0x28_Expanded,
                           "GMDataOnlineBalloonBattle_vf_0x28_Expanded");

// ---------------------------------------------------------------------------
// 4. GMDataBattle::Init (PAL 0x80538FEC / USA 0x805344A4) &
//    GMDataBattle::AdvanceRespawnSeconds (PAL 0x80538EB4 / USA 0x8053436C)
// ---------------------------------------------------------------------------
// GMDataBattle stores fixed int16_t respawnSeconds[12] at this + 28. When a
// player with index >= 12 respawns in Balloon Battle or Coin Runners, writing
// this + 28 + playerIdx * 2 corrupts GMDataOnlineBalloonBattle packet pointers
// (+56..+116) or overflows past the 56-byte GMDataOnlineCoinBattle object into
// the heap. Track slots 12..127 in host memory instead.
extern "C" void GMDataBattle_Init_Expanded(CpuContext* ctx) {
    const uint32_t thisPtr = ctx->gpr[3];
    MKW_GUEST_NEXT(80538FEC)(ctx);
    if (thisPtr) {
        const int16_t initVal = static_cast<int16_t>(Memory::Read16(thisPtr + 28u));
        gBattleRespawnOwner = thisPtr;
        gBattleRespawnSeconds.fill(initVal);
    }
}
REGISTER_NATIVE_WRAPPER_AS(0x80538FEC, GMDataBattle_Init_Expanded,
                           "GMDataBattle_Init_Expanded");

extern "C" void GMDataBattle_AdvanceRespawnSeconds_Expanded(CpuContext* ctx) {
    const auto& layout = MkwGuestLayout::kLayout;
    const uint32_t thisPtr = ctx->gpr[3];
    const uint32_t playerIdx = ctx->gpr[4] & 0xFFu;
    if (layout.playerCapacity <= 12 || !thisPtr || playerIdx < 12u) {
        MKW_GUEST_NEXT(80538EB4)(ctx);
        return;
    }
    if (playerIdx >= gBattleRespawnSeconds.size()) {
        ctx->gpr[3] = 0u;
        return;
    }
    if (gBattleRespawnOwner != thisPtr) {
        const int16_t initVal = static_cast<int16_t>(Memory::Read16(thisPtr + 28u));
        gBattleRespawnOwner = thisPtr;
        gBattleRespawnSeconds.fill(initVal);
    }
    const uint32_t vtable = Memory::Read32(thisPtr);
    const uint32_t getStepFn = Memory::Read32(vtable + 68u);
    const uint32_t getMaxFn = Memory::Read32(vtable + 72u);

    ctx->gpr[3] = thisPtr;
    InvokeIndirectCpu(getStepFn, ctx);
    const int32_t step = static_cast<int32_t>(ctx->gpr[3]);

    int16_t nextVal = static_cast<int16_t>(
        static_cast<int32_t>(gBattleRespawnSeconds[playerIdx]) + step);

    ctx->gpr[3] = thisPtr;
    InvokeIndirectCpu(getMaxFn, ctx);
    const int16_t maxVal = static_cast<int16_t>(ctx->gpr[3]);

    if (nextVal > maxVal) {
        nextVal = maxVal;
    }
    gBattleRespawnSeconds[playerIdx] = nextVal;
    ctx->gpr[3] = static_cast<uint32_t>(static_cast<int32_t>(nextVal));
}
REGISTER_NATIVE_WRAPPER_AS(0x80538EB4, GMDataBattle_AdvanceRespawnSeconds_Expanded,
                           "GMDataBattle_AdvanceRespawnSeconds_Expanded");

// ---------------------------------------------------------------------------
// 5. GMDataOnlineVS::HandlePositionTracking (PAL 0x8053F4A0 / USA 0x80539F64)
// ---------------------------------------------------------------------------
// Vanilla code sign-extends u8 position with extsb (so 128th place = 0x80
// becomes -128 -> -129 * 8 = -1032 bytes below r1!) and only initializes the
// first 12 bytes of stack u8 order[128], leaving indices 12..127 as stack
// garbage -> OOB deref of Raceinfo::players[garbage]. Replace with a clean
// host-side sort for >12p Online VS / Team VS.
extern "C" void GMDataOnlineVS_HandlePositionTracking_Expanded(CpuContext* ctx) {
    const auto& layout = MkwGuestLayout::kLayout;
    const uint32_t thisPtr = ctx->gpr[3];
    if (layout.playerCapacity <= 12 || !thisPtr) {
        MKW_GUEST_NEXT(8053F4A0)(ctx);
        return;
    }
    const uint32_t racedata = Memory::Read32(MKW_GADDR(809BD728));
    const uint32_t raceinfo = Memory::Read32(MKW_GADDR(809BD730));
    const uint32_t gmRaceinfo = Memory::Read32(thisPtr + 4u);
    if (!racedata || !raceinfo || !gmRaceinfo) {
        MKW_GUEST_NEXT(8053F4A0)(ctx);
        return;
    }
    const uint8_t rawCount = Memory::Read8(racedata + 36u);
    if (rawCount <= 12u) {
        MKW_GUEST_NEXT(8053F4A0)(ctx);
        return;
    }
    const uint32_t playerCount = std::min<uint32_t>(rawCount, 128u);
    const uint32_t riPlayers = Memory::Read32(raceinfo + 12u);
    const uint32_t gmPlayers = Memory::Read32(gmRaceinfo + 12u);
    const uint32_t posTable = Memory::Read32(gmRaceinfo + 24u);
    if (!riPlayers || !gmPlayers || !posTable) {
        return;
    }

    struct RankEntry {
        double score;
        uint8_t prevPos;
        uint8_t id;
    };
    std::array<RankEntry, 128> entries{};

    for (uint32_t i = 0u; i < playerCount; ++i) {
        const uint32_t riPlayer = Memory::Read32(riPlayers + i * 4u);
        const uint32_t gmPlayer = Memory::Read32(gmPlayers + i * 4u);
        if (!riPlayer || !gmPlayer) {
            entries[i] = {-1e12, 255u, static_cast<uint8_t>(i)};
            continue;
        }
        const uint32_t riFlags = Memory::Read32(riPlayer + 56u);
        const uint32_t gmFlags = Memory::Read32(gmPlayer + 56u);
        const uint8_t prevPos = Memory::Read8(gmPlayer + 32u);

        double score = 0.0;
        if ((riFlags & 2u) != 0u) {
            // Finished: rank strictly above active racers; lower finish time is better.
            const uint32_t timerPtr = Memory::Read32(riPlayer + 64u);
            uint32_t totalMs = 0u;
            if (timerPtr) {
                const uint16_t min = Memory::Read16(timerPtr + 4u);
                const uint8_t sec = Memory::Read8(timerPtr + 6u);
                const uint16_t ms = Memory::Read16(timerPtr + 8u);
                totalMs = (static_cast<uint32_t>(min) * 60u + static_cast<uint32_t>(sec)) * 1000u +
                          static_cast<uint32_t>(ms);
            }
            score = 1e12 - static_cast<double>(totalMs) - static_cast<double>(i) * 1e-3;
        } else if ((riFlags & 16u) != 0u || (gmFlags & 32u) != 0u) {
            // Disconnected / retired: rank strictly below active racers.
            score = -1e9 - static_cast<double>(i + 1u);
        } else {
            // Active racer: higher raceCompletion is better.
            score = static_cast<double>(ReadGuestFloat32(gmPlayer + 12u));
        }

        entries[i] = {score, prevPos, static_cast<uint8_t>(i)};
    }

    std::stable_sort(entries.begin(), entries.begin() + playerCount,
                     [](const RankEntry& a, const RankEntry& b) {
                         if (a.score != b.score) {
                             return a.score > b.score;
                         }
                         if (a.prevPos != b.prevPos) {
                             return a.prevPos < b.prevPos;
                         }
                         return a.id < b.id;
                     });

    for (uint32_t rank = 0u; rank < playerCount; ++rank) {
        const uint8_t pid = entries[rank].id;
        const uint32_t gmPlayer = Memory::Read32(gmPlayers + static_cast<uint32_t>(pid) * 4u);
        if (gmPlayer) {
            Memory::Write8(gmPlayer + 32u, static_cast<uint8_t>(rank + 1u));
        }
        Memory::Write8(posTable + rank, pid);
    }
}
REGISTER_NATIVE_WRAPPER_AS(0x8053F4A0, GMDataOnlineVS_HandlePositionTracking_Expanded,
                           "GMDataOnlineVS_HandlePositionTracking_Expanded");

// ---------------------------------------------------------------------------
// 6. GMDataOnlineVS::ReadRemotePacket (PAL 0x8053E47C / USA 0x80538F40)
// ---------------------------------------------------------------------------
// Reads aid = RKNetController->playerToAid[playerIdx] (+142368) without checking
// whether aid == 0xFF (disconnected or CPU filler slot). Indexing packet buffers
// with aid = 0xFF reads out of bounds and dereferences NULL. Guard before calling.
extern "C" void GMDataOnlineVS_ReadRemotePacket_Expanded(CpuContext* ctx) {
    const auto& layout = MkwGuestLayout::kLayout;
    const uint32_t playerIdx = ctx->gpr[4];
    if (playerIdx >= layout.playerCapacity) {
        return;
    }
    const uint32_t rknet = Memory::Read32(MKW_GADDR(809C20D8));
    if (!rknet) {
        return;
    }
    const uint8_t aid = Memory::Read8(rknet + 142368u + playerIdx);
    if (aid >= layout.playerCapacity) {
        return;
    }
    MKW_GUEST_NEXT(8053E47C)(ctx);
}
REGISTER_NATIVE_WRAPPER_AS(0x8053E47C, GMDataOnlineVS_ReadRemotePacket_Expanded,
                           "GMDataOnlineVS_ReadRemotePacket_Expanded");

// ---------------------------------------------------------------------------
// 7. Pages::WiFiVSResults::Fill (PAL 0x806465E0 / USA 0x8064D988)
// ---------------------------------------------------------------------------
// Holds only 12 CtrlRaceResult controls at this + 1156 (stride 372). In >12p
// Online VS / Team VS, writing slot = finishPos - 1 (or +6 for Team 1) without
// bounding slot < 12 overflows 43 KB past the page into the heap. Blank all 12
// rows first, then populate only rows whose target slot fits in 0..11 (or 0..5
// per team in Team VS).
extern "C" void WiFiVSResults_Fill_Expanded(CpuContext* ctx) {
    const auto& layout = MkwGuestLayout::kLayout;
    const uint32_t page = ctx->gpr[3];
    if (layout.playerCapacity <= 12 || !page) {
        MKW_GUEST_NEXT(806465E0)(ctx);
        return;
    }
    const uint8_t playerCount = Memory::Read8(page + 5622u);
    if (playerCount <= 12u) {
        MKW_GUEST_NEXT(806465E0)(ctx);
        return;
    }

    const uint8_t isTeam = Memory::Read8(page + 5620u);
    const uint8_t localCount = Memory::Read8(page + 5621u);
    const uint32_t isMultiLocal = (localCount >= 2u) ? 1u : 0u;

    for (uint32_t slot = 0u; slot < 12u; ++slot) {
        ctx->gpr[3] = page + 1156u + slot * 372u;
        ctx->gpr[4] = 11u;
        ctx->gpr[5] = isTeam;
        ctx->gpr[6] = isMultiLocal;
        InvokeIndirectCpu(MKW_GADDR(80645820), ctx);
    }

    const uint32_t racedata = Memory::Read32(MKW_GADDR(809BD728));
    if (!racedata) {
        return;
    }
    const uint32_t limit = std::min<uint32_t>(playerCount, layout.playerCapacity);
    for (uint32_t p = 0u; p < limit; ++p) {
        const uint32_t pBase = racedata + p * 240u;
        const uint8_t finishPos = Memory::Read8(pBase + 31160u);
        if (finishPos == 0u) {
            continue;
        }
        uint32_t slot = static_cast<uint32_t>(finishPos - 1u);
        if (isTeam) {
            if (slot >= 6u) {
                continue;
            }
            const uint32_t team = Memory::Read32(pBase + 244u);
            if (team == 1u) {
                slot += 6u;
            }
        } else {
            if (slot >= 12u) {
                continue;
            }
        }
        ctx->gpr[3] = page + 1156u + slot * 372u;
        ctx->gpr[4] = p;
        ctx->gpr[5] = isTeam;
        ctx->gpr[6] = isMultiLocal;
        InvokeIndirectCpu(MKW_GADDR(80645820), ctx);
    }
}
REGISTER_NATIVE_WRAPPER_AS(0x806465E0, WiFiVSResults_Fill_Expanded,
                           "WiFiVSResults_Fill_Expanded");

// ---------------------------------------------------------------------------
// 8. Kart::Manager::Update (PAL 0x8058FFE8 / USA 0x805897C4) &
//    Kart::Collision::CheckKartCollision (PAL 0x8056F7F0 / USA 0x8056A9A0)
// ---------------------------------------------------------------------------
// Clear the host-side 128x128 collision-pair bitset once per frame before
// Kart::Manager::Update, and deduplicate colliding kart pairs for players 16..127
// where vanilla's 16-bit checkedKartBitmask (+0x84) overflows to 0.
extern "C" void KartManager_Update_Expanded(CpuContext* ctx) {
    if (MkwGuestLayout::kLayout.playerCapacity > 16) {
        for (auto& row : gCheckedKartPairs) {
            row[0] = 0u;
            row[1] = 0u;
        }
    }
    MKW_GUEST_NEXT(8058FFE8)(ctx);
}
REGISTER_NATIVE_WRAPPER_AS(0x8058FFE8, KartManager_Update_Expanded,
                           "KartManager_Update_Expanded");

extern "C" void KartCollision_CheckKartCollision_Expanded(CpuContext* ctx) {
    if (MkwGuestLayout::kLayout.playerCapacity <= 16) {
        MKW_GUEST_NEXT(8056F7F0)(ctx);
        return;
    }
    const uint32_t selfColl = ctx->gpr[3];
    const uint32_t otherPlayer = ctx->gpr[4];
    if (selfColl && otherPlayer) {
        const uint32_t selfPtrs = Memory::Read32(selfColl + 0u);
        const uint32_t otherPtrs = Memory::Read32(otherPlayer + 0u);
        if (selfPtrs && otherPtrs) {
            const uint32_t selfVals = Memory::Read32(selfPtrs + 0u);
            const uint32_t otherVals = Memory::Read32(otherPtrs + 0u);
            if (selfVals && otherVals) {
                const uint8_t idxA = Memory::Read8(selfVals + 16u);
                const uint8_t idxB = Memory::Read8(otherVals + 16u);
                if (idxA != idxB && TestAndSetKartPair(idxA, idxB)) {
                    return;
                }
            }
        }
    }
    MKW_GUEST_NEXT(8056F7F0)(ctx);
}
REGISTER_NATIVE_WRAPPER_AS(0x8056F7F0, KartCollision_CheckKartCollision_Expanded,
                           "KartCollision_CheckKartCollision_Expanded");
