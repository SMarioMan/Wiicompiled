#include "abi_bridge.h"
#include "guest_function_imports.h"
#include "guest_abi/layout_access_audit.h"

namespace {
void WatchConstruction(CpuContext* ctx, GuestFunctions::Entry next, const char* type, const char* lifetime) {
#ifdef MKW_ENABLE_LAYOUT_ACCESS_AUDIT
    uint64_t generation = 0;
    if (LayoutAccessAudit::enabled.load(std::memory_order_relaxed) &&
        LayoutAccessAudit::HasType(type)) {
        generation = LayoutAccessAudit::RegisterInstance(type, ctx->gpr[3], 0, lifetime);
    }
    try {
        // Resource loading can fail before the first race-progress checkpoint.
        // Publish the watch before entering guest construction and refresh it
        // after success, while no auditor lock is held.
        if (generation) LayoutAccessAudit::WriteReport();
        next(ctx);
        if (generation) LayoutAccessAudit::WriteReport();
    } catch (...) {
        if (generation) LayoutAccessAudit::RetireInstance(generation);
        throw;
    }
#else
    next(ctx);
#endif
}

void WatchDestruction(CpuContext* ctx, GuestFunctions::Entry next) {
#ifdef MKW_ENABLE_LAYOUT_ACCESS_AUDIT
    const auto base = ctx->gpr[3];
    try {
        next(ctx);
    } catch (...) {
        LayoutAccessAudit::RetireAllocation(base);
        throw;
    }
    // Includes non-deleting destructors; release may already have retired it.
    LayoutAccessAudit::RetireAllocation(base);
#else
    next(ctx);
#endif
}
}

// PAL function identities map through the existing region table. The equivalent
// verified NTSC-U entries are 8053A7B0, 8053A9E0 and 80229A90. Only diagnostic
// state changes here: the winning guest translation still owns construction,
// destruction, allocation and release.
extern "C" void ArchiveManager_AuditConstruct(CpuContext* ctx) {
    WatchConstruction(ctx, [](CpuContext* guest) { MKW_GUEST_NEXT(8053FCEC)(guest); },
                      "ArchiveManager", "ArchiveMgr constructor/destructor");
}
REGISTER_NATIVE_WRAPPER_AS(0x8053FCEC, ArchiveManager_AuditConstruct,
                           "ArchiveManager_AuditConstruct");

extern "C" void ArchiveManager_AuditDestroy(CpuContext* ctx) {
    WatchDestruction(ctx, [](CpuContext* guest) { MKW_GUEST_NEXT(8053FF1C)(guest); });
}
REGISTER_NATIVE_WRAPPER_AS(0x8053FF1C, ArchiveManager_AuditDestroy,
                           "ArchiveManager_AuditDestroy");

// Verified NTSC-U AI::Manager constructor/destructor: 8072B8AC / 8072BA84.
// The constructor initializes the two capacity-aware controller arrays and
// trailing subordinate pointers. Watch before that work and through teardown.
extern "C" void AIManager_AuditConstruct(CpuContext* ctx) {
    WatchConstruction(ctx, [](CpuContext* guest) { MKW_GUEST_NEXT(80738ED4)(guest); },
                      "AIManager", "AI::Manager constructor/destructor");
}
REGISTER_NATIVE_WRAPPER_AS(0x80738ED4, AIManager_AuditConstruct,
                           "AIManager_AuditConstruct");

extern "C" void AIManager_AuditDestroy(CpuContext* ctx) {
    WatchDestruction(ctx, [](CpuContext* guest) { MKW_GUEST_NEXT(807390AC)(guest); });
}
REGISTER_NATIVE_WRAPPER_AS(0x807390AC, AIManager_AuditDestroy,
                           "AIManager_AuditDestroy");

// Verified NTSC-U Item::Manager constructor/destructor: 807902CC / 80790664.
// The singleton factory calls the constructor before publishing its pointer.
extern "C" void ItemManager_AuditConstruct(CpuContext* ctx) {
    WatchConstruction(ctx, [](CpuContext* guest) { MKW_GUEST_NEXT(807992D8)(guest); },
                      "ItemManager", "Item::Manager constructor/destructor");
}
REGISTER_NATIVE_WRAPPER_AS(0x807992D8, ItemManager_AuditConstruct,
                           "ItemManager_AuditConstruct");

extern "C" void ItemManager_AuditDestroy(CpuContext* ctx) {
    WatchDestruction(ctx, [](CpuContext* guest) { MKW_GUEST_NEXT(80799670)(guest); });
}
REGISTER_NATIVE_WRAPPER_AS(0x80799670, ItemManager_AuditDestroy,
                           "ItemManager_AuditDestroy");

extern "C" void GuestDelete_AuditRetirement(CpuContext* ctx) {
#ifdef MKW_ENABLE_LAYOUT_ACCESS_AUDIT
    // Retire before the allocator can hand this address to another object.
    LayoutAccessAudit::RetireAllocation(ctx->gpr[3]);
#endif
    MKW_GUEST_NEXT(80229E14)(ctx);
}
REGISTER_NATIVE_WRAPPER_AS(0x80229E14, GuestDelete_AuditRetirement,
                           "GuestDelete_AuditRetirement");

// The parent normally inlines construction. The update wrapper enrolls those
// objects at first use; standalone construction still receives a pre-call watch.
// Verified NTSC-U constructor/destructor: 8072535C / 8072537C.
extern "C" void AIBattleTargetSelector_AuditConstruct(CpuContext* ctx) {
    WatchConstruction(ctx, [](CpuContext* guest) { MKW_GUEST_NEXT(8072C548)(guest); },
                      "AIBattleTargetSelector", "battle target constructor/destructor");
}
REGISTER_NATIVE_WRAPPER_AS(0x8072C548,AIBattleTargetSelector_AuditConstruct,
                           "AIBattleTargetSelector_AuditConstruct");

extern "C" void AIBattleTargetSelector_AuditDestroy(CpuContext* ctx) {
    WatchDestruction(ctx, [](CpuContext* guest) { MKW_GUEST_NEXT(8072C568)(guest); });
}
REGISTER_NATIVE_WRAPPER_AS(0x8072C568,AIBattleTargetSelector_AuditDestroy,
                           "AIBattleTargetSelector_AuditDestroy");

// Verified NTSC-U DriverMgr constructor/destructor: 80783A50 / 80783E18.
// The factory calls construction before publishing the singleton; keep the
// original model, timer, ranking and trailing-field lifetimes under observation.
extern "C" void DriverManager_AuditConstruct(CpuContext* ctx) {
    WatchConstruction(ctx, [](CpuContext* guest) { MKW_GUEST_NEXT(8078CA5C)(guest); },
                      "DriverManager", "DriverMgr constructor/destructor");
}
REGISTER_NATIVE_WRAPPER_AS(0x8078CA5C,DriverManager_AuditConstruct,
                           "DriverManager_AuditConstruct");

extern "C" void DriverManager_AuditDestroy(CpuContext* ctx) {
    WatchDestruction(ctx, [](CpuContext* guest) { MKW_GUEST_NEXT(8078CE24)(guest); });
}
REGISTER_NATIVE_WRAPPER_AS(0x8078CE24,DriverManager_AuditDestroy,
                           "DriverManager_AuditDestroy");

// Verified NTSC-U balloon manager constructor/destructor: 80865454 / 80865568.
// The ordinary scene release can delete the singleton directly; the existing
// global-delete hook retires that path before its storage can be reused.
extern "C" void BattleBalloonManager_AuditConstruct(CpuContext* ctx) {
    WatchConstruction(ctx, [](CpuContext* guest) { MKW_GUEST_NEXT(80869884)(guest); },
                      "BattleBalloonManager", "balloon manager constructor/destructor");
}
REGISTER_NATIVE_WRAPPER_AS(0x80869884,BattleBalloonManager_AuditConstruct,
                           "BattleBalloonManager_AuditConstruct");

extern "C" void BattleBalloonManager_AuditDestroy(CpuContext* ctx) {
    WatchDestruction(ctx, [](CpuContext* guest) { MKW_GUEST_NEXT(80869998)(guest); });
}
REGISTER_NATIVE_WRAPPER_AS(0x80869998,BattleBalloonManager_AuditDestroy,
                           "BattleBalloonManager_AuditDestroy");

// Verified NTSC-U Input::Manager constructor/destructor: 8051EE7C / 8051ED68.
// This process-lifetime object owns the expanded virtual controller holders and
// shifted hardware/dummy-controller fields used by native input consumers too.
extern "C" void InputManager_AuditConstruct(CpuContext* ctx) {
    WatchConstruction(ctx, [](CpuContext* guest) { MKW_GUEST_NEXT(805232F0)(guest); },
                      "InputManager", "Input::Manager constructor/destructor");
}
REGISTER_NATIVE_WRAPPER_AS(0x805232F0,InputManager_AuditConstruct,
                           "InputManager_AuditConstruct");

extern "C" void InputManager_AuditDestroy(CpuContext* ctx) {
    WatchDestruction(ctx, [](CpuContext* guest) { MKW_GUEST_NEXT(805231DC)(guest); });
}
REGISTER_NATIVE_WRAPPER_AS(0x805231DC,InputManager_AuditDestroy,
                           "InputManager_AuditDestroy");
