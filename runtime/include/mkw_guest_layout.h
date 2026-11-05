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
};
extern const Layout kLayout;
}
