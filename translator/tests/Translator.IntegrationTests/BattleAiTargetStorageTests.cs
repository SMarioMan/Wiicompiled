using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class BattleAiTargetStorageTests
{
    [Theory]
    [InlineData(12,false)] [InlineData(13,false)] [InlineData(24,false)] [InlineData(25,false)]
    [InlineData(26,false)] [InlineData(48,false)] [InlineData(52,false)] [InlineData(64,false)]
    [InlineData(100,false)] [InlineData(116,false)] [InlineData(118,false)] [InlineData(127,false)]
    [InlineData(128,false)] [InlineData(128,true)]
    [Trait("Requires", "USA game assets and Clang")]
    public void OriginalBattleTargetStorageAndConsumersRespectExpandedTeamCapacity(int players,bool unpatched)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName??throw new DirectoryNotFoundException("Repository root missing");
        var manifest=Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json");
        using var document=JsonDocument.Parse(File.ReadAllText(manifest));
        var layout=new GuestLayouts(document.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!,players);
        var plan=GuestLayoutRelocations.Read(manifest,players);
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));
        var options=new TranslationOptions(EnableLeafInlining:false,KnownFunctionEntryPoints:map.Addresses.ToHashSet());
        var translator=new FunctionTranslator(image,layoutRelocations:unpatched?null:plan);
        // Test-only branches isolate the allocation immediate and the real
        // factory/inline-child construction path from unrelated parent actions.
        // All relevant original instructions and production image files stay intact.
        foreach(var (from,to) in new[]{(0x807236ecu,0x80723824u),(0x80723828u,0x80723984u),(0x80725ad8u,0x80725be8u),(0x80726044u,0x807260fcu),(0x80723fe0u,0x80724038u)})
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(from,4),4),
                0x48000000u|((to-from)&0x03fffffc));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(0x807239ac,4),4),0x4e800020);
        foreach(var join in new uint[]{0x80725c98,0x807261ac,0x80724078})
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(join,4),4),0x4e800020);
        var factory=translator.Translate(0x807236ec,options);
        var temp=Path.Combine(Path.GetTempPath(),"mkw-battle-ai-targets-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try {
            File.WriteAllText(Path.Combine(temp,"layout.h"),plan.EmitNativeConstants());
            var sources=new List<string>{Path.Combine(root,"runtime/src/layout_access_audit.cpp"),Path.Combine(root,"runtime/src/hle/battle_ai_targets.cpp"),Path.Combine(root,"runtime/src/guest_region.cpp")};
            var factoryFile=Path.Combine(temp,"factory.cpp");File.WriteAllText(factoryFile,factory.CxxCode);sources.Add(factoryFile);
            var entries=new uint[]{0x8072535c,0x8072537c,0x807253bc,0x807253fc,0x80725554,0x80725620,0x80725798,0x8073dd60,0x8073ddb8,0x80020a34,0x80020a80,0x80725ad8,0x80726044,0x80723fe0,0x80724678};
            foreach(var entry in entries){var path=Path.Combine(temp,$"{entry:X8}.cpp");File.WriteAllText(path,translator.Translate(entry,options).CxxCode);sources.Add(path);}
            if(players==12){var baseline=new FunctionTranslator(image);
                foreach(var entry in entries)Assert.Equal(baseline.Translate(entry,options).CxxCode,translator.Translate(entry,options).CxxCode);}
            var declarations=string.Join("\n",entries.Select(pc=>$"extern \"C\" void func_{pc:X8}(CpuContext*);"));
            var registrations=string.Join("\n",entries.Select(pc=>$"entry(0x{pc:X8},func_{pc:X8});"));
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include "layout.h"
                #include "mkw_guest_layout.h"
                #include <bit>
                #include <cstdio>
                #include <cstring>
                #include <stdexcept>
                #include <set>
                {{declarations}}
                extern "C" void func_807236EC(CpuContext*);
                extern "C" void BattleAI_FirstMatchingTarget(CpuContext*);
                extern "C" void BattleAI_UpdateTargets(CpuContext*);
                namespace MkwGuestLayout {
                    const Layout kLayout{.playerCapacity={{players}}};
                    const BattleTargetLayout kBattleTargets{MKW_LAYOUT_AIBATTLETARGETSELECTOR_TARGETS_COUNT,
                        MKW_LAYOUT_AIBATTLETARGETSELECTOR_TARGETS_OFFSET,MKW_LAYOUT_AIBATTLETARGETSELECTOR_STATES_OFFSET};
                }
                constexpr uint32_t n={{players}},count=MKW_LAYOUT_AIBATTLETARGETSELECTOR_TARGETS_COUNT,
                    bytes=MKW_LAYOUT_AIBATTLETARGETSELECTOR_SIZE,targets=MKW_LAYOUT_AIBATTLETARGETSELECTOR_TARGETS_OFFSET,
                    states=MKW_LAYOUT_AIBATTLETARGETSELECTOR_STATES_OFFSET,tail=MKW_LAYOUT_AIBATTLETARGETSELECTOR_TAIL_OFFSET,
                    parent=0x81010000,owner=0x81020000,child=0x81030000,rd=0x81040000,
                    ai=0x81050000,battle=0x81051000,teams=0x81052000,ownLink=0x81054000,
                    links=0x81200000,positions=0x81400000,sp=0x81f00000;
                uint32_t ownId=0,requested=0,allocations=0,deletions=0,nearCalls=0,forcedNearState=0;
                void entry(uint32_t address,void(*fn)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="battle target storage fixture";
                    info.rawCpuInvoker=fn;info.entryPoint=reinterpret_cast<void*>(fn);TranslatedFunctionRegistry::Register(info);}
                void allocate(CpuContext* c){requested=c->gpr[3];++allocations;
                    if(requested!={{(unpatched?"64u":"bytes")}})std::abort();
                    // Guarded expanded backing makes the deliberately old tail
                    // access safe to observe before it can mutate another slot.
                    LayoutAccessAudit::RegisterInstance("AIBattleTargetSelector",child,bytes,"AI target fixture");c->gpr[3]=child;}
                void release(CpuContext* c){if(c->gpr[3]!=child)std::abort();++deletions;LayoutAccessAudit::RetireAllocation(child);}
                void zero(CpuContext* c){c->gpr[3]=0;}
                void playerId(CpuContext* c){if(c->gpr[3]!=ownLink)std::abort();c->gpr[3]=ownId;}
                void position(CpuContext* c){auto p=c->gpr[3];
                    if(p==ownLink){c->gpr[3]=positions;return;}
                    if(p<links||p>=links+n*0x100||(p-links)%0x100)std::abort();c->gpr[3]=positions+16+(p-links)/16;}
                float get(uint32_t p){return std::bit_cast<float>(Memory::Read32(p));}
                void put(uint32_t p,float f){Memory::Write32(p,std::bit_cast<uint32_t>(f));}
                void subtract(CpuContext* c){for(uint32_t i=0;i<3;i++)put(c->gpr[3]+i*4,get(c->gpr[4]+i*4)-get(c->gpr[5]+i*4));}
                void near(CpuContext* c){auto at=c->gpr[5];if(at<child+states||at>=child+states+count*4||(at-child-states)%4)std::abort();
                    auto id=(c->gpr[4]-links)/0x100;Memory::Write32(at,forcedNearState?forcedNearState:3+(id/5)%2);++nearCalls;c->gpr[3]=1;}
                bool guards(){for(uint32_t i=0;i<16;i++)if(Memory::Read8(child-16+i)!=0x55||Memory::Read8(child+bytes+i)!=0x55)return false;return true;}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    entry(0x80229a48,allocate);entry(0x80229a90,release);entry(0x8058a238,playerId);
                    entry(0x8073de08,zero);entry(0x80001000,zero);
                    entry(0x805899e8,position);entry(0x805103f8,subtract);entry(0x80725628,near);
                    RegisterGeneratedLayoutAccessAuditMap();LayoutAccessAudit::Configure({LayoutAccessAudit::Mode::Strict});
                    Memory::Write32(0x809be398,ai);Memory::Write32(0x809b8f68,rd);
                    Memory::Write32(ai+MKW_LAYOUT_AIMANAGER_BATTLE_OFFSET,battle);Memory::Write32(owner,ownLink);
                    Memory::Write32(parent+0x38,0x81055000);Memory::Write32(0x81055000,ownLink);
                    Memory::Write32(parent+0x19c,0x81056200);Memory::Write32(parent+0x1a0,0x81056300);
                    Memory::Write32(battle+0xbc,0x81056000);Memory::Write32(0x81056000,0x81056100);
                    Memory::Write32(0x81056100+0x14,0x80001000);Memory::Write32(0x81056100+0x20,0x80001000);
                    for(uint32_t team=0;team<2;team++){
                        auto roster=teams+team*0x1000;Memory::Write32(battle+MKW_LAYOUT_AIBATTLEMANAGER_ROSTERS_OFFSET+team*4,roster);
                        uint32_t k=0;for(uint32_t i=team;i<n;i+=2)Memory::Write32(roster+k++*4,links+i*0x100);
                        Memory::Write32(roster+MKW_LAYOUT_AIBATTLETEAMROSTER_COUNT_OFFSET,k);
                    }
                    for(uint32_t i=0;i<n;i++)Memory::Write32(rd+MKW_LAYOUT_RACEDATA_RACE_OFFSET+MKW_LAYOUT_SCENARIO_PLAYERS_OFFSET+i*240+0xcc,i%2);
                    CpuContext c{};CpuContextScope scope(&c);c.gpr[1]=sp;c.gpr[2]=0x8038ac20;c.gpr[13]=0x80388880;
                    for(uint32_t lifetime=0;lifetime<2;lifetime++){
                        std::memset(Memory::GetPointer(child-16,bytes+32),0x55,bytes+32);
                        std::memset(Memory::GetPointer(child,bytes),0x7e,bytes);
                        c.gpr[30]=parent;c.gpr[18]=owner;c.gpr[17]=0;
                        try{func_807236EC(&c);if({{(unpatched?"true":"false")}})return 4;}
                        catch(const std::runtime_error&){
                            auto report=LayoutAccessAudit::ReportJson();
                            if(!{{(unpatched?"true":"false")}}||requested!=64||report.find("0x807239A4")==std::string::npos||
                                report.find("tail")==std::string::npos||Memory::Read32(child+0x3c)!=0x7e7e7e7e)return 5;
                            Memory::Write32(child+targets,links);Memory::Write32(child+states,1);
                            c.gpr[3]=child;c.gpr[4]=1;
                            try{func_80725554(&c);return 26;}catch(const std::runtime_error&){
                                if(LayoutAccessAudit::ReportJson().find("0x80725560")==std::string::npos)return 27;}
                            LayoutAccessAudit::RetireAllocation(child);return guards()?0:6;
                        }
                        if(Memory::Read32(parent+0x194)!=child||Memory::Read32(child)!=0x808c5778||Memory::Read32(child+4)!=owner||
                            Memory::Read32(child+8)||Memory::Read32(child+tail)||!guards())return 7;
                        for(uint32_t i=0;i<count*2;i++)if(Memory::Read32(child+targets+i*4)!=0x7e7e7e7e)return 8;
                        c.gpr[3]=child;c.gpr[4]=owner;func_8072535C(&c); // standalone constructor agrees with the inline path
                        for(ownId=0;ownId<2;ownId++){
                            c.gpr[31]=parent;func_80723FE0(&c);
                            for(uint32_t i=0;i<count;i++)if(Memory::Read32(child+targets+i*4)||Memory::Read32(child+states+i*4))return 9;
                            c.gpr[3]=parent;func_80724678(&c);
                            // Check the actual inlined population path, then the
                            // standalone helper, against the same team roster.
                            const uint32_t inlineFirst=ownId?0:1,inlineCount=ownId?(n+1)/2:n/2;
                            for(uint32_t i=0;i<count;i++)if(Memory::Read32(child+targets+i*4)!=(i<inlineCount?links+(inlineFirst+2*i)*0x100:0))return 28;
                            c.gpr[3]=child;func_807253BC(&c);
                            c.gpr[3]=child;func_80725798(&c);
                            const uint32_t first=ownId?0:1,active=ownId?(n+1)/2:n/2;
                            for(uint32_t i=0;i<count;i++)if(Memory::Read32(child+targets+i*4)!=(i<active?links+(first+2*i)*0x100:0))return 10;
                            for(uint32_t i=0;i<3;i++)put(positions+i*4,0);
                            for(uint32_t i=0;i<n;i++){put(positions+16+i*16,float(i%5<2?3500:i%5==2?5000:i%5==3?6500:8000));put(positions+20+i*16,0);put(positions+24+i*16,0);}
                            c.gpr[3]=child;BattleAI_UpdateTargets(&c);
                            auto expectedState=[](uint32_t id){return id%5<2?3+(id/5)%2:id%5==2?1u:id%5==3?2u:0u;};
                            std::set<uint32_t> found;
                            for(uint32_t i=0;i<count;i++){
                                auto target=Memory::Read32(child+targets+i*4);if(!target)continue;
                                auto id=(target-links)/0x100;
                                if(id>=n||id%2!=first||target!=links+id*0x100||!found.insert(id).second||
                                    Memory::Read32(child+states+i*4)!=expectedState(id))return 11;
                            }
                            if(found.size()!=active)return 19;
                            for(uint32_t wanted=0;wanted<5;wanted++){
                                uint32_t expected=0;
                                for(uint32_t i=0;i<active;i++)if(expectedState(first+2*i)==wanted){expected=links+(first+2*i)*0x100;break;}
                                // The real six-entry lookup sees the same first match
                                // as a full-roster query after state-prefix promotion.
                                c.gpr[3]=child;c.gpr[4]=wanted;func_80725554(&c);if(c.gpr[3]!=expected)return 20;
                                c.gpr[3]=child;c.gpr[4]=wanted;BattleAI_FirstMatchingTarget(&c);if(c.gpr[3]!=expected)return 21;
                                if(wanted==4){c.gpr[4]=child;func_80725AD8(&c);if(c.gpr[29]!=expected)return 22;}
                                if(wanted==3){c.gpr[4]=child;func_80726044(&c);if(c.gpr[30]!=expected)return 23;}
                            }
                            for(uint32_t wanted=0;wanted<5;wanted++){
                                for(uint32_t i=0;i<count;i++){Memory::Write32(child+targets+i*4,0);Memory::Write32(child+states+i*4,wanted);}
                                Memory::Write32(child+targets+(count-1)*4,links+(n-1)*0x100);
                                c.gpr[3]=child;c.gpr[4]=wanted;BattleAI_FirstMatchingTarget(&c);
                                if(c.gpr[3]!=links+(n-1)*0x100)return 12;
                                Memory::Write32(child+targets+(count-1)*4,0);c.gpr[3]=child;c.gpr[4]=wanted;BattleAI_FirstMatchingTarget(&c);
                                if(c.gpr[3])return 13;
                            }
                            for(uint32_t wanted=3;wanted<=4;wanted++){
                                for(uint32_t i=0;i<count;i++){Memory::Write32(child+targets+i*4,0);Memory::Write32(child+states+i*4,0);}
                                Memory::Write32(child+targets+(count-1)*4,links+(n-1)*0x100);
                                put(positions+16+(n-1)*16,3500);forcedNearState=wanted;
                                c.gpr[3]=child;BattleAI_UpdateTargets(&c);c.gpr[4]=child;
                                if(wanted==4){func_80725AD8(&c);if(c.gpr[29]!=links+(n-1)*0x100)return 24;}
                                else{func_80726044(&c);if(c.gpr[30]!=links+(n-1)*0x100)return 25;}
                                forcedNearState=0;
                            }
                            c.gpr[3]=child;c.gpr[4]=links;func_80725620(&c);if(Memory::Read32(child+8)!=links)return 14;
                            c.gpr[3]=child;func_807253BC(&c);if(Memory::Read32(child+8)||Memory::Read32(child+tail)||!guards())return 15;
                        }
                        c.gpr[3]=child;c.gpr[4]=1;func_8072537C(&c);if(!guards()||c.gpr[1]!=sp)return 16;
                    }
                    auto report=LayoutAccessAudit::ReportJson();
                    if(report.find("\"hits\":[]")==std::string::npos||report.find("\"activeInstances\":0")==std::string::npos){std::fprintf(stderr,"%s\n",report.c_str());return 17;}
                    return allocations==2&&deletions==2&&nearCalls?0:18;
                }
                """);
            var args=TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)+" -DMKW_ENABLE_LAYOUT_ACCESS_AUDIT=1";
            var support=Path.Combine(temp,"translator_test_support.cpp");File.WriteAllText(support,File.ReadAllText(support).Replace("const uint8_t kMkwGuestRegion = 0;","const uint8_t kMkwGuestRegion = 1;",StringComparison.Ordinal));
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",args){RedirectStandardError=true})!){
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);if(!done)compile.Kill(entireProcessTree:true);
                Assert.True(done,"Battle AI target compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());}
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);if(!finished)run.Kill(entireProcessTree:true);
            Assert.True(finished,"Battle AI target execution timed out");Assert.True(run.ExitCode==0,$"Battle AI target returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }finally{Directory.Delete(temp,recursive:true);}
    }
}
