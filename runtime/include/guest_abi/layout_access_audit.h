#pragma once

#include <atomic>
#include <cstdint>
#include <cstddef>
#include <stdexcept>
#include <string>
#include <string_view>

// Optional diagnostics only. Descriptions come from the resolved guest layout;
// observations never redirect, poison, protect or otherwise change guest RAM.
namespace LayoutAccessAudit {
enum class Mode { Off, Log, Strict };
enum class Operation { Read, Write };
inline constexpr uint32_t UnknownIndex = UINT32_MAX;
struct Field {
    std::string_view name;
    uint32_t oldOffset, oldSize, oldCount, oldStride;
    uint32_t newOffset, newSize, newCount, newStride;
    bool perRacer = false;
};
// This header is part of the C++17 guest ABI even though the native auditor
// implementation is compiled as C++20. Metadata borrows static generated arrays.
class FieldRange {
    const Field* data_;
    std::size_t size_;
public:
    constexpr FieldRange(const Field* data, std::size_t size) : data_(data), size_(size) {}
    template<std::size_t N>
    constexpr FieldRange(const Field (&fields)[N]) : data_(fields), size_(N) {}
    constexpr const Field* begin() const { return data_; }
    constexpr const Field* end() const { return data_ + size_; }
};
struct Type {
    std::string_view name;
    uint32_t oldSize, newSize;
    FieldRange fields;
};
struct Site {
    uint32_t pc = 0;
    std::string_view type, field, function, rule;
    uint32_t index = UnknownIndex;
    uint32_t memberOffset = 0;
    uint32_t objectBase = 0;
    bool native = false;
};
struct Options {
    Mode mode = Mode::Off;
    bool includeAmbiguous = false;
    std::string reportPath;
};
class StaleAccess final : public std::runtime_error {
public:
    explicit StaleAccess(const std::string& message) : std::runtime_error(message) {}
};
extern std::atomic<bool> enabled;
void Configure(Options options);
void RegisterType(const Type& type);
bool HasType(std::string_view type);
void BindSite(const Site& site);
void BindStackSite(uint32_t pc, std::string_view type);
uint64_t RegisterInstance(std::string_view type, uint32_t base, uint32_t allocatedBytes = 0,
                          std::string_view lifetime = {}, uint32_t owner = 0);
// Idempotent first-use registration for objects whose constructors were inlined.
// An existing watch must have the same allocation extent and owner.
uint64_t EnsureInstance(std::string_view type, uint32_t base, uint32_t allocatedBytes = 0,
                        std::string_view lifetime = {}, uint32_t owner = 0);
void RetireInstance(uint64_t generation);
void RetireAllocation(uint32_t base);
void RetireOwner(uint32_t owner);
void RetireAllInstances();
void SetPhase(std::string_view phase);
void SetExecutionContextProvider(uint32_t (*provider)());
void RetireExecutionContext(uint32_t context);
void SetGuestPc(uint32_t pc);
void Observe(uint32_t address, uint32_t width, Operation operation);
std::string ReportJson();
void WriteReport();
void Reset();
bool ParseArgument(std::string_view argument);

class ScopedSite {
    Site previous_;
    uint32_t context_;
public:
    explicit ScopedSite(Site site);
    ~ScopedSite();
    ScopedSite(const ScopedSite&) = delete;
    ScopedSite& operator=(const ScopedSite&) = delete;
};
class FunctionScope {
    Site previous_;
    uint32_t context_;
    bool active_;
    uint64_t frame_ = 0;
public:
    explicit FunctionScope(uint32_t entry);
    ~FunctionScope();
    FunctionScope(const FunctionScope&) = delete;
    FunctionScope& operator=(const FunctionScope&) = delete;
};
}

#ifdef MKW_ENABLE_LAYOUT_ACCESS_AUDIT
#define MKW_LAYOUT_AUDIT_SITE(pc) ::LayoutAccessAudit::SetGuestPc(pc)
#define MKW_LAYOUT_AUDIT_FUNCTION(pc) ::LayoutAccessAudit::FunctionScope mkw_layout_audit_function_scope(pc)
#define MKW_LAYOUT_AUDIT_READ(address, width) do { if (::LayoutAccessAudit::enabled.load(std::memory_order_relaxed)) ::LayoutAccessAudit::Observe(address, width, ::LayoutAccessAudit::Operation::Read); } while (0)
#define MKW_LAYOUT_AUDIT_WRITE(address, width) do { if (::LayoutAccessAudit::enabled.load(std::memory_order_relaxed)) ::LayoutAccessAudit::Observe(address, width, ::LayoutAccessAudit::Operation::Write); } while (0)
#else
#define MKW_LAYOUT_AUDIT_SITE(pc) ((void)0)
#define MKW_LAYOUT_AUDIT_FUNCTION(pc) ((void)0)
#define MKW_LAYOUT_AUDIT_READ(address, width) ((void)0)
#define MKW_LAYOUT_AUDIT_WRITE(address, width) ((void)0)
#endif
