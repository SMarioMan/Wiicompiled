#pragma once

#include <string_view>
#include <cstdint>

namespace RuntimeProduct {

enum class Kind {
    BaseGame,
    RetroRewind,
    Lan,
};

struct Descriptor {
    Kind kind;
    std::string_view displayName;
    uint32_t lanConfigAddress = 0;
};

// Each public executable links exactly one small provider definition. Keeping
// this selection out of target-wide preprocessor definitions lets the native
// runtime be compiled once and shared by every product.
const Descriptor& Active() noexcept;

inline bool IsRetroRewind() noexcept {
    return Active().kind == Kind::RetroRewind;
}

} // namespace RuntimeProduct
