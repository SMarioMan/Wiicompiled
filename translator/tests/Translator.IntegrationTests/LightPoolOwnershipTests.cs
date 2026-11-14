using System.Diagnostics;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class LightPoolOwnershipTests
{
    [Fact]
    [Trait("Requires", "USA game assets and Clang")]
    public void ExhaustedRealLightListsShareUntilFinalReleaseAndResetPerHolder()
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName??throw new DirectoryNotFoundException("Repository root missing");
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));
        var translator=new FunctionTranslator(image);
        var temp=Path.Combine(Path.GetTempPath(),"mkw-light-pool-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try{
            var entries=new uint[]{0x8055d018,0x8055d084,0x8055db40,0x800aeec0,0x800aeee0,0x800af070,0x800af0e0};
            var sources=new List<string>{Path.Combine(root,"runtime/src/hle/light_pool.cpp"),Path.Combine(root,"runtime/src/guest_region.cpp")};
            foreach(var pc in entries){var path=Path.Combine(temp,$"{pc:X8}.cpp");
                File.WriteAllText(path,translator.Translate(pc,new TranslationOptions(EnableLeafInlining:false,KnownFunctionEntryPoints:map.Addresses.ToHashSet())).CxxCode);sources.Add(path);}
            var declarations=string.Join("\n",entries.Select(pc=>$"extern \"C\" void func_{pc:X8}(CpuContext*);"));
            var registrations=string.Join("\n",entries.Select(pc=>$"entry(0x{pc:X8},func_{pc:X8});"));
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include "mkw_guest_layout.h"
                #include <algorithm>
                #include <array>
                #include <cstring>
                #include <random>
                #include <vector>
                {{declarations}}
                extern "C" void LightHolder_ResetSharing(CpuContext*);
                extern "C" void LightHolder_AcquireExpanded(CpuContext*);
                extern "C" void LightHolder_ReleaseExpanded(CpuContext*);
                extern "C" void RaceLightHolder_RetireSharing(CpuContext*);
                namespace MkwGuestLayout {const Layout kLayout{.playerCapacity=128,.racedataRace=32};}
                constexpr uint32_t a=0x80010000,b=0x80020000,poolA=0x80030000,poolB=0x80040000,
                    data=0x80050000,stack=0x800f0000,pool=120,stride=0x58;
                uint32_t poolSize=pool;
                uint32_t storage(uint32_t holder){if(holder==a)return poolA;if(holder==b)return poolB;std::abort();}
                void entry(uint32_t address,void(*fn)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="real light pool fixture";
                    info.rawCpuInvoker=fn;info.entryPoint=reinterpret_cast<void*>(fn);TranslatedFunctionRegistry::Register(info);}
                void initialize(CpuContext* c){
                    auto holder=c->gpr[3],objects=storage(holder);
                    c->gpr[3]=holder;c->gpr[4]=0;func_800AEEC0(c);
                    c->gpr[3]=holder+12;c->gpr[4]=0;func_800AEEC0(c);
                    for(uint32_t i=0;i<poolSize;i++){
                        auto p=objects+i*stride;std::memset(Memory::GetPointer(p,stride),0x55,stride);
                        Memory::Write32(p,0);Memory::Write32(p+4,0);Memory::Write8(p+8,0);
                        c->gpr[3]=holder+12;c->gpr[4]=p;func_800AEEE0(c);
                    }
                    c->gpr[3]=holder;
                }
                struct Model {uint32_t holder;std::array<uint32_t,pool> owners{};std::vector<uint32_t> tokens;};
                bool valid(const Model& m){
                    const auto used=std::count_if(m.owners.begin(),m.owners.end(),[](auto n){return n!=0;});
                    if(Memory::Read16(m.holder+8)!=used||Memory::Read16(m.holder+20)!=poolSize-used)return false;
                    for(uint32_t i=0;i<poolSize;i++){
                        auto p=storage(m.holder)+i*stride;
                        if(Memory::Read8(p+8)!=(m.owners[i]!=0))return false;
                        for(uint32_t j=9;j<stride;j++)if(Memory::Read8(p+j)!=0x55)return false;
                    }
                    return true;
                }
                bool acquire(CpuContext& c,Model& m,uint32_t requests){
                    for(uint32_t i=0;i<requests;i++){
                        c.gpr[3]=m.holder;LightHolder_AcquireExpanded(&c);auto p=c.gpr[3];
                        auto first=storage(m.holder);
                        if(p<first||p>=first+poolSize*stride||(p-first)%stride)return false;
                        ++m.owners[(p-first)/stride];m.tokens.push_back(p);if(!valid(m))return false;
                    }
                    return true;
                }
                bool release(CpuContext& c,Model& m){
                    std::mt19937 rng(0x128);std::shuffle(m.tokens.begin(),m.tokens.end(),rng);
                    for(auto p:m.tokens){
                        auto id=(p-storage(m.holder))/stride;if(!m.owners[id])return false;--m.owners[id];
                        c.gpr[3]=m.holder;c.gpr[4]=p;LightHolder_ReleaseExpanded(&c);
                        if(!valid(m))return false;
                    }
                    m.tokens.clear();return true;
                }
                int main(){
                    Memory::Init(16*1024*1024);{{registrations}}
                    entry(0x8055cca4,initialize);
                    Memory::Write32(0x809b8f68,data);
                    CpuContext c{};CpuContextScope scope(&c);c.gpr[1]=stack;
                    // Establish the original failure independently of the
                    // sharing wrapper: the 121st acquisition returns null.
                    c.gpr[3]=a;LightHolder_ResetSharing(&c);
                    for(uint32_t i=0;i<pool;i++){c.gpr[3]=a;func_8055D018(&c);if(!c.gpr[3])return 10;}
                    c.gpr[3]=a;func_8055D018(&c);if(c.gpr[3])return 11;
                    for(uint32_t n:{12u,13u,24u,25u,26u,48u,52u,64u,100u,116u,118u,127u,128u}){
                        Memory::Write8(data+36,n);poolSize=pool;
                        Model first{a},second{b};
                        c.gpr[3]=a;LightHolder_ResetSharing(&c);
                        if(!acquire(c,first,2*n+16))return 1;
                        // Constructing the other live holder must not erase
                        // references belonging to the exhausted first holder.
                        c.gpr[3]=b;LightHolder_ResetSharing(&c);
                        if(!acquire(c,second,2*n+16)||!release(c,first)||!valid(second))return 2;
                        c.gpr[3]=a;c.gpr[4]=0;RaceLightHolder_RetireSharing(&c);
                        c.gpr[3]=a;LightHolder_ResetSharing(&c);
                        if(!acquire(c,first,2*n+16)||!release(c,second)||!release(c,first))return 3;
                        // Scene teardown may discard still-owned lights. A new
                        // lifetime at the same address starts with a full pool.
                        if(!acquire(c,first,2*n+16))return 4;
                        c.gpr[3]=a;c.gpr[4]=0;RaceLightHolder_RetireSharing(&c);
                        first=Model{a};c.gpr[3]=a;LightHolder_ResetSharing(&c);
                        if(!acquire(c,first,2*n+16)||!release(c,first))return 5;
                        if(c.gpr[1]!=stack)return 6;
                    }
                    // The original twelve-racer path still reports exhaustion.
                    Memory::Write8(data+36,12);poolSize=1;Model retail{a};
                    c.gpr[3]=a;LightHolder_ResetSharing(&c);if(!acquire(c,retail,1))return 7;
                    c.gpr[3]=a;LightHolder_AcquireExpanded(&c);if(c.gpr[3]!=0)return 8;
                    return release(c,retail)?0:9;
                }
                """);
            var args=TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false);
            var support=Path.Combine(temp,"translator_test_support.cpp");
            File.WriteAllText(support,File.ReadAllText(support).Replace("const uint8_t kMkwGuestRegion = 0;","const uint8_t kMkwGuestRegion = 1;",StringComparison.Ordinal));
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",args){RedirectStandardError=true})!){
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);if(!done)compile.Kill(entireProcessTree:true);
                Assert.True(done,"Light ownership compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());}
            using var run=Process.Start(new ProcessStartInfo(exe){RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(30000);if(!finished)run.Kill(entireProcessTree:true);
            Assert.True(finished,"Light ownership execution timed out");Assert.True(run.ExitCode==0,$"Light ownership returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }finally{Directory.Delete(temp,recursive:true);}
    }
}
