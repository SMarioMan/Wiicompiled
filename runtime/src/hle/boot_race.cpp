#include "boot_race.h"

#include "abi_bridge.h"
#include "guest_abi/guest_region.h"
#include "guest_function_imports.h"
#include "memory.h"
#include "mkw_guest_layout.h"
#include "runtime_log.h"

#include <array>
#include <algorithm>
#include <limits>
#include <charconv>
#include <cstdint>
#include <optional>
#include <stdexcept>
#include <string>
#include <vector>

namespace {

constexpr std::string_view kOption = "--race";
constexpr uint32_t kLuigiCircuit = 0x08;
constexpr uint32_t kLastRaceCourse = 0x1F;

// Racedata (menu scenario, copied into the race scenario by Racedata::InitRace)
const auto& kLayout = MkwGuestLayout::kLayout;
constexpr uint32_t kPlayerKartOffset = 0x8;
constexpr uint32_t kPlayerCharacterOffset = 0xC;
constexpr uint32_t kPlayerTypeOffset = 0x10;
constexpr uint32_t kPlayerControllerChannelOffset = 0x6;

constexpr uint32_t kSectionPadOffset = 0x34;
constexpr uint32_t kFirstGcnControllerId = 0x124;
constexpr uint32_t kSectionP1Vs = 0x20;

enum : uint32_t { kPlayerRealLocal = 0, kPlayerCpu = 1 };
enum : uint32_t { kGameModeVs = 1, kCc150 = 2, kCpuHard = 2, kItemsBalanced = 0 };

struct Racer {
    uint32_t character;
    uint32_t kart;
};

// Standard kart matching each character's weight class.
constexpr std::array<Racer, 12> kRacers{{
    {0x00, 0x01}, // Mario
    {0x07, 0x01}, // Luigi
    {0x10, 0x01}, // Peach
    {0x0A, 0x01}, // Yoshi
    {0x0F, 0x01}, // Daisy
    {0x08, 0x00}, // Toad
    {0x06, 0x00}, // Baby Mario
    {0x0E, 0x00}, // Koopa Troopa
    {0x03, 0x02}, // Bowser
    {0x09, 0x02}, // Donkey Kong
    {0x0B, 0x02}, // Wario
    {0x16, 0x02}, // Funky Kong
}};

std::optional<uint32_t> g_course;
bool g_cpuDriven = false;
bool g_cpuMenuInput = false;

uint32_t ParseCourse(std::string_view text) {
    int base = 10;
    if (text.starts_with("0x") || text.starts_with("0X")) {
        text.remove_prefix(2);
        base = 16;
    }
    uint32_t course = 0;
    const auto [end, ec] = std::from_chars(text.data(), text.data() + text.size(), course, base);
    if (text.empty() || ec != std::errc{} || end != text.data() + text.size() || course > kLastRaceCourse) {
        throw std::invalid_argument("--race expects a race course id from 0 to 0x1F");
    }
    return course;
}

void SetUpMenuScenario(uint32_t course) {
    const uint32_t racedata = Memory::Read32(MKW_GADDR(809BD728));
    if (racedata == 0) {
        throw std::runtime_error("--race: Racedata does not exist yet at SectionMgr::Init");
    }
    const uint32_t scenario = racedata + kLayout.racedataMenu;
    for (uint32_t i = 0; i < kLayout.playerCapacity; ++i) {
        const uint32_t player = scenario + kLayout.scenarioPlayers + i * kLayout.playerStride;
        Memory::Write32(player + kPlayerKartOffset, kRacers[i % kRacers.size()].kart);
        Memory::Write32(player + kPlayerCharacterOffset, kRacers[i % kRacers.size()].character);
        Memory::Write32(player + kPlayerTypeOffset, i == 0 ? kPlayerRealLocal : kPlayerCpu);
    }
    const uint32_t settings = scenario + kLayout.scenarioSettings;
    Memory::Write32(settings + 0x00, course);
    Memory::Write32(settings + 0x04, kCc150);
    Memory::Write32(settings + 0x08, kGameModeVs);
    Memory::Write32(settings + 0x0C, 0);
    Memory::Write32(settings + 0x14, kCpuHard);
    Memory::Write32(settings + 0x18, kItemsBalanced);
    Memory::Write8(settings + 0x24, 0);
    Memory::Write8(settings + 0x25, 3);
    Memory::Write32(settings + 0x28, 0);
}

} // namespace

namespace BootRace {

bool ParseArgument(std::string_view arg) {
    if (arg == "--race-cpu" || arg == "--race-cpu-menu-input") {
        g_cpuDriven = true;
        g_cpuMenuInput = arg == "--race-cpu-menu-input" || g_cpuMenuInput;
        if (!g_course) {
            g_course = kLuigiCircuit;
        }
        return true;
    }
    if (!arg.starts_with(kOption)) {
        return false;
    }
    const std::string_view rest = arg.substr(kOption.size());
    if (rest.empty()) {
        g_course = kLuigiCircuit;
    } else if (rest.front() == '=') {
        g_course = ParseCourse(rest.substr(1));
    } else {
        return false;
    }
    return true;
}

bool Requested() {
    return g_course.has_value();
}

std::optional<Progress> ReadProgress(uint32_t raceinfo) {
    const uint32_t data = Memory::Read32(MKW_GADDR(809BD728));
    if (!raceinfo || !data) return std::nullopt;
    const uint32_t scenario = data + kLayout.racedataRace;
    const uint32_t settings = scenario + kLayout.scenarioSettings;
    Progress result{kLayout.playerCapacity, Memory::Read8(scenario + 4),
        Memory::Read32(settings), Memory::Read32(settings + 8),
        Memory::Read32(raceinfo + 0x28), Memory::Read32(raceinfo + 0x20), 0, 0};
    const uint32_t players = Memory::Read32(raceinfo + 0xC);
    if (players && result.players && result.players <= result.capacity) {
        uint32_t minimum = std::numeric_limits<uint32_t>::max();
        uint32_t minimumLap = std::numeric_limits<uint32_t>::max(), maximumLap = 0;
        for (uint32_t i = 0; i < result.players; ++i) {
            const uint32_t player = Memory::Read32(players + i * 4);
            if (!player) continue;
            ++result.observedPlayers;
            minimum = std::min(minimum, Memory::Read32(player + 0x2C));
            const uint32_t lap = Memory::Read16(player + 0x24);
            minimumLap = std::min(minimumLap, lap);
            maximumLap = std::max(maximumLap, lap);
            if (Memory::Read32(player + 0x38) & 2u) ++result.finishedPlayers;
        }
        if (result.observedPlayers == result.players) {
            result.minPlayerFrames = minimum;
            result.minLap = minimumLap;
            result.maxLap = maximumLap;
        }
    }
    return result;
}

} // namespace BootRace

// ArgumentParser::parse, picks the boot section in SectionMgr::Init.
extern "C" void ArgumentParser_Parse_805b9344(CpuContext* ctx) {
    static bool consumed = false;
    if (!g_course || consumed) {
        MKW_GUEST_FUNC(805B9344)(ctx);
        return;
    }
    consumed = true;
    SetUpMenuScenario(*g_course);

    if (!g_cpuDriven || g_cpuMenuInput) {
        const uint32_t sectionMgr = Memory::Read32(MKW_GADDR(809C1E38));
        if (sectionMgr == 0) {
            throw std::runtime_error("--race: SectionMgr instance is not set at SectionMgr::Init");
        }
        ctx->gpr[3] = sectionMgr + kSectionPadOffset;
        ctx->gpr[4] = 0;
        ctx->gpr[5] = kFirstGcnControllerId;
        MKW_GUEST_FUNC(8061B490)(ctx);
    }

    RT_LOGF("boot-race", "booting into 1P VS on course 0x%02X%s\n", *g_course, g_cpuDriven ? " (CPU driven)" : "");
    ctx->gpr[3] = kSectionP1Vs;
}
REGISTER_NATIVE_FUNCTION_AS(0x805B9344, ArgumentParser_Parse_805b9344, "ArgumentParser_Parse_805b9344");

// --race-cpu hands the local racer to the CPU driver while it keeps its camera and HUD.

// RacedataScenario::InitScreens; local racers keep no real controller channel.
extern "C" void RacedataScenario_InitScreens_8052f4e8(CpuContext* ctx) {
    if (!g_cpuDriven) {
        MKW_GUEST_NEXT(8052F4E8)(ctx);
        return;
    }
    const uint32_t players = ctx->gpr[3] + kLayout.scenarioPlayers;
    std::vector<uint8_t> channels(kLayout.playerCapacity);
    for (uint32_t i = 0; i < kLayout.playerCapacity; ++i) {
        channels[i] = Memory::Read8(players + i * kLayout.playerStride + kPlayerControllerChannelOffset);
    }
    MKW_GUEST_NEXT(8052F4E8)(ctx);
    for (uint32_t i = 0; i < kLayout.playerCapacity; ++i) {
        const uint32_t player = players + i * kLayout.playerStride;
        if (Memory::Read32(player + kPlayerTypeOffset) == kPlayerRealLocal) {
            Memory::Write8(player + kPlayerControllerChannelOffset, channels[i]);
        }
    }
}
REGISTER_NATIVE_WRAPPER_AS(0x8052F4E8, RacedataScenario_InitScreens_8052f4e8, "RacedataScenario_InitScreens_8052f4e8");

// KartAIController::IsCPU
extern "C" void KartAIController_IsCPU_8072624c(CpuContext* ctx) {
    MKW_GUEST_FUNC(8072624C)(ctx);
    if (g_cpuDriven) {
        ctx->gpr[3] = 1;
    }
}
REGISTER_NATIVE_FUNCTION_AS(0x8072624C, KartAIController_IsCPU_8072624c, "KartAIController_IsCPU_8072624c");

// Kart::Link::IsCPU; bit 0 marks a CPU racer, bit 1 the local one.
extern "C" void KartLink_IsCPU_80590664(CpuContext* ctx) {
    MKW_GUEST_FUNC(80590664)(ctx);
    if (g_cpuDriven) {
        ctx->gpr[3] = ctx->gpr[0] & 3;
    }
}
REGISTER_NATIVE_FUNCTION_AS(0x80590664, KartLink_IsCPU_80590664, "KartLink_IsCPU_80590664");

// RacedataScenario::InitRNG; fixed seeds keep --race-cpu runs repeatable for benchmarking.
extern "C" void RacedataScenario_InitRng_8052f924(CpuContext* ctx) {
    const uint32_t scenario = ctx->gpr[3];
    MKW_GUEST_FUNC(8052F924)(ctx);
    if (g_cpuDriven) {
        Memory::Write32(scenario + kLayout.scenarioSettings + 0x2C, 0x74A1B095u);
        Memory::Write32(scenario + kLayout.scenarioSettings + 0x30, 0x1234567u);
    }
}
REGISTER_NATIVE_FUNCTION_AS(0x8052F924, RacedataScenario_InitRng_8052f924, "RacedataScenario_InitRng_8052f924");

// SectionPad::GetFirstDeactivatedPad; a CPU driven racer needs no controller.
extern "C" void SectionPad_GetFirstDeactivatedPad_8061b9bc(CpuContext* ctx) {
    MKW_GUEST_FUNC(8061B9BC)(ctx);
    if (g_cpuDriven && !g_cpuMenuInput) {
        ctx->gpr[3] = static_cast<uint32_t>(-1);
    }
}
REGISTER_NATIVE_FUNCTION_AS(0x8061B9BC, SectionPad_GetFirstDeactivatedPad_8061b9bc,
                            "SectionPad_GetFirstDeactivatedPad_8061b9bc");

// Evidence comes from the real simulation update; process survival alone is not
// a successful race test. Ordinary interactive runs keep their usual logging.
extern "C" void Raceinfo_Update_805331b4(CpuContext* ctx) {
    const uint32_t raceinfo = ctx->gpr[3];
    MKW_GUEST_NEXT(805331B4)(ctx);
    if (!g_cpuDriven) return;
    static uint32_t previous = 0, lastStage = std::numeric_limits<uint32_t>::max(), ticks = 0;
    const auto progress = BootRace::ReadProgress(raceinfo);
    if (!progress) return;
    ++ticks;
    if (raceinfo != previous || progress->stage != lastStage || ticks % 60 == 0) {
        RT_LOGF("race-progress", "stage=%u frames=%u players=%u capacity=%u observed=%u min_player_frames=%u course=%u mode=%u finished=%u min_lap=%u max_lap=%u\n",
            progress->stage, progress->frames, progress->players, progress->capacity,
            progress->observedPlayers, progress->minPlayerFrames, progress->course, progress->mode,
            progress->finishedPlayers, progress->minLap, progress->maxLap);
        previous = raceinfo;
        lastStage = progress->stage;
    }
}
REGISTER_NATIVE_WRAPPER_AS(0x805331B4, Raceinfo_Update_805331b4, "Raceinfo_Update_805331b4");

// Keep a real menu controller while the CPU drives the local racer. The regular
// holder initializer can otherwise select the dummy controller on direct boot.
extern "C" void InputManager_InitControllerHolders_805242d8(CpuContext* ctx) {
    const uint32_t manager = ctx->gpr[3];
    MKW_GUEST_NEXT(805242D8)(ctx);
    if (!g_cpuMenuInput) return;
    const CpuContext result = *ctx;
    const uint32_t holder = manager + kLayout.inputRealHolders;
    ctx->gpr[3] = holder;
    ctx->gpr[4] = manager + kLayout.inputGCNControllers;
    ctx->gpr[5] = 0;
    MKW_GUEST_FUNC(80521554)(ctx);
    const uint32_t section = Memory::Read32(MKW_GADDR(809C1E38));
    if (section) {
        Memory::Write32(section + 0x44, holder);
        Memory::Write32(section + 0x3C, Memory::Read32(section + 0x38));
    }
    *ctx = result;
}
REGISTER_NATIVE_WRAPPER_AS(0x805242D8, InputManager_InitControllerHolders_805242d8,
                           "InputManager_InitControllerHolders_805242d8");

// Race initialization can reset the real holder after the menu setup above.
// Keep that holder available to menus while the racer uses its separate AI input.
extern "C" void RealControllerHolder_SetController_80521554(CpuContext* ctx) {
    if (g_cpuMenuInput) {
        const uint32_t manager = Memory::Read32(MKW_GADDR(809BD70C));
        if (manager && ctx->gpr[3] == manager + kLayout.inputRealHolders)
            ctx->gpr[4] = manager + kLayout.inputGCNControllers;
    }
    MKW_GUEST_NEXT(80521554)(ctx);
}
REGISTER_NATIVE_WRAPPER_AS(0x80521554, RealControllerHolder_SetController_80521554,
                           "RealControllerHolder_SetController_80521554");
