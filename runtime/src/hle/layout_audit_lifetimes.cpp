#include "abi_bridge.h"
#include "guest_function_imports.h"
#include "guest_abi/layout_access_audit.h"

// PAL function identities map through the existing region table. The equivalent
// verified NTSC-U entries are 8053A7B0, 8053A9E0 and 80229A90. Only diagnostic
// state changes here: the winning guest translation still owns construction,
// destruction, allocation and release.
extern "C" void ArchiveManager_AuditConstruct(CpuContext* ctx) {
#ifdef MKW_ENABLE_LAYOUT_ACCESS_AUDIT
    uint64_t generation = 0;
    if (LayoutAccessAudit::enabled.load(std::memory_order_relaxed) &&
        LayoutAccessAudit::HasType("ArchiveManager")) {
        generation = LayoutAccessAudit::RegisterInstance("ArchiveManager", ctx->gpr[3], 0,
                                                         "ArchiveMgr constructor/destructor");
    }
    try {
        MKW_GUEST_NEXT(8053FCEC)(ctx);
    } catch (...) {
        if (generation) LayoutAccessAudit::RetireInstance(generation);
        throw;
    }
#else
    MKW_GUEST_NEXT(8053FCEC)(ctx);
#endif
}
REGISTER_NATIVE_WRAPPER_AS(0x8053FCEC, ArchiveManager_AuditConstruct,
                           "ArchiveManager_AuditConstruct");

extern "C" void ArchiveManager_AuditDestroy(CpuContext* ctx) {
#ifdef MKW_ENABLE_LAYOUT_ACCESS_AUDIT
    const auto base = ctx->gpr[3];
    try {
        MKW_GUEST_NEXT(8053FF1C)(ctx);
    } catch (...) {
        LayoutAccessAudit::RetireAllocation(base);
        throw;
    }
    // Includes non-deleting destructors; release may already have retired it.
    LayoutAccessAudit::RetireAllocation(base);
#else
    MKW_GUEST_NEXT(8053FF1C)(ctx);
#endif
}
REGISTER_NATIVE_WRAPPER_AS(0x8053FF1C, ArchiveManager_AuditDestroy,
                           "ArchiveManager_AuditDestroy");

extern "C" void GuestDelete_AuditRetirement(CpuContext* ctx) {
#ifdef MKW_ENABLE_LAYOUT_ACCESS_AUDIT
    // Retire before the allocator can hand this address to another object.
    LayoutAccessAudit::RetireAllocation(ctx->gpr[3]);
#endif
    MKW_GUEST_NEXT(80229E14)(ctx);
}
REGISTER_NATIVE_WRAPPER_AS(0x80229E14, GuestDelete_AuditRetirement,
                           "GuestDelete_AuditRetirement");
