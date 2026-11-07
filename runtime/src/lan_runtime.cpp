#include "lan_runtime.h"
#include "hle/net/network_internal.h"
#include "memory.h"
#include "runtime_log.h"
#include "runtime_product.h"
#include "mkw_guest_layout.h"

namespace LanRuntime {
namespace { Options options{MkwGuestLayout::kLayout.playerCapacity}; }
const Options& Settings() { return options; }
bool ParseArgument(std::string_view argument) {
    return RuntimeProduct::Active().kind == RuntimeProduct::Kind::Lan && options.Parse(argument);
}
void Validate() {
    if (RuntimeProduct::Active().kind == RuntimeProduct::Kind::Lan) options.Validate();
}
void FinishAutomation() {
    const auto& product = RuntimeProduct::Active();
    if (product.kind == RuntimeProduct::Kind::Lan && product.lanConfigAddress)
        Memory::Write32(product.lanConfigAddress+16, 0);
}
void InitializeGuest() {
    const auto& product = RuntimeProduct::Active();
    if (product.kind != RuntimeProduct::Kind::Lan) return;
    const auto address = product.lanConfigAddress;
    if (!address || Memory::Read32(address) != 0x4d4b5743 || Memory::Read32(address+4) != 2 ||
        Memory::Read32(address+28) != options.capacity ||
        Memory::Read32(address+32) != MkwGuestLayout::kLayout.inputGCNControllers ||
        Memory::Read32(address+36) != MkwGuestLayout::kLayout.racedataRace + MkwGuestLayout::kLayout.scenarioSettings ||
        Memory::Read32(address+40) != MkwGuestLayout::kLayout.racedataRace + MkwGuestLayout::kLayout.scenarioSettings + 8 ||
        Memory::Read32(address+44) != MkwGuestLayout::kLayout.racedataRace + 4)
        throw std::runtime_error("The linked LAN module has no runtime configuration matching this guest layout");
    options.Validate();
    Memory::Write32(address+8, options.instance);
    Memory::Write32(address+12, options.instances);
    Memory::Write32(address+16, static_cast<uint32_t>(options.automation));
    uint32_t bindAddress = options.bindAddress;
    if (!bindAddress) {
        if (options.localOnly) bindAddress = 0x7f000000 | options.instance;
        else {
            in_addr fallback{}; fallback.s_addr = htonl(0x7f000001);
            bindAddress = ntohl(NetworkHle::ResolveDefaultInterfaceIp(fallback).s_addr);
        }
    }
    Memory::Write32(address+20, bindAddress);
    Memory::Write32(address+24, static_cast<uint32_t>(options.peers.size()));
    for (size_t i = 0; i < options.peers.size(); i++) Memory::Write32(address+48+uint32_t(i)*4, options.peers[i]);
    RT_LOG(RT_TAG_MOD) << "LAN runtime instance " << options.instance << ", automation "
        << static_cast<uint32_t>(options.automation) << std::endl;
}
} // namespace LanRuntime
