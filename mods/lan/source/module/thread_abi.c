#include <rvl/OSThread.h>

/* These are the SDK offsets used by OSCreateThread, OSLoadContext and the
 * checked runtime scheduler. Validate the actual 32-bit PowerPC ABI during
 * every module build, rather than a host's pointer/bitfield layout. */
_Static_assert(sizeof(void *) == 4, "LAN module requires 32-bit pointers");
_Static_assert(offsetof(OSThread_t, ctx_flags) == 0x1a2, "OSContext flags");
_Static_assert(offsetof(OSThread_t, gqr) == 0x1a4, "OSContext GQRs");
_Static_assert(offsetof(OSThread_t, state) == 0x2c8, "OSThread state");
_Static_assert(offsetof(OSThread_t, param_stack_size) == 0x2cc, "OSThread suspend count");
_Static_assert(offsetof(OSThread_t, param_priorty) == 0x2d0, "OSThread effective priority");
_Static_assert(offsetof(OSThread_t, priority) == 0x2d4, "OSThread base priority");
_Static_assert(offsetof(OSThread_t, sleep_queue) == 0x2dc, "OSThread queue");
_Static_assert(offsetof(OSThread_t, _errno) == 0x30c, "OSThread errno");
_Static_assert(sizeof(OSThread_t) == 0x318, "OSThread allocation size");
