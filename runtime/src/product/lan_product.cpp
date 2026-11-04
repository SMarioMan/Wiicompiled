#include "runtime_product.h"

#include "lan_runtime_config.h"

namespace RuntimeProduct {

const Descriptor& Active() noexcept {
    static constexpr Descriptor descriptor{
        Kind::Lan,
        "WiiCompiled LAN",
        kLanConfigGuestAddress,
    };
    return descriptor;
}

} // namespace RuntimeProduct
