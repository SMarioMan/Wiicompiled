using System.Diagnostics;
using Xunit;

namespace Translator.Tests;

public class LayoutAuditLifetimeWrapperTests
{
    [Theory]
    [InlineData(false, "ArchiveManager", 0x8053a7b0u, 0x8053a9e0u)]
    [InlineData(true, "ArchiveManager", 0x8053a7b0u, 0x8053a9e0u)]
    [InlineData(false, "AIManager", 0x8072b8acu, 0x8072ba84u)]
    [InlineData(true, "AIManager", 0x8072b8acu, 0x8072ba84u)]
    [InlineData(false, "ItemManager", 0x807902ccu, 0x80790664u)]
    [InlineData(true, "ItemManager", 0x807902ccu, 0x80790664u)]
    [InlineData(false, "AIBattleTargetSelector", 0x8072535cu, 0x8072537cu)]
    [InlineData(true, "AIBattleTargetSelector", 0x8072535cu, 0x8072537cu)]
    [InlineData(false, "DriverManager", 0x80783a50u, 0x80783e18u)]
    [InlineData(true, "DriverManager", 0x80783a50u, 0x80783e18u)]
    [InlineData(false, "BattleBalloonManager", 0x80865454u, 0x80865568u)]
    [InlineData(true, "BattleBalloonManager", 0x80865454u, 0x80865568u)]
    public void WrappersWatchConstructionAndRetireBeforeReleaseIncludingFailures(bool instrumented, string type, uint constructor, uint destructor)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName??throw new DirectoryNotFoundException("Repository root missing");
        var temp=Path.Combine(Path.GetTempPath(),"mkw-audit-lifetimes-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try{
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include "guest_abi/layout_access_audit.h"
                #include <stdexcept>
                #include <string>
                #include <fstream>
                extern "C" void {{{type}}}_AuditConstruct(CpuContext*);
                extern "C" void {{{type}}}_AuditDestroy(CpuContext*);
                extern "C" void GuestDelete_AuditRetirement(CpuContext*);
                using namespace LayoutAccessAudit;
                constexpr uint32_t base=0x80020000;
                bool expectedLive=true,fail=false;
                uint32_t constructors=0,destructors=0,releases=0;
                const char* reportPath=nullptr;
                bool active(bool live){return ReportJson().find(live?"\"activeInstances\":1":"\"activeInstances\":0")!=std::string::npos;}
                void originalCtor(CpuContext* c){
                    if(c->gpr[3]!=base||!active(expectedLive))std::abort();++constructors;
                    if(expectedLive){std::ifstream input(reportPath);std::string saved((std::istreambuf_iterator<char>(input)),{});
                        if(saved.find("\"activeInstances\":1")==std::string::npos)std::abort();}
                    ScopedSite site({.pc=0x80001000,.type="{{{type}}}",.field="tail",.native=true});
                    Memory::Read32(base+56); // intentionally stale constructor reader
                    if(fail)throw std::runtime_error("controlled construction failure");
                }
                void originalDelete(CpuContext* c){
                    if(c->gpr[3]!=base||!active(false))std::abort();++releases;
                    ScopedSite site({.pc=0x80002000,.type="{{{type}}}",.field="tail",.native=true});
                    Memory::Write32(base+56,0x1234); // allocation already reusable, no watch
                }
                void originalDtor(CpuContext* c){
                    if(c->gpr[3]!=base||!active(expectedLive))std::abort();++destructors;
                    if(c->gpr[4])GuestDelete_AuditRetirement(c);
                }
                void entry(uint32_t address,void(*fn)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="original archive lifetime fixture";
                    info.rawCpuInvoker=fn;info.entryPoint=reinterpret_cast<void*>(fn);TranslatedFunctionRegistry::Register(info);
                }
                int main(int argc,char** argv){
                    if(argc!=2)return 9;reportPath=argv[1];
                    Memory::Init(1024*1024);
                    constexpr Field fields[]={{"header",0,8,1,8,0,8,1,8},{"holders",8,48,12,4,8,96,24,4,true},{"tail",56,4,1,4,104,4,1,4}};
                    RegisterType({"{{{type}}}",60,108,fields});Configure({Mode::Log,false,reportPath});
                    // Actual NTSC-U function entries; wrapper identities use the
                    // existing PAL-to-USA region mapping rather than PAL data.
                    entry(0x{{{constructor:X8}}},originalCtor);entry(0x{{{destructor:X8}}},originalDtor);entry(0x80229a90,originalDelete);
                    CpuContext c{};CpuContextScope scope(&c);c.gpr[3]=base;
                #ifndef MKW_ENABLE_LAYOUT_ACCESS_AUDIT
                    expectedLive=false;Configure({Mode::Off});
                #endif
                    for(unsigned deleting=0;deleting<2;deleting++){
                        c.gpr[3]=base;{{{type}}}_AuditConstruct(&c);if(!active(expectedLive))return 1;
                        c.gpr[3]=base;c.gpr[4]=deleting;{{{type}}}_AuditDestroy(&c);if(!active(false))return 2;
                    }
                #ifndef MKW_ENABLE_LAYOUT_ACCESS_AUDIT
                    return constructors==2&&destructors==2&&releases==1&&active(false)?0:8;
                #else
                    fail=true;c.gpr[3]=base;
                    try{{{{type}}}_AuditConstruct(&c);return 3;}catch(const std::runtime_error&){}
                    if(!active(false))return 4;fail=false;
                    auto report=ReportJson();
                    if(report.find("\"count\":3")==std::string::npos||report.find("0x80002000")!=std::string::npos)return 5;
                    Configure({Mode::Off});expectedLive=false;c.gpr[3]=base;{{{type}}}_AuditConstruct(&c);
                    c.gpr[3]=base;c.gpr[4]=0;{{{type}}}_AuditDestroy(&c);if(!active(false))return 6;
                    Reset();Configure({Mode::Log,false,reportPath});c.gpr[3]=base;{{{type}}}_AuditConstruct(&c);
                    c.gpr[3]=base;c.gpr[4]=0;{{{type}}}_AuditDestroy(&c);
                    return constructors==5&&destructors==4&&releases==1&&active(false)?0:7;
                #endif
                }
                """);
            var sources=new[]{Path.Combine(root,"runtime/src/layout_access_audit.cpp"),Path.Combine(root,"runtime/src/hle/layout_audit_lifetimes.cpp"),Path.Combine(root,"runtime/src/guest_region.cpp")};
            var args=TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)+(instrumented?" -DMKW_ENABLE_LAYOUT_ACCESS_AUDIT=1":"");
            var support=Path.Combine(temp,"translator_test_support.cpp");
            File.WriteAllText(support,File.ReadAllText(support).Replace("const uint8_t kMkwGuestRegion = 0;","const uint8_t kMkwGuestRegion = 1;",StringComparison.Ordinal));
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",args){RedirectStandardError=true})!){
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);if(!done)compile.Kill(entireProcessTree:true);
                Assert.True(done,"Lifetime wrapper compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());}
            var runInfo=new ProcessStartInfo(exe){RedirectStandardError=true};runInfo.ArgumentList.Add(Path.Combine(temp,"early-report.json"));
            using var run=Process.Start(runInfo)!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(15000);if(!finished)run.Kill(entireProcessTree:true);
            Assert.True(finished,"Lifetime wrapper execution timed out");Assert.True(run.ExitCode==0,$"Lifetime wrapper returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }finally{Directory.Delete(temp,recursive:true);}
    }
}
