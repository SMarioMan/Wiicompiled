using System.Diagnostics;
using Xunit;

namespace Translator.Tests;

public class LayoutAuditBulkCacheTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CacheZeroAndLockedCacheCopiesObserveReadsAndWritesBeforeMutation(bool instrumented)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "translator/Translator.sln"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException();
        var temp = Path.Combine(Path.GetTempPath(), "mkw-audit-bulk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var harness = Path.Combine(temp, "harness.cpp"); var exe = Path.Combine(temp, "test");
            File.WriteAllText(harness, """
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include "guest_abi/layout_access_audit.h"
                #include <cstring>
                #include <string>
                extern "C" void DCZeroRange_HLE_801a16e4(CpuContext*);
                extern "C" void LCLoadBlocks_HLE_801a1894(CpuContext*);
                extern "C" void LCStoreBlocks_HLE_801a18b8(CpuContext*);
                extern "C" uint32_t LCStoreData_HLE_801a18dc(CpuContext*);
                unsigned notifications=0;
                extern "C" void GxNotifyGuestRamDmaWrite(uint32_t,uint32_t){++notifications;}
                using namespace LayoutAccessAudit;
                constexpr uint32_t base=0x80020000,oldTail=base+96,newTail=base+160,out=0x80030000;
                bool unchanged(uint32_t address,uint32_t bytes,uint8_t value){
                    for(uint32_t i=0;i<bytes;i++)if(Memory::Read8(address+i)!=value)return false;return true;
                }
                int main(){
                    Memory::Init(1024*1024);
                    constexpr Field fields[]={{"header",0,32,1,32,0,32,1,32},
                        {"slots",32,64,16,4,32,128,32,4}, {"tail",96,64,1,64,160,64,1,64}};
                    RegisterType({"BulkObject",160,224,fields});
                    auto generation=RegisterInstance("BulkObject",base,224);
                    CpuContext c{};CpuContextScope context(&c);
                    std::memset(Memory::GetPointer(base,224),0x55,224);
                    std::memset(Memory::GetPointer(out,64),0x77,64);
                #ifdef MKW_ENABLE_LAYOUT_ACCESS_AUDIT
                    Configure({Mode::Strict});
                    {
                        ScopedSite site({.pc=0x80001000,.type="BulkObject",.field="tail",.native=true});
                        try{memset_zero_32(oldTail);return 1;}catch(const StaleAccess&){}
                    }
                    if(!unchanged(oldTail,32,0x55))return 2;
                    {
                        ScopedSite site({.pc=0x80002000,.type="BulkObject",.field="tail",.native=true});
                        c.gpr[3]=out;c.gpr[4]=oldTail;c.gpr[5]=1;
                        try{LCLoadBlocks_HLE_801a1894(&c);return 3;}catch(const StaleAccess&){}
                    }
                    if(!unchanged(out,32,0x77)||notifications)return 4;
                    {
                        ScopedSite site({.pc=0x80003000,.type="BulkObject",.field="tail",.native=true});
                        c.gpr[3]=oldTail;c.gpr[4]=out;c.gpr[5]=1;
                        try{LCStoreBlocks_HLE_801a18b8(&c);return 5;}catch(const StaleAccess&){}
                    }
                    if(!unchanged(oldTail,32,0x55)||notifications)return 6;
                    {
                        ScopedSite site({.pc=0x80004000,.type="BulkObject",.field="tail",.native=true});
                        c.gpr[3]=oldTail+1;c.gpr[4]=1;
                        try{DCZeroRange_HLE_801a16e4(&c);return 7;}catch(const StaleAccess&){}
                    }
                    if(!unchanged(oldTail,32,0x55)||notifications)return 8;
                    {
                        ScopedSite site({.pc=0x80006000,.type="BulkObject",.field="tail",.native=true});
                        c.gpr[3]=oldTail;c.gpr[4]=out;c.gpr[5]=96;
                        // Starts at a stale tail pointer but spans beyond the
                        // original 64-byte field: do not miss this raw copy.
                        try{LCStoreData_HLE_801a18dc(&c);return 16;}catch(const StaleAccess&){}
                    }
                    if(!unchanged(oldTail,96,0x55)||notifications)return 17;
                    auto report=ReportJson();
                    for(auto pc:{"0x80001000","0x80002000","0x80003000","0x80004000","0x80006000"})
                        if(report.find(pc)==std::string::npos)return 9;
                    if(report.find("\"operation\":\"read\"")==std::string::npos ||
                       report.find("\"width\":32")==std::string::npos ||
                       report.find("\"width\":96")==std::string::npos)return 10;
                #else
                    Configure({Mode::Off});
                    memset_zero_32(oldTail);if(!unchanged(oldTail,32,0))return 11;
                #endif
                    // Correct relocated accesses retain the real byte copying,
                    // overlapping memmove behavior and GX DMA notifications.
                    ScopedSite site({.pc=0x80005000,.type="BulkObject",.field="tail",.native=true});
                    c.gpr[3]=newTail;c.gpr[4]=out;c.gpr[5]=32;
                    if(LCStoreData_HLE_801a18dc(&c)!=1 || !unchanged(newTail,32,0x77))return 12;
                    c.gpr[3]=newTail+16;c.gpr[4]=newTail;c.gpr[5]=32;
                    LCStoreData_HLE_801a18dc(&c);if(!unchanged(newTail,48,0x77))return 13;
                    c.gpr[3]=newTail+1;c.gpr[4]=1;DCZeroRange_HLE_801a16e4(&c);
                    if(!unchanged(newTail,32,0)||!unchanged(newTail+32,16,0x77)||notifications!=3)return 14;
                    memset_zero_32(newTail+32);if(!unchanged(newTail,64,0))return 15;
                    RetireInstance(generation);
                }
                """);
            var sources = new[] { Path.Combine(root, "runtime/src/layout_access_audit.cpp"),
                Path.Combine(root, "runtime/src/hle/os/os_cache.cpp"), Path.Combine(root, "runtime/src/guest_region.cpp") };
            var args = TranslatorCppTestHarness.BuildCompileArguments(root, temp, sources, harness, exe, includeDataSections: false)
                + (instrumented ? " -DMKW_ENABLE_LAYOUT_ACCESS_AUDIT=1" : "");
            using (var compile = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX") ?? "clang++", args) { RedirectStandardError = true })!)
            {
                var errors = compile.StandardError.ReadToEndAsync(); var done = compile.WaitForExit(60000);
                if (!done) compile.Kill(entireProcessTree: true);
                Assert.True(done, "Bulk cache fixture compilation timed out"); Assert.True(compile.ExitCode == 0, errors.GetAwaiter().GetResult());
            }
            using var run = Process.Start(new ProcessStartInfo(exe) { RedirectStandardError = true })!;
            var output = run.StandardError.ReadToEndAsync(); var finished = run.WaitForExit(15000);
            if (!finished) run.Kill(entireProcessTree: true);
            Assert.True(finished, "Bulk cache fixture execution timed out"); Assert.True(run.ExitCode == 0, $"exit {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally { Directory.Delete(temp, recursive: true); }
    }
}
