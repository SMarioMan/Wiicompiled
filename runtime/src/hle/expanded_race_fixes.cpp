#include "abi_bridge.h"
#include "guest_abi/guest_region.h"
#include "guest_function_imports.h"
#include "memory.h"
#include "mkw_guest_layout.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstdlib>
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

// ---------------------------------------------------------------------------
// Runtime-toggleable high-player-count visual culling optimizations (#1-#4).
// All OFF by default; enabled via MKW_HIGH_COUNT_OPT=1 or per-feature env vars:
//   MKW_OPT_KART_CULL=<N>         (#1: nearest N karts drawn; default 20, 0=off)
//   MKW_OPT_EFFECTS_CULL=<0|1>    (#2: cull distant/off-screen effects & shadows)
//   MKW_OPT_NAMEBALLOON_CULL=<N>  (#3: nearest N nameplates; default 12, 0=off)
//   MKW_OPT_SKELETAL_CULL=<0|1>   (#4: skip transforms/bones for culled karts)
// Zero physics or gameplay changes; 100% uniform parity across all instances;
// only active when playerCount > 12.
// ---------------------------------------------------------------------------
struct HighCountOptConfig {
    uint32_t kartDrawCap = 0;
    bool cullEffectsAndShadows = false;
    uint32_t nameBalloonCap = 0;
    bool cullSkeletal = false;

    bool AnyEnabled() const noexcept {
        return kartDrawCap > 0 || cullEffectsAndShadows || nameBalloonCap > 0 || cullSkeletal;
    }
};

const HighCountOptConfig& GetHighCountOptConfig() noexcept {
    static const HighCountOptConfig cfg = []() {
        HighCountOptConfig c{};
        auto readEnvInt = [](const char* name, int fallback) -> int {
            const char* val = std::getenv(name);
            if (!val || !*val) {
                return fallback;
            }
            return std::atoi(val);
        };
        const bool master = readEnvInt("MKW_HIGH_COUNT_OPT", 0) != 0;
        const int kartCap = readEnvInt("MKW_OPT_KART_CULL", master ? 20 : 0);
        c.kartDrawCap = kartCap > 0 ? static_cast<uint32_t>(kartCap) : 0u;
        c.cullEffectsAndShadows = readEnvInt("MKW_OPT_EFFECTS_CULL", master ? 1 : 0) != 0;
        const int nameCap = readEnvInt("MKW_OPT_NAMEBALLOON_CULL", master ? 12 : 0);
        c.nameBalloonCap = nameCap > 0 ? static_cast<uint32_t>(nameCap) : 0u;
        c.cullSkeletal = readEnvInt("MKW_OPT_SKELETAL_CULL", master ? 1 : 0) != 0;
        return c;
    }();
    return cfg;
}

uint32_t gLastCullKartMgr = 0u;
bool gHighCountCullActive = false;
std::array<bool, 128> gKartDrawVisible{};
std::array<bool, 128> gPrevKartDrawVisible{};
std::array<bool, 128> gKartEffectsVisible{};
std::array<bool, 128> gPrevShadowVisible{};
std::array<bool, 128> gPrevEffectsCulled{};
std::array<bool, 128> gKartNameVisible{};

inline uint8_t ReadKartLinkPlayerIdx(uint32_t linkAddr) {
    if (!linkAddr) {
        return 255u;
    }
    const uint32_t ptrs = Memory::Read32(linkAddr);
    if (!ptrs) {
        return 255u;
    }
    const uint32_t vals = Memory::Read32(ptrs);
    if (!vals) {
        return 255u;
    }
    return Memory::Read8(vals + 0x10u);
}

void ResetHighCountCullState(uint32_t kartMgr) {
    gLastCullKartMgr = kartMgr;
    gHighCountCullActive = false;
    gKartDrawVisible.fill(true);
    gPrevKartDrawVisible.fill(true);
    gKartEffectsVisible.fill(true);
    gPrevShadowVisible.fill(true);
    gPrevEffectsCulled.fill(false);
    gKartNameVisible.fill(true);
}

void UpdateHighCountCullClassification(uint32_t kartMgr) {
    const auto& cfg = GetHighCountOptConfig();
    if (!cfg.AnyEnabled() || !kartMgr) {
        gHighCountCullActive = false;
        return;
    }
    if (kartMgr != gLastCullKartMgr) {
        ResetHighCountCullState(kartMgr);
    }
    const uint32_t playersArr = Memory::Read32(kartMgr + 0x20u);
    const uint8_t playerCount = Memory::Read8(kartMgr + 0x24u);
    if (!playersArr || playerCount <= 12u || playerCount > 128u) {
        gHighCountCullActive = false;
        return;
    }
    const uint32_t camMgr = Memory::Read32(MKW_GADDR(809C19B8));
    if (!camMgr) {
        gHighCountCullActive = false;
        return;
    }
    const uint32_t activeCams = Memory::Read32(camMgr + 0x18u);
    const uint32_t cam = activeCams ? Memory::Read32(activeCams) : 0u;
    if (!cam) {
        gHighCountCullActive = false;
        return;
    }
    const uint8_t focusedIdx = Memory::Read8(camMgr + 0x27u);
    const float camX = ReadGuestFloat32(cam + 0x64u);
    const float camY = ReadGuestFloat32(cam + 0x68u);
    const float camZ = ReadGuestFloat32(cam + 0x6Cu);
    float fwdX = ReadGuestFloat32(cam + 0x70u) - camX;
    float fwdY = ReadGuestFloat32(cam + 0x74u) - camY;
    float fwdZ = ReadGuestFloat32(cam + 0x78u) - camZ;
    const float fwdLenSq = fwdX * fwdX + fwdY * fwdY + fwdZ * fwdZ;
    const bool hasFwd = fwdLenSq > 1e-6f;
    if (hasFwd) {
        const float invLen = 1.0f / std::sqrt(fwdLenSq);
        fwdX *= invLen;
        fwdY *= invLen;
        fwdZ *= invLen;
    }

    struct RankEntry {
        uint8_t idx;
        bool behind;
        float distSq;
        float score;
    };
    std::array<RankEntry, 128> ranked{};
    constexpr float kNearKeepDistSq = 1500.0f * 1500.0f;   // ~15m: always keep even if behind
    constexpr float kBehindCheckDistSq = 2000.0f * 2000.0f; // ~20m: frustum-cull behind camera
    constexpr float kEffectsDistSq = 4000.0f * 4000.0f;    // ~40m: effects/shadow cutoff

    for (uint8_t i = 0; i < playerCount; ++i) {
        RankEntry e{i, false, 0.0f, 0.0f};
        const uint32_t player = Memory::Read32(playersArr + static_cast<uint32_t>(i) * 4u);
        const uint32_t ptrs = player ? Memory::Read32(player) : 0u;
        const uint32_t body = ptrs ? Memory::Read32(ptrs + 0x08u) : 0u;
        const uint32_t bodySub = body ? Memory::Read32(body + 0x90u) : 0u;
        const uint32_t phys = bodySub ? Memory::Read32(bodySub + 0x04u) : 0u;
        if (i == focusedIdx) {
            e.distSq = 0.0f;
            e.score = -1.0f;
        } else if (phys) {
            const float dx = ReadGuestFloat32(phys + 0x68u) - camX;
            const float dy = ReadGuestFloat32(phys + 0x6Cu) - camY;
            const float dz = ReadGuestFloat32(phys + 0x70u) - camZ;
            const float dSq = dx * dx + dy * dy + dz * dz;
            e.distSq = dSq;
            if (hasFwd && dSq > kBehindCheckDistSq) {
                const float dotFwd = dx * fwdX + dy * fwdY + dz * fwdZ;
                if (dotFwd < -0.25f * std::sqrt(dSq)) {
                    e.behind = true;
                }
            }
            e.score = e.behind ? (dSq + 1e18f) : dSq;
        }
        ranked[i] = e;
    }

    std::sort(ranked.begin(), ranked.begin() + playerCount,
              [](const RankEntry& a, const RankEntry& b) { return a.score < b.score; });

    for (uint8_t rank = 0; rank < playerCount; ++rank) {
        const auto& e = ranked[rank];
        const bool isFocused = (e.idx == focusedIdx);
        const bool drawVis = (cfg.kartDrawCap == 0u)
                                 ? true
                                 : (isFocused || e.distSq < kNearKeepDistSq ||
                                    (!e.behind && rank < cfg.kartDrawCap));
        gKartDrawVisible[e.idx] = drawVis;
        gKartEffectsVisible[e.idx] = !cfg.cullEffectsAndShadows
                                         ? true
                                         : (isFocused || (drawVis && e.distSq < kEffectsDistSq));
        gKartNameVisible[e.idx] = (cfg.nameBalloonCap == 0u)
                                      ? true
                                      : (drawVis && (isFocused || rank < cfg.nameBalloonCap));
    }
    gHighCountCullActive = true;
}

void ApplyHighCountCullVisibility(CpuContext* ctx, uint32_t kartMgr) {
    if (!gHighCountCullActive || !kartMgr) {
        return;
    }
    const auto& cfg = GetHighCountOptConfig();
    const uint32_t playersArr = Memory::Read32(kartMgr + 0x20u);
    const uint8_t playerCount = Memory::Read8(kartMgr + 0x24u);
    if (!playersArr || playerCount <= 12u || playerCount > 128u) {
        return;
    }
    for (uint8_t i = 0; i < playerCount; ++i) {
        const bool drawChanged = (cfg.kartDrawCap > 0u) && (gKartDrawVisible[i] != gPrevKartDrawVisible[i]);
        const bool shadowChanged = cfg.cullEffectsAndShadows && (gKartEffectsVisible[i] != gPrevShadowVisible[i]);
        if (!drawChanged && !shadowChanged) {
            continue;
        }
        const uint32_t player = Memory::Read32(playersArr + static_cast<uint32_t>(i) * 4u);
        const uint32_t ptrs = player ? Memory::Read32(player) : 0u;
        if (!ptrs) {
            continue;
        }
        if (drawChanged) {
            const uint32_t modelsVis = Memory::Read32(ptrs + 0x58u);
            if (modelsVis) {
                CpuContext sub = *ctx;
                sub.gpr[3] = modelsVis;
                if (!gKartDrawVisible[i]) {
                    const uint8_t orig = Memory::Read8(modelsVis + 0x12u);
                    Memory::Write8(modelsVis + 0x12u, 0u);
                    MKW_GUEST_NEXT(8056A470)(&sub);
                    Memory::Write8(modelsVis + 0x12u, orig);
                } else {
                    MKW_GUEST_NEXT(8056A470)(&sub);
                }
            }
            gPrevKartDrawVisible[i] = gKartDrawVisible[i];
        }
        if (shadowChanged) {
            const uint32_t shadowMdl = Memory::Read32(ptrs + 0x20u);
            if (shadowMdl) {
                CpuContext sub = *ctx;
                sub.gpr[3] = shadowMdl;
                sub.gpr[4] = gKartEffectsVisible[i] ? 1u : 0u;
                MKW_GUEST_FUNC(8055CB08)(&sub);
            }
            gPrevShadowVisible[i] = gKartEffectsVisible[i];
        }
    }
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
    const uint32_t kartMgr = ctx->gpr[3];
    UpdateHighCountCullClassification(kartMgr);
    MKW_GUEST_NEXT(8058FFE8)(ctx);
    ApplyHighCountCullVisibility(ctx, kartMgr);
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

// ---------------------------------------------------------------------------
// 9. EntityManager::Update (PAL 0x807859B0 / USA 0x80781580) &
//    EntityManager::FindEntitiesCloseToEntity (PAL 0x807868C0 / USA 0x80782490)
// ---------------------------------------------------------------------------
// Native C++ equivalents of the hot 1D sweep-and-prune spatial broadphase
// loops. Bit-identical to the PowerPC originals (same tie-breaking, same
// insertion sort order, same result order and 256-entry cap) while avoiding
// per-iteration PPC register/CR/rotl/layout-audit emulation overhead.
namespace {
inline float ReadGuestFloat(uint32_t addr) {
    const uint32_t bits = Memory::Read32(addr);
    float v;
    std::memcpy(&v, &bits, sizeof(v));
    return v;
}
inline void WriteGuestFloat(uint32_t addr, float v) {
    uint32_t bits;
    std::memcpy(&bits, &v, sizeof(bits));
    Memory::Write32(addr, bits);
}
} // namespace

extern "C" void EntityManager_Update_Native(CpuContext* ctx) {
    const uint32_t mgr = ctx->gpr[3];
    Memory::Write32(mgr + 1064u, 256u);
    Memory::Write32(mgr + 1068u, 256u);
    Memory::Write32(mgr + 1072u, 256u);
    Memory::Write32(mgr + 1076u, 256u);
    Memory::Write32(mgr + 1080u, 0u);
    Memory::Write32(mgr + 1084u, 0u);
    WriteGuestFloat(mgr + 1100u, 0.0f);
    Memory::Write32(mgr + 1104u, 0u);

    const uint32_t zMaxArr = Memory::Read32(mgr + 16u);
    const uint32_t zMinArr = Memory::Read32(mgr + 20u);
    const uint32_t entities = Memory::Read32(mgr + 24u);
    const int32_t count = static_cast<int32_t>(Memory::Read32(mgr + 1056u));

    int32_t activeSeen = 0;
    uint32_t entOff = 0u;
    while (activeSeen < count) {
        const uint32_t ent = entities + entOff;
        const uint32_t flags = Memory::Read32(ent + 12u);
        if ((flags & 1024u) != 0u) {
            ++activeSeen;
            if ((flags & 2304u) != 0u) {
                const uint32_t posPtr = Memory::Read32(ent);
                const float radius = ReadGuestFloat(ent + 8u);
                const float x = ReadGuestFloat(posPtr);
                const float z = ReadGuestFloat(posPtr + 8u);
                WriteGuestFloat(ent + 24u, x + radius);
                WriteGuestFloat(ent + 28u, x - radius);
                const int32_t zMaxIdx = static_cast<int16_t>(Memory::Read16(ent + 20u));
                WriteGuestFloat(zMaxArr + static_cast<uint32_t>(zMaxIdx) * 8u, z + radius);
                const int32_t zMinIdx = static_cast<int16_t>(Memory::Read16(ent + 22u));
                WriteGuestFloat(zMinArr + static_cast<uint32_t>(zMinIdx) * 8u, z - radius);
                Memory::Write32(ent + 12u, flags & ~2048u);
            }
        }
        entOff += 32u;
    }

    for (int32_t i = 1; i < count; ++i) {
        for (int32_t j = i; j >= 1; --j) {
            const uint32_t prevAddr = zMaxArr + static_cast<uint32_t>(j - 1) * 8u;
            const uint32_t currAddr = zMaxArr + static_cast<uint32_t>(j) * 8u;
            const float prevVal = ReadGuestFloat(prevAddr);
            const float currVal = ReadGuestFloat(currAddr);
            if (!(prevVal > currVal)) {
                break;
            }
            const uint8_t currB4 = Memory::Read8(currAddr + 4u);
            const uint8_t currB5 = Memory::Read8(currAddr + 5u);
            const uint8_t prevB4 = Memory::Read8(prevAddr + 4u);
            const uint8_t prevB5 = Memory::Read8(prevAddr + 5u);
            WriteGuestFloat(currAddr, prevVal);
            Memory::Write8(currAddr + 4u, prevB4);
            Memory::Write8(currAddr + 5u, prevB5);
            WriteGuestFloat(prevAddr, currVal);
            Memory::Write8(prevAddr + 4u, currB4);
            Memory::Write8(prevAddr + 5u, currB5);

            const uint32_t zMinA = zMinArr + static_cast<uint32_t>(prevB4) * 8u;
            const uint32_t zMinB = zMinArr + static_cast<uint32_t>(currB4) * 8u;
            Memory::Write8(zMinA + 4u, static_cast<uint8_t>(Memory::Read8(zMinA + 4u) + 1u));
            Memory::Write8(zMinB + 4u, static_cast<uint8_t>(Memory::Read8(zMinB + 4u) - 1u));

            const uint32_t entA = entities + static_cast<uint32_t>(Memory::Read8(zMinA + 5u)) * 32u;
            const uint32_t entB = entities + static_cast<uint32_t>(Memory::Read8(zMinB + 5u)) * 32u;
            Memory::Write16(entA + 20u,
                            static_cast<uint16_t>(static_cast<int16_t>(Memory::Read16(entA + 20u)) + 1));
            Memory::Write16(entB + 20u,
                            static_cast<uint16_t>(static_cast<int16_t>(Memory::Read16(entB + 20u)) - 1));

            if (prevB5 == currB4) {
                uint8_t k = prevB5;
                do {
                    ++k;
                } while (static_cast<int32_t>(Memory::Read8(zMinArr + static_cast<uint32_t>(k) * 8u + 4u)) < j);
                Memory::Write8(currAddr + 5u, k);
            }
            if (currB5 > prevB4) {
                Memory::Write8(prevAddr + 5u, prevB4);
            }
        }
    }

    for (int32_t i = 1; i < count; ++i) {
        for (int32_t j = i; j >= 1; --j) {
            const uint32_t prevAddr = zMinArr + static_cast<uint32_t>(j - 1) * 8u;
            const uint32_t currAddr = zMinArr + static_cast<uint32_t>(j) * 8u;
            const float prevVal = ReadGuestFloat(prevAddr);
            const float currVal = ReadGuestFloat(currAddr);
            if (!(prevVal > currVal)) {
                break;
            }
            const uint8_t currB4 = Memory::Read8(currAddr + 4u);
            const uint8_t currB5 = Memory::Read8(currAddr + 5u);
            const uint8_t prevB4 = Memory::Read8(prevAddr + 4u);
            const uint8_t prevB5 = Memory::Read8(prevAddr + 5u);
            WriteGuestFloat(currAddr, prevVal);
            Memory::Write8(currAddr + 4u, prevB4);
            Memory::Write8(currAddr + 5u, prevB5);
            WriteGuestFloat(prevAddr, currVal);
            Memory::Write8(prevAddr + 4u, currB4);
            Memory::Write8(prevAddr + 5u, currB5);

            const uint32_t zMaxA = zMaxArr + static_cast<uint32_t>(prevB4) * 8u;
            const uint32_t zMaxB = zMaxArr + static_cast<uint32_t>(currB4) * 8u;
            Memory::Write8(zMaxA + 4u, static_cast<uint8_t>(Memory::Read8(zMaxA + 4u) + 1u));
            Memory::Write8(zMaxB + 4u, static_cast<uint8_t>(Memory::Read8(zMaxB + 4u) - 1u));

            const uint32_t entA = entities + static_cast<uint32_t>(prevB5) * 32u;
            const uint32_t entB = entities + static_cast<uint32_t>(currB5) * 32u;
            Memory::Write16(entA + 22u,
                            static_cast<uint16_t>(static_cast<int16_t>(Memory::Read16(entA + 22u)) + 1));
            Memory::Write16(entB + 22u,
                            static_cast<uint16_t>(static_cast<int16_t>(Memory::Read16(entB + 22u)) - 1));

            if (prevB4 > currB4) {
                for (int32_t k = prevB4; k > static_cast<int32_t>(currB4); --k) {
                    const uint32_t slot = zMaxArr + static_cast<uint32_t>(k) * 8u;
                    const uint8_t h = Memory::Read8(slot + 5u);
                    if (static_cast<int32_t>(h) != j - 1) {
                        break;
                    }
                    Memory::Write8(slot + 5u, static_cast<uint8_t>(h + 1u));
                }
            } else {
                for (int32_t k = currB4; k > static_cast<int32_t>(prevB4); --k) {
                    const uint32_t slot = zMaxArr + static_cast<uint32_t>(k) * 8u;
                    const uint8_t h = Memory::Read8(slot + 5u);
                    if (static_cast<int32_t>(h) != j) {
                        break;
                    }
                    Memory::Write8(slot + 5u, static_cast<uint8_t>(h - 1u));
                }
            }
        }
    }
}
REGISTER_NATIVE_REPLACEMENT_AS(0x807859B0, EntityManager_Update_Native,
                               "EntityManager_Update_Native");

extern "C" void EntityManager_FindEntitiesCloseToEntity_Native(CpuContext* ctx) {
    const uint32_t mgr = ctx->gpr[3];
    const uint32_t entity = ctx->gpr[4];
    const uint32_t mask = ctx->gpr[5];

    if ((Memory::Read32(entity + 12u) & 1024u) == 0u) {
        return;
    }

    const int32_t zMinIdx = static_cast<int16_t>(Memory::Read16(entity + 22u));
    int32_t zMaxIdx = static_cast<int16_t>(Memory::Read16(entity + 20u));
    const uint32_t posPtr = Memory::Read32(entity);
    const uint32_t zMaxArr = Memory::Read32(mgr + 16u);
    const uint32_t zMinArr = Memory::Read32(mgr + 20u);
    const int32_t lastIdx = static_cast<int32_t>(Memory::Read32(mgr + 1056u)) - 1;

    const float radius = ReadGuestFloat(entity + 4u);
    const float posZ = ReadGuestFloat(posPtr + 8u);
    const float posX = ReadGuestFloat(posPtr);
    const float queryZMax = ReadGuestFloat(zMaxArr + static_cast<uint32_t>(zMaxIdx) * 8u);
    const float zPlusR = posZ + radius;
    const float zMinusR = posZ - radius;
    const float queryZMin = ReadGuestFloat(zMinArr + static_cast<uint32_t>(zMinIdx) * 8u);
    const float xPlusR = posX + radius;
    const float queryXMax = ReadGuestFloat(entity + 24u);
    const float queryXMin = ReadGuestFloat(entity + 28u);
    const float xMinusR = posX - radius;

    Memory::Write32(mgr + 1080u, 0u);
    Memory::Write32(mgr + 1084u, entity);
    WriteGuestFloat(mgr + 1100u, 0.0f);
    Memory::Write32(mgr + 1104u, mask);

    while (zMaxIdx > 7 && ReadGuestFloat(zMaxArr + static_cast<uint32_t>(zMaxIdx - 8) * 8u) >= queryZMin) {
        zMaxIdx -= 8;
    }
    while (zMaxIdx > 0 && ReadGuestFloat(zMaxArr + static_cast<uint32_t>(zMaxIdx - 1) * 8u) >= queryZMin) {
        --zMaxIdx;
    }

    int32_t scanIdx = zMinIdx;
    const int32_t lastMinus7 = lastIdx - 7;
    while (scanIdx < lastMinus7 && ReadGuestFloat(zMinArr + static_cast<uint32_t>(scanIdx + 8) * 8u) <= queryZMax) {
        scanIdx += 8;
    }
    while (scanIdx < lastIdx && ReadGuestFloat(zMinArr + static_cast<uint32_t>(scanIdx + 1) * 8u) <= queryZMax) {
        ++scanIdx;
    }

    const int32_t minScan = static_cast<int32_t>(Memory::Read8(zMaxArr + static_cast<uint32_t>(zMaxIdx) * 8u + 5u));
    if (scanIdx < minScan) {
        return;
    }

    const uint32_t entities = Memory::Read32(mgr + 24u);
    const uint32_t outArr = Memory::Read32(mgr + 28u);
    uint32_t foundCount = 0u;

    for (int32_t k = scanIdx; k >= minScan; --k) {
        const uint32_t zMinEntry = zMinArr + static_cast<uint32_t>(k) * 8u;
        if (static_cast<int32_t>(Memory::Read8(zMinEntry + 4u)) < zMaxIdx || k == zMinIdx) {
            if (k == 0) {
                break;
            }
            continue;
        }
        const uint32_t cand = entities + static_cast<uint32_t>(Memory::Read8(zMinEntry + 5u)) * 32u;
        if (ReadGuestFloat(cand + 24u) < queryXMin || ReadGuestFloat(cand + 28u) > queryXMax) {
            continue;
        }
        const uint32_t candFlags = Memory::Read32(cand + 12u);
        if ((candFlags & mask) == 0u || (candFlags & 512u) != 0u) {
            continue;
        }
        const uint32_t candPos = Memory::Read32(cand);
        const float candR = ReadGuestFloat(cand + 4u);
        const float candZ = ReadGuestFloat(candPos + 8u);
        if (candZ + candR < zMinusR || candZ - candR > zPlusR) {
            continue;
        }
        const float candX = ReadGuestFloat(candPos);
        if (candX + candR < xMinusR || candX - candR > xPlusR) {
            continue;
        }
        Memory::Write32(outArr + foundCount * 4u, cand);
        ++foundCount;
        Memory::Write32(mgr + 1080u, foundCount);
        if (foundCount == 256u || k == 0) {
            break;
        }
    }
}
REGISTER_NATIVE_REPLACEMENT_AS(0x807868C0, EntityManager_FindEntitiesCloseToEntity_Native,
                               "EntityManager_FindEntitiesCloseToEntity_Native");

// ---------------------------------------------------------------------------
// 10. Runtime-flagged high-player-count visual culling hooks (#1-#4)
// ---------------------------------------------------------------------------
extern "C" void KartModelsVisibility_Update_HighCountOpt(CpuContext* ctx) {
    if (gHighCountCullActive && GetHighCountOptConfig().kartDrawCap > 0u) {
        const uint32_t modelsVis = ctx->gpr[3];
        const uint8_t playerIdx = ReadKartLinkPlayerIdx(modelsVis);
        if (playerIdx < 128u && !gKartDrawVisible[playerIdx]) {
            const uint8_t orig = Memory::Read8(modelsVis + 0x12u);
            Memory::Write8(modelsVis + 0x12u, 0u);
            MKW_GUEST_NEXT(8056A470)(ctx);
            Memory::Write8(modelsVis + 0x12u, orig);
            return;
        }
    }
    MKW_GUEST_NEXT(8056A470)(ctx);
}
REGISTER_NATIVE_WRAPPER_AS(0x8056A470, KartModelsVisibility_Update_HighCountOpt,
                           "KartModelsVisibility_Update_HighCountOpt");

extern "C" void KartSub_UpdateTransforms_HighCountOpt(CpuContext* ctx) {
    if (gHighCountCullActive && GetHighCountOptConfig().cullSkeletal) {
        const uint8_t playerIdx = ReadKartLinkPlayerIdx(ctx->gpr[3]);
        if (playerIdx < 128u && !gKartDrawVisible[playerIdx]) {
            return;
        }
    }
    MKW_GUEST_NEXT(8059783C)(ctx);
}
REGISTER_NATIVE_WRAPPER_AS(0x8059783C, KartSub_UpdateTransforms_HighCountOpt,
                           "KartSub_UpdateTransforms_HighCountOpt");

extern "C" void DriverController_Update_HighCountOpt(CpuContext* ctx) {
    if (gHighCountCullActive && GetHighCountOptConfig().cullSkeletal) {
        const uint8_t playerIdx = ReadKartLinkPlayerIdx(ctx->gpr[3]);
        if (playerIdx < 128u && !gKartDrawVisible[playerIdx]) {
            return;
        }
    }
    MKW_GUEST_NEXT(807CB360)(ctx);
}
REGISTER_NATIVE_WRAPPER_AS(0x807CB360, DriverController_Update_HighCountOpt,
                           "DriverController_Update_HighCountOpt");

extern "C" void EffectsPlayer_Update_HighCountOpt(CpuContext* ctx) {
    if (gHighCountCullActive && GetHighCountOptConfig().cullEffectsAndShadows) {
        const uint32_t effPlayer = ctx->gpr[3];
        const uint8_t playerIdx = effPlayer ? Memory::Read8(effPlayer + 0x12Eu) : 255u;
        if (playerIdx < 128u) {
            if (!gKartEffectsVisible[playerIdx]) {
                if (!gPrevEffectsCulled[playerIdx]) {
                    gPrevEffectsCulled[playerIdx] = true;
                    CpuContext sub = *ctx;
                    sub.gpr[3] = effPlayer;
                    sub.gpr[4] = 1u;
                    MKW_GUEST_FUNC(806958F4)(&sub);
                }
                return;
            }
            gPrevEffectsCulled[playerIdx] = false;
        }
    }
    MKW_GUEST_NEXT(80693D84)(ctx);
}
REGISTER_NATIVE_WRAPPER_AS(0x80693D84, EffectsPlayer_Update_HighCountOpt,
                           "EffectsPlayer_Update_HighCountOpt");

extern "C" void CtrlRaceNameBalloon_OnUpdate_HighCountOpt(CpuContext* ctx) {
    if (gHighCountCullActive && GetHighCountOptConfig().nameBalloonCap > 0u) {
        const uint32_t ctrl = ctx->gpr[3];
        const int32_t playerId = ctrl ? static_cast<int32_t>(Memory::Read32(ctrl + 0x178u)) : -1;
        if (playerId >= 0 && playerId < 128 && !gKartNameVisible[static_cast<size_t>(playerId)]) {
            Memory::Write8(ctrl + 0x80u, 1u);
            return;
        }
    }
    MKW_GUEST_NEXT(807F06E8)(ctx);
}
REGISTER_NATIVE_WRAPPER_AS(0x807F06E8, CtrlRaceNameBalloon_OnUpdate_HighCountOpt,
                           "CtrlRaceNameBalloon_OnUpdate_HighCountOpt");
