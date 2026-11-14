#ifndef LAN_DISCOVERY_CLOCK_H
#define LAN_DISCOVERY_CLOCK_H

#include <stdint.h>

/* 60 units per second at the Wii's 60,750,000 Hz timebase. Keep the
 * conversion freestanding: the PPC module has no compiler-rt __udivdi3. */
#define LAN_DISCOVERY_TICKS_PER_UNIT 1012500u
static inline uint64_t lan_discovery_time(uint64_t ticks) {
    const uint32_t upper = (uint32_t)(ticks >> 32);
    const uint32_t high = upper / LAN_DISCOVERY_TICKS_PER_UNIT;
    uint32_t remainder = upper % LAN_DISCOVERY_TICKS_PER_UNIT;
    uint32_t lower = (uint32_t)ticks;
    uint32_t low = 0;
    for (unsigned bit = 0; bit < 32; bit++) {
        remainder = (remainder << 1) | (lower >> 31);
        lower <<= 1;
        low <<= 1;
        if (remainder >= LAN_DISCOVERY_TICKS_PER_UNIT) {
            remainder -= LAN_DISCOVERY_TICKS_PER_UNIT;
            low |= 1;
        }
    }
    return ((uint64_t)high << 32) | low;
}

#endif
