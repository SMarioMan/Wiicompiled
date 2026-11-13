#include "abi_bridge.h"
#include "guest_function_imports.h"
#include "memory.h"
#include "starting_grid.h"

#include <bit>

extern "C" void KTPT_ExpandedStartingGrid(CpuContext* ctx) {
    const auto saved=*ctx;
    const unsigned index=saved.gpr[6],count=saved.gpr[7];
    if(count<=12 || index>=count) { MKW_GUEST_NEXT(80514368)(ctx);return; }
    const uint32_t output=saved.gpr[4];
    std::array<StartingGrid::Vec,12> authored;
    for(unsigned i=0;i<authored.size();i++) {
        *ctx=saved;ctx->gpr[6]=i;ctx->gpr[7]=12;
        MKW_GUEST_NEXT(80514368)(ctx);
        for(unsigned axis=0;axis<3;axis++)authored[i][axis]=std::bit_cast<float>(Memory::Read32(output+axis*4));
    }
    // Reuse the plan while this course/start transform and active field match.
    static thread_local std::array<StartingGrid::Vec,12> previous{};
    static thread_local std::vector<StartingGrid::Vec> positions;
    if(positions.size()!=count || previous!=authored) { positions=StartingGrid::Pack(authored,count);previous=authored; }
    for(unsigned axis=0;axis<3;axis++)Memory::Write32(output+axis*4,std::bit_cast<uint32_t>(positions[index][axis]));
    *ctx=saved;
}
REGISTER_NATIVE_WRAPPER_AS(0x80514368, KTPT_ExpandedStartingGrid, "KTPT_ExpandedStartingGrid");
