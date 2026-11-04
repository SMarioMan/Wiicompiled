#include "lan_options.h"
#include <cstdlib>
#include <iostream>

void Check(bool value) { if (!value) { std::cerr << "LAN option contract failed\n"; std::exit(1); } }
template<class F> void Invalid(F action) {
    try { action(); } catch (const std::invalid_argument&) { return; }
    Check(false);
}
int main() {
    using namespace LanRuntime;
    Options manual;
    Check(manual.automation == Automation::Manual);
    Check(manual.Parse("--lan-id=2"));
    Check(manual.automation == Automation::Manual && manual.instance == 2);
    Check(manual.Parse("--lan-host") && manual.automation == Automation::Host);
    Invalid([&] { manual.Parse("--lan-join"); });
    Options join;
    Check(join.Parse("--lan-id=1") && join.Parse("--lan-join"));
    Check(join.instance == 1 && join.automation == Automation::Join);
    Check(!join.Parse("--not-an-option"));
    Invalid([&] { join.Parse("--lan-id=0"); });
    Invalid([&] { join.Parse("--lan-peers=13"); });
    Invalid([&] { join.Parse("--lan-id=2junk"); });
    Invalid([&] { join.Parse("--lan-id=-1"); });
    Options remote;
    Check(remote.Parse("--lan-bind=192.168.1.10"));
    remote.Validate(); // multicast discovery does not require a peer list
    Check(remote.Parse("--lan-peer=192.168.1.20"));
    remote.Validate();
    Check(remote.bindAddress == 0xc0a8010a && remote.peers[0] == 0xc0a80114);
    for (const auto ip : {"1.2.3", "1.2.3.4.5", "256.2.3.4", "1..3.4", "0.0.0.0", "255.255.255.255", "224.0.0.1"})
        Invalid([&] { Options::Address(ip); });
    Options local;
    Check(local.Parse("--lan-local"));
    Check(local.localOnly);
    local.Parse("--lan-bind=127.0.0.1");
    Invalid([&] { local.Validate(); });
    Options mismatch;
    mismatch.Parse("--lan-id=12"); mismatch.Parse("--lan-peers=2");
    Invalid([&] { mismatch.Validate(); });
}
