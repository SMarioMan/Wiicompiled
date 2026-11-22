#pragma once
#include <cstdint>

// Supplied by per-game glue, so a precompiled runtime can serve any guest layout.
namespace MkwGuestLayout {
struct Layout {
    uint32_t playerCapacity;
    uint32_t playerStride;
    uint32_t scenarioPlayers;
    uint32_t scenarioSettings;
    uint32_t scenarioMission;
    uint32_t scenarioGhost;
    uint32_t scenarioSize;
    uint32_t racedataRace;
    uint32_t racedataMenu;
    uint32_t racedataAwards;
    uint32_t racedataGhosts;
    uint32_t racedataSize;
    uint32_t inputRealHolders;
    uint32_t inputGCNControllers;
    uint32_t friendRoomControls;
    uint32_t friendRoomControlStride;
    uint32_t friendRoomPlayerIds;
    uint32_t friendRoomPlayerCount;
};
extern const Layout kLayout;

extern const uint32_t kDwcConnectionReserve;

// Separate export keeps the established Layout ABI intact for native glue.
struct BattleTargetLayout {
    uint32_t capacity, targets, states;
};
extern const BattleTargetLayout kBattleTargets;

struct TeamResultLayout {
    uint32_t players, count, stride;
    uint32_t resultTeamPoint, currentScore, teamScore;
};
extern const TeamResultLayout kTeamResults;
}
