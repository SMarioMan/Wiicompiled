#pragma once
#include <charconv>
#include <cstdint>
#include <stdexcept>
#include <string_view>
#include <vector>

namespace LanRuntime {
enum class Automation : uint32_t { Manual, Host, Join };
struct Options {
    uint32_t instance = 1;
    uint32_t instances = 12;
    Automation automation = Automation::Manual;
    uint32_t bindAddress = 0;
    bool localOnly = false;
    std::vector<uint32_t> peers;

    static uint32_t Number(std::string_view value, uint32_t maximum) {
        uint32_t result = 0;
        const auto [end, error] = std::from_chars(value.data(), value.data()+value.size(), result);
        if (value.empty() || error != std::errc{} || end != value.data()+value.size() || !result || result > maximum)
            throw std::invalid_argument("LAN IDs/counts must be between 1 and 12");
        return result;
    }
    static uint32_t Address(std::string_view value) {
        uint32_t result = 0;
        for (unsigned octet = 0; octet < 4; octet++) {
            const auto dot = value.find('.');
            const auto part = value.substr(0, dot);
            unsigned number = 0;
            const auto [end, error] = std::from_chars(part.data(), part.data()+part.size(), number);
            if (part.empty() || error != std::errc{} || end != part.data()+part.size() || number > 255 ||
                ((octet == 3) != (dot == std::string_view::npos)))
                throw std::invalid_argument("LAN addresses require four IPv4 octets");
            result = (result << 8) | number;
            if (dot != std::string_view::npos) value.remove_prefix(dot+1);
        }
        if (!result || result == 0xffffffff || (result >> 28) >= 14)
            throw std::invalid_argument("LAN addresses must be unicast IPv4 addresses");
        return result;
    }
    bool Parse(std::string_view argument) {
        if (argument == "--lan-host" || argument == "--lan-join") {
            const auto selected = argument == "--lan-host" ? Automation::Host : Automation::Join;
            if (automation != Automation::Manual && automation != selected)
                throw std::invalid_argument("Choose only one LAN automation role");
            automation = selected;
        } else if (argument == "--lan-local") localOnly = true;
        else if (argument.starts_with("--lan-id=")) instance = Number(argument.substr(9), 12);
        else if (argument.starts_with("--lan-peers=")) instances = Number(argument.substr(12), 12);
        else if (argument.starts_with("--lan-bind=")) bindAddress = Address(argument.substr(11));
        else if (argument.starts_with("--lan-peer=")) {
            if (peers.size() == 12) throw std::invalid_argument("The current LAN layout supports twelve peers");
            peers.push_back(Address(argument.substr(11)));
        } else return false;
        return true;
    }
    void Validate() const {
        if (instance > instances) throw std::invalid_argument("--lan-id must not exceed --lan-peers");
        if (bindAddress && localOnly)
            throw std::invalid_argument("Choose --lan-local or --lan-bind, not both");
    }
};
} // namespace LanRuntime
