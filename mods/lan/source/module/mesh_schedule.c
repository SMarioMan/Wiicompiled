#include "local_config.h"
#include "mkw.h"
#include "mesh_schedule.h"
#include <rvl/dwc.h>
#include <rvl/OSTime.h>

/* Only the two SDK mesh-deadline call sites use this shim. At each, r31 is
 * the candidate SDK node. Tail-calling the C helper preserves the caller's LR.
 * Do not replace the no-argument SDK function globally with an r31 assumption. */
__asm__(".section .text.LANMeshTryTick,\"ax\"\n"
        ".global LANMeshTryTick\n"
        "LANMeshTryTick:\n"
        "mr 3,31\n"
        "b LANMeshNextTick\n"
        ".previous\n");

uint64_t LANMeshNextTick(const uint32_t *peer) {
    struct dwc_gamedata *game = get_dwc_gamedata();
    if (!game || !peer || MKW_LAN_CAPACITY <= 12 || game->player_count <= 2 ||
        game->player_count > MKW_LAN_MATCH_NODES ||
        game->dwc_match_state != DWC_MATCH_STATE_CL_WAITING ||
        !peer[0] || peer[0] == game->my_pid)
        return DWCi_GetNextMeshMakeTryTick();
    /* SDK node +0x18 is the attempt counter tested at USA 800D80EC.
     * The existing SDK retains connection checks, retry limits and teardown. */
    const uint32_t delay = lan_mesh_delay_ms(game->my_pid, peer[0], peer[0x18/4]);
    /* The SDK compares this deadline as signed 64-bit time. Keep a passive
     * endpoint in the future without using the negative UINT64_MAX sentinel. */
    if (delay == LAN_MESH_PASSIVE_DELAY) return INT64_MAX;
    return OSGetTime() + (uint64_t)millisecs_to_ticks(delay);
}
