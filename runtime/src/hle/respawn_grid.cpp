#include "abi_bridge.h"
#include "guest_function_imports.h"
#include "memory.h"
#include "mkw_guest_layout.h"
#include "starting_grid.h"

#include <bit>

// JGPT's original placement helper indexes twelve signed-byte offset pairs by
// player ID. Beyond twelve it reads adjacent constants as offsets, sometimes
// dropping a kart far outside the road on every rescue.
extern "C" void JGPT_ExpandedRespawnGrid(CpuContext* ctx) {
    const auto saved=*ctx;
    const auto& layout=MkwGuestLayout::kLayout;
    const auto data=Memory::Read32(MKW_GADDR(809BD728));
    const unsigned count=data ? Memory::Read8(data+layout.racedataRace+4) : 0;
    const unsigned index=saved.gpr[5];
    if(count<=12 || count>layout.playerCapacity || index>=count) {
        MKW_GUEST_NEXT(8051896C)(ctx);return;
    }
    const auto output=saved.gpr[4];
    std::array<StartingGrid::Vec,12> authored;
    for(unsigned i=0;i<authored.size();i++) {
        *ctx=saved;ctx->gpr[5]=i;MKW_GUEST_NEXT(8051896C)(ctx);
        for(unsigned axis=0;axis<3;axis++)authored[i][axis]=std::bit_cast<float>(Memory::Read32(output+axis*4));
    }
    // Battle modes deliberately use the exact JGPT center for every racer.
    if(authored[0]==authored[1]) { *ctx=saved;MKW_GUEST_NEXT(8051896C)(ctx);return; }
    // JGPT has four racers per row; the shared packing helper expects its
    // fourth sample to establish the second axis of the authored footprint.
    std::swap(authored[3],authored[4]);
    static thread_local std::array<StartingGrid::Vec,12> previous{};
    static thread_local std::vector<StartingGrid::Vec> positions;
    if(positions.size()!=count || previous!=authored) {
        positions=StartingGrid::Pack(authored,count);previous=authored;
    }
    for(unsigned axis=0;axis<3;axis++)Memory::Write32(output+axis*4,std::bit_cast<uint32_t>(positions[index][axis]));
    *ctx=saved;
}
REGISTER_NATIVE_WRAPPER_AS(0x8051896C, JGPT_ExpandedRespawnGrid, "JGPT_ExpandedRespawnGrid");
