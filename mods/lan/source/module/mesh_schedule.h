#ifndef MKW_LAN_MESH_SCHEDULE_H
#define MKW_LAN_MESH_SCHEDULE_H
#include <stdint.h>

/* Stable pair ownership avoids synchronized retries. The other endpoint can
 * still initiate after a grace period if the preferred endpoint is busy. */
static inline uint32_t lan_mesh_delay_ms(uint32_t local, uint32_t remote, uint32_t attempt) {
    const uint32_t low = local < remote ? local : remote;
    const uint32_t high = local < remote ? remote : local;
    const uint32_t mixed = low * 0x9e3779b1u ^ high * 0x85ebca77u;
    const uint32_t jitter = ((mixed ^ (mixed >> 16)) & 31u) * 4u;
    const uint32_t shift = attempt < 4 ? attempt : 4;
    uint32_t delay = (local < remote ? 125u : 625u) << shift;
    if (delay > 4000) delay = 4000;
    return delay + jitter;
}
#endif
