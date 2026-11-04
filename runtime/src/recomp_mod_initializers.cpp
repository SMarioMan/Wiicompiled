#include "recomp_mod_loader.h"
#include "runtime_log.h"

#include <mutex>
#include <vector>

namespace {
struct Phase {
    std::mutex mutex;
    std::vector<RecompMod::InitializerFn> callbacks;
    bool ran = false;
};
Phase& ImagePhase() { static Phase phase; return phase; }
Phase& PatchPhase() { static Phase phase; return phase; }
Phase& CtorPhase() { static Phase phase; return phase; }

void Register(Phase& phase, RecompMod::InitializerFn fn) {
    if (!fn) return;
    std::lock_guard lock(phase.mutex);
    phase.callbacks.push_back(fn);
}
void Run(Phase& phase, const char* label) {
    std::vector<RecompMod::InitializerFn> pending;
    {
        std::lock_guard lock(phase.mutex);
        if (phase.ran) return;
        phase.ran = true;
        pending = phase.callbacks;
    }
    for (const auto fn : pending) fn();
    if (!pending.empty()) {
        RT_LOG(RT_TAG_MOD) << "Ran " << pending.size() << " recomp mod " << label << " initializer(s)" << std::endl;
    }
}
} // namespace

namespace RecompMod {
void RegisterImageInitializer(InitializerFn fn) { Register(ImagePhase(), fn); }
void RunImageInitializers() { Run(ImagePhase(), "image"); }
void RegisterMemoryInitializer(InitializerFn fn) { Register(PatchPhase(), fn); }
void RunMemoryInitializers() {
    RunImageInitializers();
    Run(PatchPhase(), "memory");
}
void RegisterPostRelInitializer(InitializerFn fn) { Register(CtorPhase(), fn); }
void RunPostRelInitializers() { Run(CtorPhase(), "post-REL"); }
} // namespace RecompMod
