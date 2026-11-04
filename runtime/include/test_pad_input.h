#pragma once

#include <cstdint>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <optional>

namespace TestPadInput {
// Opt-in integration-test input. The driver atomically replaces a regular file
// containing a big-endian button mask and optional signed stick X/Y bytes.
// A two-byte file releases the stick; an all-zero record releases all controls.
struct State { uint16_t buttons; int8_t stickX = 0; int8_t stickY = 0; };

inline std::optional<State> Read(const std::filesystem::path& path) {
    std::error_code error;
    if (!std::filesystem::is_regular_file(path, error)) return std::nullopt;
    std::ifstream input(path, std::ios::binary);
    unsigned char bytes[2];
    if (!input.read(reinterpret_cast<char*>(bytes), sizeof(bytes))) return std::nullopt;
    State state{static_cast<uint16_t>((static_cast<uint16_t>(bytes[0]) << 8) | bytes[1])};
    const int x = input.get();
    if (x == std::char_traits<char>::eof()) return state;
    const int y = input.get();
    if (y == std::char_traits<char>::eof()) return std::nullopt;
    state.stickX = static_cast<int8_t>(x < 128 ? x : x - 256);
    state.stickY = static_cast<int8_t>(y < 128 ? y : y - 256);
    return state;
}
inline std::optional<State> ReadConfigured() {
    static const char* path = std::getenv("WIICOMPILED_TEST_PAD_FILE");
    if (!path || !*path) return std::nullopt;
    return Read(std::filesystem::u8path(path));
}
} // namespace TestPadInput
