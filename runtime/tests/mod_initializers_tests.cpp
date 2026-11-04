#include "recomp_mod_loader.h"

#include <cstdlib>
#include <iostream>
#include <string_view>
#include <vector>

namespace {
std::vector<int> calls;
int imageState = 0;
int expectedState = 0;
void Check(bool condition) {
    if (!condition) {
        std::cerr << "Mod initialization ordering/state check failed\n";
        std::exit(1);
    }
}
void Image() { calls.push_back(1); imageState = 7; }
void Patches() {
    Check(imageState == expectedState);
    calls.push_back(2);
    imageState = 11;
}
void Ctors() { Check(imageState == 11); calls.push_back(3); }
}
int main(int argc, char** argv) {
    RecompMod::RegisterImageInitializer(nullptr);
    RecompMod::RegisterImageInitializer(Image);
    RecompMod::RegisterMemoryInitializer(Patches);
    RecompMod::RegisterPostRelInitializer(Ctors);
    const bool late = argc > 1 && std::string_view(argv[1]) == "late";
    if (!late) {
        RecompMod::RunImageInitializers();
        Check(imageState == 7 && calls == std::vector<int>{1});
        // An early DOL hook changes module state before StaticR links.
        imageState = 9;
    }
    expectedState = late ? 7 : 9;
    RecompMod::RunMemoryInitializers();
    RecompMod::RunPostRelInitializers();
    RecompMod::RunImageInitializers();
    RecompMod::RunMemoryInitializers();
    RecompMod::RunPostRelInitializers();
    Check(imageState == 11 && calls == std::vector<int>({1, 2, 3}));
}
