#pragma once

#ifdef _WIN32

#include "lan_runtime.h"
#include "network_internal.h"

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstring>

namespace NetworkHle::ShmLan {

constexpr uint32_t kMaxNodes = 128;
constexpr uint32_t kMcastChannels = 2;
constexpr uint32_t kMcastSlots = 64;
constexpr uint32_t kUnicastBuckets = 2;
constexpr uint32_t kUnicastSlots = 128;
constexpr uint32_t kMaxPayload = 1512;

constexpr uint32_t kMcastGroupDiscovery = 0xEFFF4D4Bu; // 239.255.77.75
constexpr uint32_t kMcastGroupData = 0xEFFF4D4Cu;      // 239.255.77.76

struct alignas(64) ShmPacketSlot {
    std::atomic<uint64_t> seq;
    uint32_t srcIp;
    uint16_t srcPort;
    uint16_t dstPort;
    uint16_t len;
    uint16_t pad;
    uint8_t data[kMaxPayload];
};

struct alignas(64) McastSenderRing {
    std::atomic<uint64_t> writeHead;
    uint8_t pad[56];
    ShmPacketSlot slots[kMcastSlots];
};

struct alignas(64) UnicastMailbox {
    std::atomic<uint64_t> writeHead;
    uint8_t pad[56];
    ShmPacketSlot slots[kUnicastSlots];
};

struct ShmArena {
    uint32_t magic;
    uint32_t version;
    // Compact directory (2 KB total) so TryRecvFrom/HasReadable never stride
    // across 12.25 MB of 96 KB sender rings on empty scans.
    alignas(64) std::atomic<uint64_t> mcastActiveMask[kMcastChannels][2];
    alignas(64) std::atomic<uint64_t> mcastWriteHead[kMcastChannels][kMaxNodes];
    McastSenderRing mcast[kMcastChannels][kMaxNodes];
    UnicastMailbox unicast[kMaxNodes][kUnicastBuckets];
};

inline bool IsEnabled() noexcept {
    return LanRuntime::Settings().localOnly;
}

inline uint32_t LocalSlotIndex() noexcept {
    const uint32_t inst = LanRuntime::Settings().instance;
    return (inst >= 1 && inst <= kMaxNodes) ? (inst - 1) : 0;
}

inline uint32_t LocalIpHostOrder() noexcept {
    return 0x7F000001u + LocalSlotIndex();
}

inline int8_t McastChannelForIp(uint32_t ipHostOrder) noexcept {
    if (ipHostOrder == kMcastGroupDiscovery) return 0;
    if (ipHostOrder == kMcastGroupData) return 1;
    return -1;
}

inline int UnicastSlotForIp(uint32_t ipHostOrder) noexcept {
    if ((ipHostOrder & 0xFFFFFF00u) != 0x7F000000u) return -1;
    const uint32_t host = ipHostOrder & 0xFFu;
    if (host < 1 || host > kMaxNodes) return -1;
    return static_cast<int>(host - 1);
}

inline int UnicastBucketForPort(uint16_t portHostOrder) noexcept {
    if (portHostOrder == 27900) return 0; // LAN broadcast/status unicast
    if (portHostOrder == 27910) return 1; // LAN direct data unicast
    return -1;                            // GT2 ephemeral ports stay on Winsock (deferred WSAPoll)
}

inline ShmArena* GetArena() noexcept {
    static ShmArena* s_arena = []() -> ShmArena* {
        if (!IsEnabled()) return nullptr;
        const uint64_t sz = sizeof(ShmArena);
        HANDLE mapping = CreateFileMappingW(
            INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
            static_cast<DWORD>(sz >> 32), static_cast<DWORD>(sz & 0xFFFFFFFFu),
            L"Local\\MKW_LAN_SHM_V2");
        if (!mapping) return nullptr;
        void* view = MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(ShmArena));
        if (!view) {
            CloseHandle(mapping);
            return nullptr;
        }
        auto* arena = static_cast<ShmArena*>(view);
        arena->magic = 0x4D4B5348u; // 'MKSH'
        arena->version = 1;
        return arena;
    }();
    return s_arena;
}

inline void OnBind(WiiSocket& s, const sockaddr_in& addr) noexcept {
    if (!IsEnabled() || s.type != SOCK_DGRAM) return;
    ShmArena* arena = GetArena();
    if (!arena) return;
    const uint32_t ip = ntohl(addr.sin_addr.s_addr);
    const uint16_t port = ntohs(addr.sin_port);
    s.shmBoundIp = (ip == 0) ? LocalIpHostOrder() : ip;
    s.shmBoundPort = port;
    if (s.shmBoundPort == 0) {
        sockaddr_in actual{};
        socklen_t len = sizeof(actual);
        if (getsockname(s.native, reinterpret_cast<sockaddr*>(&actual), &len) == 0) {
            s.shmBoundPort = ntohs(actual.sin_port);
        }
    }
    const int slot = UnicastSlotForIp(s.shmBoundIp);
    const int bucket = UnicastBucketForPort(s.shmBoundPort);
    if (slot >= 0 && bucket >= 0) {
        s.shmUnicastReadSeq = arena->unicast[slot][bucket].writeHead.load(std::memory_order_acquire);
        s.shmInitialized = true;
    }
}

inline void OnJoinMulticast(WiiSocket& s, uint32_t groupIpHostOrder) noexcept {
    if (!IsEnabled() || s.type != SOCK_DGRAM) return;
    const int8_t ch = McastChannelForIp(groupIpHostOrder);
    if (ch < 0) return;
    ShmArena* arena = GetArena();
    if (!arena) return;
    s.shmMcastChannel = ch;
    s.shmMcastScanCursor = 0;
    for (uint32_t i = 0; i < kMaxNodes; ++i) {
        s.shmMcastReadSeq[i] = arena->mcastWriteHead[ch][i].load(std::memory_order_acquire);
    }
    s.shmInitialized = true;
}

// Returns >= 0 (bytes sent) if handled via SHM, or -1 to fall through to Winsock.
inline int32_t TrySendTo(WiiSocket& s, const uint8_t* data, uint32_t size, const sockaddr_in* dest) noexcept {
    if (!IsEnabled() || s.type != SOCK_DGRAM || !dest || !data || size > kMaxPayload) {
        return -1;
    }
    ShmArena* arena = GetArena();
    if (!arena) return -1;

    const uint32_t dstIp = ntohl(dest->sin_addr.s_addr);
    const uint16_t dstPort = ntohs(dest->sin_port);
    const uint32_t srcIp = s.shmBoundIp ? s.shmBoundIp : LocalIpHostOrder();
    uint16_t srcPort = s.shmBoundPort;
    if (srcPort == 0) {
        sockaddr_in actual{};
        socklen_t len = sizeof(actual);
        if (getsockname(s.native, reinterpret_cast<sockaddr*>(&actual), &len) == 0) {
            srcPort = ntohs(actual.sin_port);
            s.shmBoundPort = srcPort;
        }
    }

    const int8_t mcastCh = McastChannelForIp(dstIp);
    if (mcastCh >= 0) {
        const uint32_t mySlot = LocalSlotIndex();
        McastSenderRing& ring = arena->mcast[mcastCh][mySlot];
        const uint64_t seq = arena->mcastWriteHead[mcastCh][mySlot].load(std::memory_order_relaxed);
        ShmPacketSlot& slot = ring.slots[seq & (kMcastSlots - 1)];
        slot.seq.store(0, std::memory_order_release); // mark in-progress
        slot.srcIp = srcIp;
        slot.srcPort = srcPort;
        slot.dstPort = dstPort;
        slot.len = static_cast<uint16_t>(size);
        if (size > 0) std::memcpy(slot.data, data, size);
        slot.seq.store(seq + 1, std::memory_order_release);
        arena->mcastWriteHead[mcastCh][mySlot].store(seq + 1, std::memory_order_release);
        const uint64_t bit = 1ull << (mySlot & 63u);
        auto& maskWord = arena->mcastActiveMask[mcastCh][mySlot >> 6];
        if ((maskWord.load(std::memory_order_relaxed) & bit) == 0) {
            maskWord.fetch_or(bit, std::memory_order_release);
        }
        return static_cast<int32_t>(size);
    }

    const int dstSlot = UnicastSlotForIp(dstIp);
    const int bucket = UnicastBucketForPort(dstPort);
    if (dstSlot >= 0 && bucket >= 0) {
        UnicastMailbox& mb = arena->unicast[dstSlot][bucket];
        const uint64_t seq = mb.writeHead.fetch_add(1, std::memory_order_acq_rel);
        ShmPacketSlot& slot = mb.slots[seq & (kUnicastSlots - 1)];
        slot.srcIp = srcIp;
        slot.srcPort = srcPort;
        slot.dstPort = dstPort;
        slot.len = static_cast<uint16_t>(size);
        if (size > 0) std::memcpy(slot.data, data, size);
        slot.seq.store(seq + 1, std::memory_order_release);
        return static_cast<int32_t>(size);
    }

    return -1; // e.g. 127.0.0.254 telemetry -> real Winsock
}

// Returns >= 0 (bytes read), -SO_EAGAIN if SHM socket is empty, or -1 to fall through to Winsock.
inline int32_t TryRecvFrom(WiiSocket& s, char* outData, uint32_t outCap, sockaddr_in* outFrom) noexcept {
    if (!IsEnabled() || s.type != SOCK_DGRAM || !s.shmInitialized) {
        return -1;
    }
    ShmArena* arena = GetArena();
    if (!arena) return -1;

    if (s.shmMcastChannel >= 0) {
        const int8_t ch = s.shmMcastChannel;
        const uint64_t mask0 = arena->mcastActiveMask[ch][0].load(std::memory_order_acquire);
        const uint64_t mask1 = arena->mcastActiveMask[ch][1].load(std::memory_order_acquire);
        if ((mask0 | mask1) == 0) {
            return -SO_EAGAIN;
        }
        const uint32_t start = s.shmMcastScanCursor;
        const auto* heads = arena->mcastWriteHead[ch];
        for (uint32_t step = 0; step < kMaxNodes; ++step) {
            const uint32_t idx = (start + step) & (kMaxNodes - 1);
            const uint64_t mask = (idx < 64) ? mask0 : mask1;
            if ((mask & (1ull << (idx & 63u))) == 0) continue;
            const uint64_t head = heads[idx].load(std::memory_order_acquire);
            uint64_t cur = s.shmMcastReadSeq[idx];
            if (cur >= head) continue;
            if (head - cur >= kMcastSlots) {
                cur = head - (kMcastSlots / 2);
            }
            const McastSenderRing& ring = arena->mcast[ch][idx];
            const ShmPacketSlot& slot = ring.slots[cur & (kMcastSlots - 1)];
            const uint64_t seq1 = slot.seq.load(std::memory_order_acquire);
            if (seq1 != cur + 1) {
                s.shmMcastReadSeq[idx] = head;
                continue;
            }
            const uint16_t len = slot.len;
            const uint32_t srcIp = slot.srcIp;
            const uint16_t srcPort = slot.srcPort;
            const uint32_t copyLen = std::min<uint32_t>(len, outCap);
            if (copyLen > 0) std::memcpy(outData, slot.data, copyLen);
            const uint64_t seq2 = slot.seq.load(std::memory_order_acquire);
            if (seq2 != seq1) {
                s.shmMcastReadSeq[idx] = heads[idx].load(std::memory_order_acquire);
                continue;
            }
            s.shmMcastReadSeq[idx] = cur + 1;
            s.shmMcastScanCursor = static_cast<uint8_t>((idx + 1) & (kMaxNodes - 1));
            if (outFrom) {
                std::memset(outFrom, 0, sizeof(*outFrom));
                outFrom->sin_family = AF_INET;
                outFrom->sin_addr.s_addr = htonl(srcIp);
                outFrom->sin_port = htons(srcPort);
            }
            return static_cast<int32_t>(copyLen);
        }
        return -SO_EAGAIN;
    }

    const int mySlot = UnicastSlotForIp(s.shmBoundIp);
    const int bucket = UnicastBucketForPort(s.shmBoundPort);
    if (mySlot >= 0 && bucket >= 0) {
        UnicastMailbox& mb = arena->unicast[mySlot][bucket];
        for (uint32_t attempts = 0; attempts < 16; ++attempts) {
            const uint64_t head = mb.writeHead.load(std::memory_order_acquire);
            uint64_t cur = s.shmUnicastReadSeq;
            if (cur >= head) break;
            if (head - cur >= kUnicastSlots) {
                cur = head - (kUnicastSlots / 2);
                s.shmUnicastReadSeq = cur;
            }
            const ShmPacketSlot& slot = mb.slots[cur & (kUnicastSlots - 1)];
            const uint64_t pub = slot.seq.load(std::memory_order_acquire);
            if (pub != cur + 1) {
                // Writer in progress on this ticket; retry next tick.
                break;
            }
            const uint16_t dstPort = slot.dstPort;
            const uint16_t len = slot.len;
            const uint32_t srcIp = slot.srcIp;
            const uint16_t srcPort = slot.srcPort;
            const uint32_t copyLen = std::min<uint32_t>(len, outCap);
            if (dstPort == s.shmBoundPort && copyLen > 0) {
                std::memcpy(outData, slot.data, copyLen);
            }
            const uint64_t pub2 = slot.seq.load(std::memory_order_acquire);
            if (pub2 != pub) {
                s.shmUnicastReadSeq = mb.writeHead.load(std::memory_order_acquire);
                continue;
            }
            s.shmUnicastReadSeq = cur + 1;
            if (dstPort != s.shmBoundPort) {
                continue;
            }
            if (outFrom) {
                std::memset(outFrom, 0, sizeof(*outFrom));
                outFrom->sin_family = AF_INET;
                outFrom->sin_addr.s_addr = htonl(srcIp);
                outFrom->sin_port = htons(srcPort);
            }
            return static_cast<int32_t>(copyLen);
        }
        return -SO_EAGAIN;
    }

    return -1;
}

inline bool HasReadable(const WiiSocket& s) noexcept {
    if (!IsEnabled() || s.type != SOCK_DGRAM || !s.shmInitialized) return false;
    ShmArena* arena = GetArena();
    if (!arena) return false;
    if (s.shmMcastChannel >= 0) {
        const int8_t ch = s.shmMcastChannel;
        const uint64_t mask0 = arena->mcastActiveMask[ch][0].load(std::memory_order_acquire);
        const uint64_t mask1 = arena->mcastActiveMask[ch][1].load(std::memory_order_acquire);
        const auto* heads = arena->mcastWriteHead[ch];
        uint64_t m = mask0;
        while (m != 0) {
            const uint32_t idx = static_cast<uint32_t>(__builtin_ctzll(m));
            m &= (m - 1);
            if (heads[idx].load(std::memory_order_acquire) > s.shmMcastReadSeq[idx]) {
                return true;
            }
        }
        m = mask1;
        while (m != 0) {
            const uint32_t idx = 64u + static_cast<uint32_t>(__builtin_ctzll(m));
            m &= (m - 1);
            if (heads[idx].load(std::memory_order_acquire) > s.shmMcastReadSeq[idx]) {
                return true;
            }
        }
        return false;
    }
    const int mySlot = UnicastSlotForIp(s.shmBoundIp);
    const int bucket = UnicastBucketForPort(s.shmBoundPort);
    if (mySlot >= 0 && bucket >= 0) {
        return arena->unicast[mySlot][bucket].writeHead.load(std::memory_order_acquire) > s.shmUnicastReadSeq;
    }
    return false;
}

} // namespace NetworkHle::ShmLan

#endif // _WIN32
