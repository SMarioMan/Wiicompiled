#include "abi_bridge.h"
#include "guest_function_imports.h"
#include "guest_abi/layout_access_audit.h"
#include "memory.h"
#include "mkw_guest_layout.h"
#include <algorithm>
#include <array>
#include <vector>

namespace {
uint32_t Field(uint32_t address, const char* field, uint32_t pc = MKW_GADDR(8072C740)) {
    if (LayoutAccessAudit::enabled.load(std::memory_order_relaxed)) {
        LayoutAccessAudit::ScopedSite site({.pc=pc,
            .type="AIBattleTargetSelector",.field=field,.rule="native:expanded battle target lookup",.native=true});
        return Memory::Read32(address);
    }
    return Memory::Read32(address);
}
}

// USA 80725554 unrolls six opponent/state pairs. Preserve its first non-null
// matching target and null fallback using the generated inline-array layout.
extern "C" void BattleAI_FirstMatchingTarget(CpuContext* ctx) {
    if (GuestRegion::Current() != GuestRegion::Id::NtscU || MkwGuestLayout::kLayout.playerCapacity <= 12) {
        MKW_GUEST_NEXT(8072C740)(ctx);return;
    }
    const auto base=ctx->gpr[3], wanted=ctx->gpr[4];
    const auto& layout=MkwGuestLayout::kBattleTargets;
    for (uint32_t index=0;index<layout.capacity;++index) {
        const auto target=Field(base+layout.targets+index*4,"targets");
        if (target && Field(base+layout.states+index*4,"states")==wanted) {
            ctx->gpr[3]=target;return;
        }
    }
    ctx->gpr[3]=0;
}
REGISTER_NATIVE_WRAPPER_AS(0x8072C740, BattleAI_FirstMatchingTarget,
                           "BattleAI_FirstMatchingTarget");


// The behavior selectors inline their six-way lookups. After the original
// distance/angle classifier has visited the full relocated roster, promote the
// first non-null target in each of its five fixed state classes into that prefix.
// Paired target/state values and every remaining target stay in the same guest
// arrays. This preserves each current classification's first matching result;
// expanded AI subsequently retains that cache ordering between updates.
extern "C" void BattleAI_UpdateTargets(CpuContext* ctx) {
    const auto base=ctx->gpr[3];
    MKW_GUEST_NEXT(8072C5E8)(ctx);
    if (GuestRegion::Current()!=GuestRegion::Id::NtscU || MkwGuestLayout::kLayout.playerCapacity<=12) return;
    const auto& layout=MkwGuestLayout::kBattleTargets;
    struct Slot {uint32_t target,state;bool first;};
    std::vector<Slot> slots;slots.reserve(layout.capacity);
    std::array<bool,5> seen{};
    const auto pc=MKW_GADDR(8072C5E8);
    for(uint32_t i=0;i<layout.capacity;++i){
        const auto target=Field(base+layout.targets+i*4,"targets",pc);
        const auto state=Field(base+layout.states+i*4,"states",pc);
        const bool first=target && state<seen.size() && !seen[state];
        if(first)seen[state]=true;
        slots.push_back({target,state,first});
    }
    // No writes when the prefix already contains the state representatives.
    if(std::is_partitioned(slots.begin(),slots.end(),[](const Slot& slot){return slot.first;})) return;
    std::stable_partition(slots.begin(),slots.end(),[](const Slot& slot){return slot.first;});
    for(uint32_t i=0;i<layout.capacity;++i){
        if(LayoutAccessAudit::enabled.load(std::memory_order_relaxed)){
            {LayoutAccessAudit::ScopedSite site({.pc=pc,.type="AIBattleTargetSelector",.field="targets",
                .rule="native:battle target state prefix",.native=true});Memory::Write32(base+layout.targets+i*4,slots[i].target);}
            {LayoutAccessAudit::ScopedSite site({.pc=pc,.type="AIBattleTargetSelector",.field="states",
                .rule="native:battle target state prefix",.native=true});Memory::Write32(base+layout.states+i*4,slots[i].state);}
        }else{
            Memory::Write32(base+layout.targets+i*4,slots[i].target);
            Memory::Write32(base+layout.states+i*4,slots[i].state);
        }
    }
}
REGISTER_NATIVE_WRAPPER_AS(0x8072C5E8, BattleAI_UpdateTargets,"BattleAI_UpdateTargets");
