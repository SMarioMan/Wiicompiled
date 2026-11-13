using System.Diagnostics;
using Translator.Core.Translation;
using Xunit;
namespace Translator.Tests;
public class ExpandedCpuRosterTests
{
    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(48)] [InlineData(100)]
    public void NativeMenuRosterCountsSparseSlotsAndExhaustsCharactersBeforeRepeating(int players)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName ?? throw new DirectoryNotFoundException();
        var temp=Path.Combine(Path.GetTempPath(),"mkw-cpu-roster-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try {
            var header=Path.Combine(temp,"layout.h");
            File.WriteAllText(header,GuestLayoutRelocations.Read(Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json"),players).EmitNativeConstants());
            var glue=Path.Combine(temp,"glue.cpp");
            File.WriteAllText(glue,$"#define MKW_TRANSLATED_PLAYER_CAPACITY {players}\n#define MKW_TRANSLATED_GUEST_LAYOUT_HEADER \"{header}\"\n#include \"{root}/runtime/game/guest_layout_values.cpp\"\n");
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include "mkw_guest_layout.h"
                #include <array>
                #include <algorithm>
                #include <cstring>
                extern "C" void GetActiveRacedataPlayers_Expanded(CpuContext*);
                extern "C" void CharacterSelect_AssignExpandedCpuCharacters(CpuContext*);
                constexpr uint32_t data=0x80010000,stack=0x80f00000,capacity={{players}};
                uint32_t originalCalls=0,rngCalls=0,eligibleCount=24;
                uint32_t weight(uint32_t ch){return ch%3;}
                void original(CpuContext* c){++originalCalls;c->gpr[3]=0x12345678;}
                void getweight(CpuContext* c){if(c->gpr[3]>=24)std::abort();c->gpr[3]=weight(c->gpr[3]);}
                void unlocked(CpuContext* c){c->gpr[3]=c->gpr[3]<eligibleCount;}
                void rng_init(CpuContext* c){if(c->gpr[3]!=stack-96 || c->gpr[1]!=stack-128)std::abort();Memory::Write32(c->gpr[3],0x1234);}
                void rng_destroy(CpuContext* c){if(c->gpr[4]!=0xffffffffu || Memory::Read32(c->gpr[3])!=0x1234)std::abort();}
                void rng(CpuContext* c){if(!c->gpr[4] || c->gpr[4]>24)std::abort();c->gpr[3]=(rngCalls++*7)%c->gpr[4];}
                void entry(uint32_t address,void(*fn)(CpuContext*)){TranslatedFunctionInfo info{};info.address=address;info.name="CPU roster fixture";
                    info.rawCpuInvoker=fn;info.entryPoint=reinterpret_cast<void*>(fn);TranslatedFunctionRegistry::Register(info);}
                int main(){Memory::Init(16*1024*1024);const auto& l=MkwGuestLayout::kLayout;
                    Memory::Write32(0x809bd728,data);auto base=data+l.racedataMenu+l.scenarioPlayers;
                    entry(0x80860500,original);entry(0x8083ec28,original);
                    entry(0x8081cd3c,getweight);entry(0x8081d020,unlocked);entry(0x80555464,rng_init);entry(0x80555538,rng_destroy);entry(0x805555cc,rng);
                    CpuContext c{};CpuContextScope scope(&c);c.gpr[1]=stack;c.lr=0x80006000;
                    if(capacity==12){GetActiveRacedataPlayers_Expanded(&c);CharacterSelect_AssignExpandedCpuCharacters(&c);return originalCalls==2&&c.gpr[3]==0x12345678?0:1;}
                    for(uint32_t pool : {24u,6u,1u,0u})for(bool sparse : {false,true}){
                        eligibleCount=pool;rngCalls=0;std::memset(Memory::GetPointer(data,l.racedataSize),0x55,l.racedataSize);
                        std::array<uint32_t,24> uses{};std::array<uint32_t,3> weights{};uint32_t active=0,cpus=0;
                        for(uint32_t i=0;i<capacity;i++){
                            auto p=base+i*l.playerStride;const auto type=i==capacity-1?0u:sparse&&i%4==0?5u:1u;
                            Memory::Write32(p+0x10,type);Memory::Write32(p+0xc,type==0?0:0xffffffffu);Memory::Write32(p+8,0x87654321);
                            if(type!=5)active++;if(type==1)cpus++;
                        }
                        uses[0]=1;weights[0]=1;
                        GetActiveRacedataPlayers_Expanded(&c);if(c.gpr[3]!=active)return 2;
                        for(uint32_t r=0;r<32;r++)if(r!=1)c.gpr[r]=0x12340000+r;c.cr=0x87654321;c.xer=0xabcdef01;c.ctr=5;
                        const CpuContext saved=c;CharacterSelect_AssignExpandedCpuCharacters(&c);
                        if(std::memcmp(&c,&saved,sizeof(c)))return 3;
                        if(rngCalls!=(pool?cpus:0))return 4;
                        for(uint32_t i=0;i<capacity;i++){
                            auto p=base+i*l.playerStride;auto type=Memory::Read32(p+0x10),ch=Memory::Read32(p+0xc);
                            if(Memory::Read32(p+8)!=0x87654321)return 5;
                            if(type!=1 || pool==0){if(ch!=(type==0?0u:0xffffffffu))return 6;continue;}
                            if(ch>=pool)return 7;
                            auto minimum=*std::min_element(uses.begin(),uses.begin()+pool);if(uses[ch]!=minimum)return 8;
                            uint32_t minWeight=0xffffffffu;for(uint32_t j=0;j<pool;j++)if(uses[j]==minimum)minWeight=std::min(minWeight,weights[weight(j)]);
                            if(weights[weight(ch)]!=minWeight)return 9;
                            ++uses[ch];++weights[weight(ch)];
                        }
                        // All bytes outside the CPU character word and fixture fields stay untouched.
                        for(uint32_t offset=0;offset<l.racedataSize;offset++){
                            bool changed=false;
                            if(offset>=l.racedataMenu+l.scenarioPlayers && offset<l.racedataMenu+l.scenarioPlayers+capacity*l.playerStride){
                                auto field=(offset-l.racedataMenu-l.scenarioPlayers)%l.playerStride;changed=field>=8&&field<20;}
                            if(!changed && Memory::Read8(data+offset)!=0x55)return 10;
                        }
                    }
                    return 0;
                }
                """);
            var sources=new[]{glue,Path.Combine(root,"runtime/src/hle/cpu_roster.cpp"),Path.Combine(root,"runtime/src/guest_region.cpp")};
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!) {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);if(!done)compile.Kill(entireProcessTree:true);
                Assert.True(done,"CPU roster compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());}
            using var run=Process.Start(new ProcessStartInfo(exe){RedirectStandardError=true})!;var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"CPU roster execution timed out");Assert.True(run.ExitCode==0,$"Roster returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        } finally {Directory.Delete(temp,recursive:true);}
    }
}
