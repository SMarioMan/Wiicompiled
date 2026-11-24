#include "lan_runtime.h"
#include "guest_abi/guest_region.h"
#include "hle_stubs.h"
#include "memory.h"
#include "mkw_guest_layout.h"
#include "hle/controller_status_contract.h"
#include "input_bindings.h"
#include "motion_trick.h"
#include "runtime_product.h"
#include "shared_audio_mix.h"
#include "test_pad_input.h"

#include <algorithm>
#include <atomic>
#include <bit>
#include <cstdio>
#include <cstdint>
#include <cstdlib>
#include <cstring>

#include <SDL3/SDL_gamepad.h>
#include <dolphin/pad.h>

namespace {

std::atomic<bool> g_rumbleEnabled{true};

bool NativeButtonHeld(SDL_Gamepad* gamepad, uint32_t nativeButton) {
    if (gamepad == nullptr || nativeButton == PAD_NATIVE_BUTTON_INVALID ||
        nativeButton >= SDL_GAMEPAD_BUTTON_COUNT) {
        return false;
    }
    return SDL_GetGamepadButton(gamepad, static_cast<SDL_GamepadButton>(nativeButton));
}

// A digital button bound to L or R has no analog travel of its own. On real
// hardware the click only engages at full depression, so report a full pull.
void FillTriggersHeldByButtons(PADStatus* statuses) {
    if (InputBindings::InputBlocked()) {
        return;
    }
    for (uint32_t port = 0; port < PAD_CHANMAX; ++port) {
        if (statuses[port].err != PAD_ERR_NONE) {
            continue;
        }
        const s32 index = PADGetIndexForPort(port);
        if (index < 0) {
            continue;
        }
        SDL_Gamepad* gamepad = PADGetSDLGamepadForIndex(static_cast<u32>(index));
        if (gamepad == nullptr) {
            continue;
        }
        const auto scan = [&](PADButtonMapping* mappings, u32 count) {
            if (mappings == nullptr) {
                return;
            }
            for (u32 i = 0; i < count; ++i) {
                const PADButtonMapping& mapping = mappings[i];
                if (mapping.padButton != PAD_TRIGGER_L && mapping.padButton != PAD_TRIGGER_R) {
                    continue;
                }
                if (!NativeButtonHeld(gamepad, mapping.nativeButton)) {
                    continue;
                }
                if (mapping.padButton == PAD_TRIGGER_L) {
                    statuses[port].triggerLeft = 255;
                } else {
                    statuses[port].triggerRight = 255;
                }
            }
        };
        u32 count = 0;
        scan(PADGetButtonMappings(port, &count), count);
        count = 0;
        scan(PADGetAltButtonMappings(port, &count), count);
    }
}

// Skip the ESRB notice screen ("Online interactions are not rated by the ESRB",
// PageId 0x01, Pages::ESRBNotice) by advancing its active hold timer past 90 frames
// immediately (and redirecting nextSectionId 0x16 -> 0x40 when possible).
void SkipEsrbNoticePage() {
    try {
        const uint32_t manager = Memory::Read32(MKW_GADDR(809C1E38));
        if (!manager) return;
        const uint32_t section = Memory::Read32(manager);
        const uint32_t count = section ? Memory::Read32(section + 0x37Cu) : 0;
        if (count == 0 || count > 11) {
            return;
        }
        for (uint32_t i = 0; i < count; ++i) {
            const uint32_t page = Memory::Read32(section + 0x354u + i * 4u);
            if (page != 0 && Memory::Read32(page + 4u) == 0x01u) {
                // Hide the ESRB notice LayoutUIControl (page + 84) so it never flashes
                // during section fade-in/fade-out, and advance duration past 90 frames.
                Memory::Write8(page + 84u + 96u, 0u);
                Memory::Write8(page + 84u + 128u, 1u);
                if (Memory::Read32(page + 32u) <= 90u) {
                    Memory::Write32(page + 32u, 91u);
                }
            }
        }
    } catch (const Memory::AccessViolation&) {
    }
}

// Skip the online "Check Members" / VR screen (Pages::VR, PageId 0x90) straight
// to the Course Vote screen by zeroing its countdown timer immediately.
void SkipCheckMembersPage() {
    const auto& layout = MkwGuestLayout::kLayout;
    if (layout.playerCapacity != 128u) {
        return;
    }
    try {
        const uint32_t manager = Memory::Read32(MKW_GADDR(809C1E38));
        const uint32_t section = manager ? Memory::Read32(manager) : 0;
        const uint32_t count = section ? Memory::Read32(section + 0x37Cu) : 0;
        if (count == 0 || count > 11) {
            return;
        }
        for (uint32_t i = 0; i < count; ++i) {
            const uint32_t page = Memory::Read32(section + 0x354u + i * 4u);
            if (page != 0 && Memory::Read32(page + 4u) == 0x90u) {
                const uint32_t countdown = Memory::Read32(page + 49592u);
                if (countdown != 0u) {
                    Memory::WriteFloat32(countdown, 0.0f);
                }
            }
        }
    } catch (const Memory::AccessViolation&) {
    }
}

void UpdateSharedAudioRaceState() {
    if (!SharedAudioMix::IsEnabled()) {
        return;
    }
    try {
        const uint32_t raceinfo = Memory::Read32(MKW_GADDR(809BD730));
        SharedAudioMix::SetInRace(raceinfo != 0u);
    } catch (const Memory::AccessViolation&) {
        SharedAudioMix::SetInRace(false);
    }
}

// Unlock all characters, vehicles, cups, and race classes (50cc/100cc/150cc/Mirror)
// on every valid license in SaveDataManager::sInstance (PAL 0x809BD748 / USA 0x809B8F88).
void UnlockAllContent() {
    try {
        const uint32_t mgr = Memory::Read32(MKW_GADDR(809BD748));
        if (!mgr) return;
        const uint32_t raw = Memory::Read32(mgr + 20u);
        if (!raw) return;
        for (uint32_t lic = 0; lic < 4u; ++lic) {
            if (Memory::Read32(raw + 8u + lic * 0x8CC0u) != 0x524B5044u) { // 'RKPD'
                continue;
            }
            const uint32_t unlockBase = mgr + 56u + lic * 0x93F0u + 0x8FFCu;
            for (uint32_t w = 0; w < 4u; ++w) {
                if (Memory::Read32(unlockBase + w * 4u) != 0xFFFFFFFFu) {
                    Memory::Write32(unlockBase + w * 4u, 0xFFFFFFFFu);
                }
            }
        }
    } catch (const Memory::AccessViolation&) {
    }
}

// Seed SectionParams->characters[0] before Character Select (PageId 0x6B) opens
// so each LAN instance highlights a unique non-Mii character (0..23) by default.
void SeedDefaultCharacterHighlight() {
    static bool seededForCharSelect = false;
    if (seededForCharSelect) return;
    const auto& options = LanRuntime::Settings();
    if (options.instance == 0) return;
    try {
        const uint32_t manager = Memory::Read32(MKW_GADDR(809C1E38));
        if (!manager) return;
        const uint32_t section = Memory::Read32(manager);
        const uint32_t globalContext = Memory::Read32(manager + 0x98u);
        if (!globalContext) return;
        if (section) {
            const uint32_t count = Memory::Read32(section + 0x37Cu);
            if (count > 0 && count <= 11) {
                for (uint32_t i = 0; i < count; ++i) {
                    const uint32_t page = Memory::Read32(section + 0x354u + i * 4u);
                    if (page != 0 && Memory::Read32(page + 4u) == 0x6Bu) {
                        seededForCharSelect = true;
                        return;
                    }
                }
            }
        }
        const uint32_t targetChar = (options.instance - 1u) % 24u;
        Memory::Write32(globalContext + 0x12Cu, targetChar);
    } catch (const Memory::AccessViolation&) {
    }
}

// Arrange expanded Course Voting cards (Pages::Vote, PageId 0x92) in a balanced,
// dynamically scaled grid so >12 votes never pile up at the center of the screen.
void LayoutExpandedVotePage() {
    const auto& layout = MkwGuestLayout::kLayout;
    if (layout.playerCapacity <= 12) return;
    try {
        const uint32_t manager = Memory::Read32(MKW_GADDR(809C1E38));
        const uint32_t section = manager ? Memory::Read32(manager) : 0;
        const uint32_t count = section ? Memory::Read32(section + 0x37Cu) : 0;
        if (count == 0 || count > 11) return;

        uint32_t votePage = 0;
        for (uint32_t i = 0; i < count; ++i) {
            const uint32_t page = Memory::Read32(section + 0x354u + i * 4u);
            if (page != 0 && Memory::Read32(page + 4u) == 0x92u) {
                votePage = page;
                break;
            }
        }
        if (!votePage) return;

        constexpr uint32_t kVotesOffset = 1224u;
        constexpr uint32_t kVoteStride = 376u;
        const uint32_t voteCountOffset =
            kVotesOffset + layout.playerCapacity * kVoteStride + layout.playerCapacity * 4u + 8u;
        const uint32_t voteCount = Memory::Read32(votePage + voteCountOffset);
        if (voteCount <= 12 || voteCount > layout.playerCapacity) return;

        constexpr float kAvailW = 740.f;
        constexpr float kAvailH = 290.f;
        constexpr float kCenterY = -8.f;
        constexpr float kBaseCardW = 205.f;
        constexpr float kBaseCardH = 52.f;

        uint32_t bestRows = 4;
        float bestScale = 0.f;
        const uint32_t maxRows = std::min<uint32_t>(voteCount, 16u);
        for (uint32_t r = 1; r <= maxRows; ++r) {
            const uint32_t maxCols = (voteCount + r - 1) / r;
            const float sx = kAvailW / (static_cast<float>(maxCols) * kBaseCardW);
            const float sy = kAvailH / (static_cast<float>(r) * kBaseCardH);
            const float s = std::min(0.95f, std::min(sx, sy));
            if (s > bestScale) {
                bestScale = s;
                bestRows = r;
            }
        }
        const float scale = std::clamp(bestScale, 0.24f, 0.95f);
        const uint32_t rows = bestRows;
        const uint32_t maxCols = (voteCount + rows - 1) / rows;
        const float cellW = std::min(kBaseCardW * scale * 1.06f, kAvailW / static_cast<float>(maxCols));
        const float cellH = std::min(kBaseCardH * scale * 1.06f, kAvailH / static_cast<float>(rows));

        uint32_t index = 0;
        for (uint32_t row = 0; row < rows; ++row) {
            const uint32_t cols = voteCount / rows + (row < voteCount % rows ? 1u : 0u);
            for (uint32_t col = 0; col < cols; ++col, ++index) {
                const uint32_t control = votePage + kVotesOffset + index * kVoteStride;
                const float x = cellW * (static_cast<float>(col) - (static_cast<float>(cols) - 1.f) * 0.5f);
                const float y = kCenterY + cellH * ((static_cast<float>(rows) - 1.f) * 0.5f - static_cast<float>(row));
                Memory::Write32(control + 76u, std::bit_cast<uint32_t>(x));
                Memory::Write32(control + 80u, std::bit_cast<uint32_t>(y));
                Memory::Write32(control + 88u, std::bit_cast<uint32_t>(scale));
                Memory::Write32(control + 92u, std::bit_cast<uint32_t>(scale));
            }
        }
    } catch (const Memory::AccessViolation&) {
    }
}

// The LAN boot/menu path uses a virtual port, including unattended local instances.
// Keep this in the native PAD path: jumping into the original PADRead would bypass HLE.
void ApplyLanMenuInput(PADStatus& pad, uint32_t& rumbleMask) {
    const auto& product = RuntimeProduct::Active();
    if (product.kind != RuntimeProduct::Kind::Lan) return;
    pad.err = PAD_ERR_NONE;
    rumbleMask |= 0x80000000u;
    static unsigned pulse = 0;
    static bool startupDone = false;
    static uint32_t lastPage = UINT32_MAX;
    const auto& options = LanRuntime::Settings();
    if (startupDone) return;
    try {
        const auto manager = Memory::Read32(0x809BD508u); // USA boot/menu layout
        const auto section = manager ? Memory::Read32(manager) : 0;
        const auto count = section ? Memory::Read32(section + 0x37Cu) : 0;
        const auto page = count > 0 && count <= 11 ? Memory::Read32(section + 0x354u + (count - 1) * 4) : 0;
        const auto id = page ? Memory::Read32(page + 4) : 0;
        if (id != lastPage) {
            RT_LOG(RT_TAG_MOD) << "LAN instance " << options.instance << " menu page 0x"
                << std::hex << id << " in section 0x" << (section ? Memory::Read32(section) : 0)
                << std::dec << std::endl;
            lastPage = id;
        }
        if (options.automation == LanRuntime::Automation::Manual) return;
        if (id == 0x9D) {
            // Host/join is a startup convenience, not a permanent lobby role.
            LanRuntime::FinishAutomation();
            startupDone = true;
        }
        const bool select = page && Memory::Read32(page + 8) == 4 &&
            (id == 0x85 || id == 0x86 ||
             (id == 0x4E && (Memory::Read32(section) == 0x55 || Memory::Read32(section) == 0x84)) ||
             (id == 0x51 && Memory::Read32(manager + 0x38) == 0x124 &&
                Memory::Read32(manager + 0x3C) == 0x124) ||
             (options.automation == LanRuntime::Automation::Host && id == 0x8D) ||
             (options.automation == LanRuntime::Automation::Join && (id == 0x96 || id == 0x97)));
        if (select) {
            const unsigned phase = pulse++ % 30;
            if (id == 0x86) {
                if (phase < 4) pad.button |= PAD_BUTTON_LEFT | PAD_BUTTON_UP;
                else if (phase >= 8 && phase < 12) pad.button |= PAD_BUTTON_A;
            } else if (phase < 4) pad.button |= PAD_BUTTON_A;
        } else pulse = 0;
    } catch (const Memory::AccessViolation&) {
        pulse = 0;
    }
}

void WritePadStatus(uint32_t base, const PADStatus& status) {
    const auto guestStatus = PadStatusContract::Encode({
        status.button,
        status.stickX,
        status.stickY,
        status.substickX,
        status.substickY,
        status.triggerL,
        status.triggerR,
        status.analogA,
        status.analogB,
        status.err,
    });
    uint8_t* dst = Memory::GetPointer(base, guestStatus.size());
    std::memcpy(dst, guestStatus.data(), guestStatus.size());
}

} // namespace

extern "C" bool PAD_HLE_RumbleEnabled()
{
    return g_rumbleEnabled.load(std::memory_order_relaxed);
}

extern "C" void PAD_HLE_SetRumbleEnabled(bool enabled)
{
    g_rumbleEnabled.store(enabled, std::memory_order_relaxed);
}

extern "C" uint32_t PAD__Init_HLE()
{
    return PADInit() ? 1u : 0u;
}
PPC_NATIVE_OVERRIDE(801AF2F0, PAD__Init_HLE, uint32_t, (), ());

// PADRead: gathers every GameCube pad source for the frame and writes the statuses to guest memory.
extern "C" uint32_t PAD__Read_HLE(uint32_t statusPtr)
{
    if (statusPtr == 0) {
        return 0;
    }

    PADStatus statuses[PAD_CHANMAX]{};
    uint32_t rumbleMask = PADRead(statuses);

    FillTriggersHeldByButtons(statuses);
    InputBindings::Apply(statuses);
    MotionTrick::Apply(statuses);
    SkipEsrbNoticePage();
    SkipCheckMembersPage();
    UpdateSharedAudioRaceState();
    UnlockAllContent();
    SeedDefaultCharacterHighlight();
    LayoutExpandedVotePage();
    ApplyLanMenuInput(statuses[0], rumbleMask);
    if (const auto input = TestPadInput::ReadConfigured()) {
        statuses[0].err = PAD_ERR_NONE;
        rumbleMask |= 0x80000000u;
        statuses[0].button |= input->buttons;
        statuses[0].stickX = input->stickX;
        statuses[0].stickY = input->stickY;
    }

    try {
        for (uint32_t i = 0; i < PAD_CHANMAX; ++i) {
            WritePadStatus(statusPtr + static_cast<uint32_t>(i * PadStatusContract::kGuestStatusSize),
                           statuses[i]);
        }
    } catch (const Memory::AccessViolation&) {
        return 0;
    }

    return rumbleMask;
}
PPC_NATIVE_OVERRIDE(801AF44C, PAD__Read_HLE, uint32_t, (uint32_t statusPtr), (statusPtr));

extern "C" uint32_t PAD__Reset_HLE(uint32_t mask)
{
    return PADReset(mask) ? 1u : 0u;
}
PPC_NATIVE_OVERRIDE(801AF0DC, PAD__Reset_HLE, uint32_t, (uint32_t mask), (mask));

extern "C" uint32_t PAD__Recalibrate_HLE(uint32_t mask)
{
    return PADRecalibrate(mask) ? 1u : 0u;
}
PPC_NATIVE_OVERRIDE(801AF1E4, PAD__Recalibrate_HLE, uint32_t, (uint32_t mask), (mask));

extern "C" void PAD__ControlMotor_HLE(int32_t chan, uint32_t command)
{
    if (command == PAD_MOTOR_RUMBLE && !g_rumbleEnabled.load(std::memory_order_relaxed)) {
        command = PAD_MOTOR_STOP;
    }
    PADControlMotor(chan, command);
}
PPC_NATIVE_OVERRIDE_VOID(801AF908, PAD__ControlMotor_HLE, (int32_t chan, uint32_t command), (chan, command));
