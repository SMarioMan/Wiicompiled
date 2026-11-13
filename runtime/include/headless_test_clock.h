#pragma once

#include <atomic>
#include <charconv>
#include <cstdint>
#include <stdexcept>
#include <string_view>

// Opt-in support for functional multi-instance tests on constrained hosts.
// This does not establish default-timing or real-time network performance.
namespace HeadlessTestClock {
inline std::atomic<uint32_t> divisor{1};
inline uint32_t Configure(const char* value, bool headless) {
    if (!value) { divisor.store(1,std::memory_order_relaxed);return 1; }
    const std::string_view text(value);
    uint32_t parsed=0;
    const auto [end,error]=std::from_chars(text.data(),text.data()+text.size(),parsed);
    if (!headless || text.empty() || error!=std::errc{} || end!=text.data()+text.size() || parsed<1 || parsed>64)
        throw std::invalid_argument("MKW_TEST_CLOCK_DIVISOR requires --headless and an integer from 1 to 64");
    divisor.store(parsed,std::memory_order_relaxed);
    return parsed;
}
inline uint64_t Nanoseconds(uint64_t elapsed) {
    const auto value=divisor.load(std::memory_order_relaxed);
    return value==1 ? elapsed : elapsed/value;
}
inline uint32_t DeltaMicros(uint32_t elapsed) {
    const auto value=divisor.load(std::memory_order_relaxed);
    if(value==1)return elapsed;
    static thread_local uint32_t remainder=0;
    static thread_local uint32_t previous=1;
    if(previous!=value){remainder=0;previous=value;}
    const uint64_t total=uint64_t(elapsed)+remainder;
    remainder=total%value;
    return static_cast<uint32_t>(total/value);
}
}
