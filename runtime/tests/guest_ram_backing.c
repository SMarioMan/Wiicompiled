#define _GNU_SOURCE
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <unistd.h>

/* Test-only backing for observing an owned game's RAM without ptrace.
 * The game still owns its ftruncate, shared aliases and page protections. */
int memfd_create(const char *name, unsigned int flags) {
    const char *directory = getenv("WIICOMPILED_TEST_RAM_DIR");
    if (!directory || strcmp(name, "wiicompiled-guest-ram")) {
        int (*original)(const char *, unsigned int) = dlsym(RTLD_NEXT, "memfd_create");
        if (!original) { errno = ENOSYS; return -1; }
        return original(name, flags);
    }
    static _Atomic unsigned int next_id;
    char path[PATH_MAX];
    const int length = snprintf(path, sizeof(path), "%s/ram-%u.bin", directory, atomic_fetch_add(&next_id, 1));
    if (length < 0 || (size_t)length >= sizeof(path)) { errno = ENAMETOOLONG; return -1; }
    return open(path, O_RDWR | O_CREAT | O_EXCL | ((flags & MFD_CLOEXEC) ? O_CLOEXEC : 0), 0600);
}
