#pragma once
#include <cstdint>
#include <span>
#if defined(_WIN32)
#include <winsock2.h>
#include <ws2tcpip.h>
#else
#include <sys/socket.h>
#include <netinet/in.h>
#endif

namespace SocketOptionContract {
inline int Level(uint32_t level) { return level == 0xffff ? SOL_SOCKET : static_cast<int>(level); }
inline int Name(uint32_t level, uint32_t name) {
    if (level == 0) {
        switch (name) {
        case 9: return IP_MULTICAST_IF;
        case 10: return IP_MULTICAST_TTL;
        case 11: return IP_MULTICAST_LOOP;
        case 12: return IP_ADD_MEMBERSHIP;
        case 13: return IP_DROP_MEMBERSHIP;
        default: return static_cast<int>(name);
        }
    }
    if (level == 0xffff) {
        switch (name) {
        case 0x4: return SO_REUSEADDR;
        case 0x80: return SO_LINGER;
        case 0x100: return SO_OOBINLINE;
        case 0x1001: return SO_SNDBUF;
        case 0x1002: return SO_RCVBUF;
        case 0x1008: return SO_TYPE;
        case 0x1009: return SO_ERROR;
        default: return static_cast<int>(name);
        }
    }
    return static_cast<int>(name);
}
inline bool IsInteger(uint32_t level, uint32_t name) {
    if (level == 0) return name == 10 || name == 11;
    if (level == 0xffff) return name == 4 || name == 0x100 ||
        name == 0x1001 || name == 0x1002 || name == 0x1008 || name == 0x1009;
    return level == 6 && name == 1; // TCP_NODELAY
}
inline bool Integer(std::span<const uint8_t> bytes, int& value) {
    if (bytes.size() == 1) { value = bytes[0]; return true; }
    if (bytes.size() != 4) return false;
    value = static_cast<int32_t>((uint32_t(bytes[0]) << 24) | (uint32_t(bytes[1]) << 16) |
        (uint32_t(bytes[2]) << 8) | bytes[3]);
    return true;
}
} // namespace SocketOptionContract
