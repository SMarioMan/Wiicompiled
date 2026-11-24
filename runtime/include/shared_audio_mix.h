#pragma once

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstring>

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

namespace SharedAudioMix {

constexpr uint32_t kMaxSlots = 128;
constexpr uint32_t kRingBlocks = 16;      // 16 x 3ms = 48ms jitter buffer per secondary instance
constexpr uint32_t kMaxBlockFrames = 256; // Wii AX 3ms frame = 96 stereo frames (headroom for 48kHz = 144)
constexpr uint32_t kDedupTableSize = 512; // Power of 2 open-addressed voice dedup table
constexpr uint32_t kMasterStreamSlots = 8;

struct alignas(64) AudioBlock {
    std::atomic<uint64_t> seq;
    uint32_t frameCount;
    uint32_t pad;
    int16_t samples[kMaxBlockFrames * 2]; // Interleaved LE16 L, R
};

struct alignas(64) SecondaryAudioRing {
    std::atomic<uint64_t> writeHead;
    std::atomic<uint64_t> lastActiveTickMs;
    uint8_t pad[48];
    AudioBlock blocks[kRingBlocks];
};

struct alignas(16) VoiceDedupEntry {
    std::atomic<uint64_t> keyAndOwner; // Upper 56 bits: hash, lower 8 bits: owner slot (0..127)
    std::atomic<uint64_t> lastTickMs;
};

struct SharedAudioArena {
    uint32_t magic;
    uint32_t version;
    alignas(64) std::atomic<uint64_t> activeMask[2];
    alignas(64) std::atomic<uint64_t> masterStreamKeys[kMasterStreamSlots];
    alignas(64) std::atomic<uint64_t> masterStreamTickMs[kMasterStreamSlots];
    alignas(64) VoiceDedupEntry dedup[kDedupTableSize];
    SecondaryAudioRing rings[kMaxSlots];
};

struct Config {
    bool enabled = false;
    bool isMaster = false;
    uint32_t slot = 0; // 0..127 (0 = master)
};

inline Config& State() noexcept {
    static Config s_cfg{};
    return s_cfg;
}

inline void Configure(bool enabled, bool isMaster, uint32_t instanceId) noexcept {
    Config& cfg = State();
    cfg.enabled = enabled;
    cfg.isMaster = enabled && isMaster;
    cfg.slot = (instanceId >= 1 && instanceId <= kMaxSlots) ? (instanceId - 1) : 0;
}

inline bool IsEnabled() noexcept { return State().enabled; }
inline bool IsMaster() noexcept { return State().enabled && State().isMaster; }
inline bool IsSecondary() noexcept { return State().enabled && !State().isMaster; }

#ifdef _WIN32

inline uint64_t NowMs() noexcept {
    return static_cast<uint64_t>(GetTickCount64());
}

inline SharedAudioArena* GetArena() noexcept {
    static SharedAudioArena* s_arena = []() -> SharedAudioArena* {
        if (!IsEnabled()) {
            return nullptr;
        }
        const uint64_t sz = sizeof(SharedAudioArena);
        HANDLE mapping = CreateFileMappingW(
            INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
            static_cast<DWORD>(sz >> 32), static_cast<DWORD>(sz & 0xFFFFFFFFu),
            L"Local\\MKW_AUDIO_MIX_V1");
        if (!mapping) {
            return nullptr;
        }
        void* view = MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(SharedAudioArena));
        if (!view) {
            CloseHandle(mapping);
            return nullptr;
        }
        auto* arena = static_cast<SharedAudioArena*>(view);
        arena->magic = 0x4D4B4155u; // 'MKAU'
        arena->version = 1;
        return arena;
    }();
    return s_arena;
}

inline uint64_t MixHash64(uint64_t x) noexcept {
    x ^= x >> 30;
    x *= 0xbf58476d1ce4e5b9ull;
    x ^= x >> 27;
    x *= 0x94d049bb133111ebull;
    x ^= x >> 31;
    return x;
}

// Decides whether the current instance should mix an AX voice into its output bus.
// - Stage music (isStream == true) is always played by Master (slot 0) and muted on
//   secondary instances when they play the same stream, while distinct power-up streams
//   (e.g. Star Power / Mega Mushroom) play once across secondary instances.
// - Global SFX (Lightning, Countdown, Stage Ambience) sharing the same sample signature
//   and pitch bucket within 30ms are claimed by one instance so they are heard once.
// - Distinct character voices, item hits, and kart engines at different pitches pass through.
inline bool ShouldMixVoice(bool isStream, uint32_t loopAddr, uint32_t endAddr,
                           uint16_t sampleFormat, uint32_t srcRatio) noexcept {
    if (!IsEnabled()) {
        return true;
    }
    SharedAudioArena* arena = GetArena();
    if (!arena) {
        return true;
    }

    const uint32_t mySlot = State().slot;
    const uint64_t nowMs = NowMs();

    if (isStream) {
        // Stream signature: buffer span + coarse playback ratio + format.
        const uint32_t span = (endAddr >= loopAddr) ? (endAddr - loopAddr) : 0u;
        const uint64_t streamKey = MixHash64(
            (static_cast<uint64_t>(span) << 24) ^
            (static_cast<uint64_t>(srcRatio >> 8) << 4) ^
            static_cast<uint64_t>(sampleFormat) ^ 0x5354524D00000001ull);
        if (State().isMaster) {
            // Master always plays its own BGM and registers it so secondary instances
            // suppress duplicate stage/menu music regardless of boot phase offset.
            const uint32_t idx = static_cast<uint32_t>(streamKey & (kMasterStreamSlots - 1));
            arena->masterStreamKeys[idx].store(streamKey, std::memory_order_relaxed);
            arena->masterStreamTickMs[idx].store(nowMs, std::memory_order_release);
            return true;
        }
        // Check if Master is currently playing this exact stream (within 250ms).
        for (uint32_t i = 0; i < kMasterStreamSlots; ++i) {
            const uint64_t ts = arena->masterStreamTickMs[i].load(std::memory_order_acquire);
            if (ts != 0 && nowMs >= ts && (nowMs - ts) <= 250u) {
                if (arena->masterStreamKeys[i].load(std::memory_order_relaxed) == streamKey) {
                    return false; // Duplicate stage/menu BGM -> Master already plays it
                }
            }
        }
        // Distinct stream (e.g. Star Power or Mega Mushroom on a secondary player):
        // deduplicate across secondary instances so even multiple simultaneous Stars play once.
        const uint32_t bucket = static_cast<uint32_t>(streamKey & (kDedupTableSize - 1));
        const uint64_t keyHi = (streamKey & 0xFFFFFFFFFFFFFF00ull);
        VoiceDedupEntry& e = arena->dedup[bucket];
        const uint64_t prevTs = e.lastTickMs.load(std::memory_order_acquire);
        const uint64_t prevKo = e.keyAndOwner.load(std::memory_order_relaxed);
        if (prevTs != 0 && nowMs >= prevTs && (nowMs - prevTs) <= 60u &&
            (prevKo & 0xFFFFFFFFFFFFFF00ull) == keyHi && (prevKo & 0xFFu) != mySlot) {
            return false;
        }
        e.keyAndOwner.store(keyHi | (mySlot & 0xFFu), std::memory_order_relaxed);
        e.lastTickMs.store(nowMs, std::memory_order_release);
        return true;
    }

    // Non-streamed SFX: deduplicate exact identical (loop, end, format, pitch-bucket)
    // sounds triggered simultaneously (e.g. Lightning, Countdown 3-2-1-GO, Lap Fanfare).
    // Master always wins ties so Player 1's local audio stays 100% intact.
    const uint64_t sfxKey = MixHash64(
        (static_cast<uint64_t>(loopAddr) << 32) ^
        static_cast<uint64_t>(endAddr) ^
        (static_cast<uint64_t>(srcRatio >> 10) << 16) ^
        static_cast<uint64_t>(sampleFormat));
    const uint32_t bucket = static_cast<uint32_t>(sfxKey & (kDedupTableSize - 1));
    const uint64_t keyHi = (sfxKey & 0xFFFFFFFFFFFFFF00ull);
    VoiceDedupEntry& e = arena->dedup[bucket];

    if (State().isMaster) {
        e.keyAndOwner.store(keyHi | (mySlot & 0xFFu), std::memory_order_relaxed);
        e.lastTickMs.store(nowMs, std::memory_order_release);
        return true;
    }

    const uint64_t prevTs = e.lastTickMs.load(std::memory_order_acquire);
    const uint64_t prevKo = e.keyAndOwner.load(std::memory_order_relaxed);
    if (prevTs != 0 && nowMs >= prevTs && (nowMs - prevTs) <= 30u &&
        (prevKo & 0xFFFFFFFFFFFFFF00ull) == keyHi && (prevKo & 0xFFu) != mySlot) {
        return false;
    }
    e.keyAndOwner.store(keyHi | (mySlot & 0xFFu), std::memory_order_relaxed);
    e.lastTickMs.store(nowMs, std::memory_order_release);
    return true;
}

// Called by secondary instances (2..N) in AudioBackend instead of pushing to WASAPI/SDL.
inline void PublishSecondaryBlock(const int16_t* stereoSamples, size_t frameCount) noexcept {
    if (!IsSecondary() || !stereoSamples || frameCount == 0 || frameCount > kMaxBlockFrames) {
        return;
    }
    SharedAudioArena* arena = GetArena();
    if (!arena) {
        return;
    }
    const uint32_t slot = State().slot;
    SecondaryAudioRing& ring = arena->rings[slot];
    const uint64_t seq = ring.writeHead.load(std::memory_order_relaxed);
    AudioBlock& blk = ring.blocks[seq & (kRingBlocks - 1)];
    blk.seq.store(0, std::memory_order_release);
    blk.frameCount = static_cast<uint32_t>(frameCount);
    std::memcpy(blk.samples, stereoSamples, frameCount * 2 * sizeof(int16_t));
    blk.seq.store(seq + 1, std::memory_order_release);
    ring.writeHead.store(seq + 1, std::memory_order_release);
    ring.lastActiveTickMs.store(NowMs(), std::memory_order_release);

    const uint64_t bit = 1ull << (slot & 63u);
    auto& maskWord = arena->activeMask[slot >> 6];
    if ((maskWord.load(std::memory_order_relaxed) & bit) == 0) {
        maskWord.fetch_or(bit, std::memory_order_release);
    }
}

// Soft-knee limiter: linear below 0.82 full scale, smoothly saturates toward +-1.0
// above 0.82 so simultaneous explosions/items across 128 players never digitally clip.
inline float SoftLimit(float x) noexcept {
    constexpr float kKnee = 0.82f;
    const float ax = std::fabs(x);
    if (ax <= kKnee) {
        return x;
    }
    const float excess = (ax - kKnee) / (1.0f - kKnee);
    const float limited = kKnee + (1.0f - kKnee) * std::tanh(excess);
    return x < 0.0f ? -limited : limited;
}

// Called by Master (instance 1) right before pushing its stereo block to SDL3/WASAPI.
// Pulls 1 matching block from each active secondary instance, applies constant-power
// RMS normalization (prioritizing Master's Stage Music), and soft-limits to prevent clipping.
inline void MixIntoMasterBlock(int16_t* masterStereo, size_t frameCount) noexcept {
    if (!IsMaster() || !masterStereo || frameCount == 0 || frameCount > kMaxBlockFrames) {
        return;
    }
    SharedAudioArena* arena = GetArena();
    if (!arena) {
        return;
    }

    static uint64_t s_readSeq[kMaxSlots]{};
    static float s_secL[kMaxBlockFrames]{};
    static float s_secR[kMaxBlockFrames]{};

    std::memset(s_secL, 0, frameCount * sizeof(float));
    std::memset(s_secR, 0, frameCount * sizeof(float));

    const uint64_t nowMs = NowMs();
    const uint64_t mask0 = arena->activeMask[0].load(std::memory_order_acquire);
    const uint64_t mask1 = arena->activeMask[1].load(std::memory_order_acquire);
    const uint32_t masterSlot = State().slot;

    uint32_t audibleContributors = 0;

    auto processMask = [&](uint64_t mask, uint32_t baseSlot) {
        while (mask != 0) {
            const uint32_t bit = static_cast<uint32_t>(__builtin_ctzll(mask));
            mask &= (mask - 1);
            const uint32_t slot = baseSlot + bit;
            if (slot == masterSlot || slot >= kMaxSlots) {
                continue;
            }
            const SecondaryAudioRing& ring = arena->rings[slot];
            const uint64_t lastMs = ring.lastActiveTickMs.load(std::memory_order_acquire);
            if (lastMs == 0 || nowMs < lastMs || (nowMs - lastMs) > 500u) {
                continue;
            }
            const uint64_t head = ring.writeHead.load(std::memory_order_acquire);
            uint64_t cur = s_readSeq[slot];
            if (cur >= head) {
                continue;
            }
            // Keep latency tight (~1-2 blocks = 3-6ms) if a writer ran ahead.
            if (head - cur > 4u) {
                cur = head - 2u;
            }
            const AudioBlock& blk = ring.blocks[cur & (kRingBlocks - 1)];
            const uint64_t seq1 = blk.seq.load(std::memory_order_acquire);
            if (seq1 != cur + 1) {
                s_readSeq[slot] = head;
                continue;
            }
            const size_t n = std::min<size_t>(frameCount, blk.frameCount);
            int32_t peak = 0;
            int16_t localTmp[kMaxBlockFrames * 2];
            std::memcpy(localTmp, blk.samples, n * 2 * sizeof(int16_t));
            const uint64_t seq2 = blk.seq.load(std::memory_order_acquire);
            if (seq2 != seq1) {
                s_readSeq[slot] = ring.writeHead.load(std::memory_order_acquire);
                continue;
            }
            s_readSeq[slot] = cur + 1;

            for (size_t i = 0; i < n; ++i) {
                const int16_t l = localTmp[i * 2];
                const int16_t r = localTmp[i * 2 + 1];
                const int32_t al = l < 0 ? -static_cast<int32_t>(l) : static_cast<int32_t>(l);
                const int32_t ar = r < 0 ? -static_cast<int32_t>(r) : static_cast<int32_t>(r);
                if (al > peak) peak = al;
                if (ar > peak) peak = ar;
                s_secL[i] += static_cast<float>(l) * (1.0f / 32768.0f);
                s_secR[i] += static_cast<float>(r) * (1.0f / 32768.0f);
            }
            if (peak > 48) { // Ignore digital silence so silent slots don't duck audible players
                ++audibleContributors;
            }
        }
    };

    processMask(mask0, 0);
    processMask(mask1, 64);

    if (audibleContributors == 0) {
        return;
    }

    // Constant-power RMS normalization for secondary SFX bus, keeping Master's Stage Music
    // clear and prioritized (akin to native 4-player split-screen balance).
    const float secGain = 0.72f / std::sqrt(static_cast<float>(audibleContributors));
    constexpr float kMasterGain = 0.92f;

    for (size_t i = 0; i < frameCount; ++i) {
        const float ml = static_cast<float>(masterStereo[i * 2]) * (1.0f / 32768.0f);
        const float mr = static_cast<float>(masterStereo[i * 2 + 1]) * (1.0f / 32768.0f);
        const float outL = SoftLimit(ml * kMasterGain + s_secL[i] * secGain);
        const float outR = SoftLimit(mr * kMasterGain + s_secR[i] * secGain);
        masterStereo[i * 2] = static_cast<int16_t>(std::clamp(std::lround(outL * 32767.0f), -32768l, 32767l));
        masterStereo[i * 2 + 1] = static_cast<int16_t>(std::clamp(std::lround(outR * 32767.0f), -32768l, 32767l));
    }
}

#else

inline bool ShouldMixVoice(bool, uint32_t, uint32_t, uint16_t, uint32_t) noexcept { return true; }
inline void PublishSecondaryBlock(const int16_t*, size_t) noexcept {}
inline void MixIntoMasterBlock(int16_t*, size_t) noexcept {}

#endif // _WIN32

} // namespace SharedAudioMix
