#pragma once

#include <string_view>
#include <cstdint>
#include <optional>

// `--race[=<course id>]`: boot straight into a 1P VS race instead of the title screen.
// `--race-mode=vs|team-vs|grand-prix|balloon|coin|time-trial`: select the mode.
// `--race-cpu`: the same, with the local racer driven by the CPU.
// `--race-cpu-menu-input`: retain a real controller for menu confirmation.
// `--race-players=N` and `--race-laps=N`: select the field and race length.
namespace BootRace {
// False when `arg` is not a boot-race option; throws on a malformed one.
bool ParseArgument(std::string_view arg);
bool Requested();

struct Progress {
    uint32_t capacity, players, course, mode, stage, frames;
    uint32_t observedPlayers, minPlayerFrames;
    uint32_t finishedPlayers, minLap, maxLap;
};
// Read-only simulation evidence for automated boot races. Missing state returns null.
std::optional<Progress> ReadProgress(uint32_t raceinfo);
} // namespace BootRace
