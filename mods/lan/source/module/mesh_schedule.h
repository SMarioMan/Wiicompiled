#ifndef MKW_LAN_MESH_SCHEDULE_H
#define MKW_LAN_MESH_SCHEDULE_H
#include <stdint.h>

/* Only the lower PID initiates a peer link. Every outstanding reservation
 * therefore points upward in PID order, so a cycle cannot form. The highest
 * remaining endpoint stays available to accept requests. An elapsed fallback
 * grace period must not reverse ownership and recreate crossed reservations. */
#define LAN_MESH_PASSIVE_DELAY UINT32_MAX
static inline uint32_t lan_mesh_delay_ms(uint32_t local, uint32_t remote, uint32_t attempt) {
    if (local >= remote) return LAN_MESH_PASSIVE_DELAY;
    const uint32_t low = local < remote ? local : remote;
    const uint32_t high = local < remote ? remote : local;
    const uint32_t mixed = low * 0x9e3779b1u ^ high * 0x85ebca77u;
    const uint32_t jitter = ((mixed ^ (mixed >> 16)) & 31u) * 4u;
    const uint32_t shift = attempt < 4 ? attempt : 4;
    return (125u << shift) + jitter;
}
#endif
