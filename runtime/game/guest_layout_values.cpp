#include "mkw_guest_layout.h"

#ifdef MKW_TRANSLATED_GUEST_LAYOUT_HEADER
#include MKW_TRANSLATED_GUEST_LAYOUT_HEADER
#endif

#ifndef MKW_TRANSLATED_PLAYER_CAPACITY
#define MKW_TRANSLATED_PLAYER_CAPACITY 12
#endif

namespace MkwGuestLayout {
#if defined(MKW_GUEST_LAYOUT_EXPLICIT) && MKW_GUEST_LAYOUT_EXPLICIT
static_assert(MKW_TRANSLATED_PLAYER_CAPACITY == MKW_LAYOUT_PLAYER_CAPACITY,
              "Native and translated player capacities must agree");
const Layout kLayout{
    MKW_LAYOUT_PLAYER_CAPACITY, MKW_LAYOUT_SCENARIO_PLAYERS_STRIDE,
    MKW_LAYOUT_SCENARIO_PLAYERS_OFFSET, MKW_LAYOUT_SCENARIO_SETTINGS_OFFSET,
    MKW_LAYOUT_SCENARIO_MISSION_OFFSET, MKW_LAYOUT_SCENARIO_GHOST_OFFSET,
    MKW_LAYOUT_SCENARIO_SIZE, MKW_LAYOUT_RACEDATA_RACE_OFFSET,
    MKW_LAYOUT_RACEDATA_MENU_OFFSET, MKW_LAYOUT_RACEDATA_AWARDS_OFFSET,
    MKW_LAYOUT_RACEDATA_GHOSTS_OFFSET, MKW_LAYOUT_RACEDATA_SIZE,
    MKW_LAYOUT_INPUTMANAGER_REALHOLDERS_OFFSET, MKW_LAYOUT_INPUTMANAGER_GCNCONTROLLERS_OFFSET};
#else
// Legacy, unmodified guest ABI. Expanded builds must supply the verified header.
static_assert(MKW_TRANSLATED_PLAYER_CAPACITY == 12,
              "Expanded guest layouts require generated native constants");
constexpr uint32_t players = 12, playerSize = 0xf0, scenarioHeader = 8;
constexpr uint32_t settings = scenarioHeader + players * playerSize;
constexpr uint32_t mission = settings + 0x34, ghost = mission + 0x70;
constexpr uint32_t scenarioSize = ghost + 4, race = 0x20;
constexpr uint32_t menu = race + scenarioSize, awards = menu + scenarioSize;
constexpr uint32_t ghosts = awards + scenarioSize;
const Layout kLayout{players, playerSize, scenarioHeader, settings, mission,
                     ghost, scenarioSize, race, menu, awards, ghosts, ghosts + 2 * 0x2800, 4, 0x3ba0};
#endif
}
