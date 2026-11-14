using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class OnlineVsStateTests
{
    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(32)] [InlineData(48)] [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void OnlineFactoryAndScenarioScoresPreserveAllParticipants(int players)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var manifest=Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json");
        using var doc=JsonDocument.Parse(File.ReadAllText(manifest));
        var layout=new GuestLayouts(doc.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!,players);
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));
        var options=new TranslationOptions(EnableLeafInlining:false,KnownFunctionEntryPoints:map.Addresses.ToHashSet());
        var translator=new FunctionTranslator(image,layoutRelocations:GuestLayoutRelocations.Read(manifest,players));
        var temp=Path.Combine(Path.GetTempPath(),"mkw-online-vs-state-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var plan=GuestLayoutRelocations.Read(manifest,players);
            var header=Path.Combine(temp,"layout.h");File.WriteAllText(header,plan.EmitNativeConstants());
            var glue=Path.Combine(temp,"glue.cpp");
            File.WriteAllText(glue,$"#define MKW_TRANSLATED_PLAYER_CAPACITY {players}\n#define MKW_TRANSLATED_GUEST_LAYOUT_HEADER \"{header}\"\n#include \"{root}/runtime/game/guest_layout_values.cpp\"\n");
            var entries=new uint[]{0x8052d640,0x8052d780,0x8052d940,0x8052d964,0x80538e34,0x80020494,0x8002058c,0x80529b20,0x80529c28,0x8052990c,0x8052a698};
            var sources=new List<string>{glue,Path.Combine(root,"runtime/src/hle/scenario_scores.cpp"),Path.Combine(root,"runtime/src/guest_region.cpp")};
            foreach(var pc in entries)
            {
                var file=Path.Combine(temp,$"{pc:X8}.cpp");File.WriteAllText(file,translator.Translate(pc,options).CxxCode);sources.Add(file);
            }
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var declarations=string.Join("\n",entries.Select(pc=>$"extern \"C\" void func_{pc:X8}(CpuContext*);"));
            var registrations=string.Join("\n",entries.Select(pc=>$"register_entry(0x{pc:X8},func_{pc:X8});"));
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include "mkw_guest_layout.h"
                #include <cstdlib>
                #include <cstring>
                #include <cstdio>
                #include <array>
                #include <vector>
                {{declarations}}
                extern "C" void Scenario_ResetExpandedScores(CpuContext*);
                extern "C" void Scenario_AdvanceExpandedScores(CpuContext*);
                extern "C" void Scenario_ReindexExpandedPreviousRanks(CpuContext*);
                constexpr uint32_t scenario=0x81000000,info=0x81100000,gm=0x81200000,sp=0x81f00000,n={{players}},
                    gmSize={{layout.Symbol("OnlineVSData.size")}},tail={{layout.Symbol("OnlineVSData.tail.offset")}};
                uint32_t allocationSize=0;
                void allocate(CpuContext* c){allocationSize=c->gpr[3];c->gpr[3]=gm;}
                void ignore(CpuContext*){}
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="online VS state";
                    info.rawCpuInvoker=entry;info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);
                    FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    const auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    register_entry(0x80229a48,allocate);register_entry(0x8064f8f0,ignore);
                    // Native identities are PAL; their base functions here are real USA translations.
                    register_entry(0x8052e668,func_80529B20);register_entry(0x8052e770,func_80529C28);
                    register_entry(0x8052f1e0,func_8052A698);
                    const auto& l=MkwGuestLayout::kLayout;
                    CpuContext c{};CpuContextScope scope(&c);c.gpr[1]=sp;c.gpr[2]=0x8038ac20;c.gpr[13]=0x80388880;
                    constexpr uint32_t data=0x81400000;
                    std::memset(Memory::GetPointer(data,l.racedataSize),0x55,l.racedataSize);
                    for(uint32_t i=0;i<16;i++){Memory::Write8(data-16+i,0x77);Memory::Write8(data+l.racedataSize+i,0x77);}
                    c.gpr[3]=data;func_8052990C(&c);
                    for(auto scenarioOffset:{l.racedataMenu,l.racedataAwards})for(uint32_t i=0;i<n;i++){
                        const auto p=data+scenarioOffset+l.scenarioPlayers+i*l.playerStride;
                        if(Memory::Read16(p+0xd8)||Memory::Read16(p+0xde)||Memory::Read8(p+0xe0)!=i+1||Memory::Read8(p+0xe1)!=i+1)return 15;
                    }
                    for(uint32_t i=0;i<l.scenarioSize;i++)if(Memory::Read8(data+l.racedataRace+i)!=0x55)return 16;
                    for(uint32_t i=0;i<16;i++)if(Memory::Read8(data-16+i)!=0x77||Memory::Read8(data+l.racedataSize+i)!=0x77)return 17;
                    const auto menu=data+l.racedataMenu;
                    for(auto mode:{7u,10u})for(uint32_t pattern=0;pattern<3;pattern++){
                        Memory::Write32(menu+l.scenarioSettings+8,mode);
                        std::array<uint32_t,n> ranks{},types{},expected{};
                        for(uint32_t i=0;i<n;i++){
                            const auto p=menu+l.scenarioPlayers+i*l.playerStride;
                            ranks[i]=pattern==0?0:pattern==1?n-i:i==n-1?254:i%4;
                            types[i]=pattern==2&&i%3==1?5:4;expected[i]=i+1;
                            Memory::Write32(p+0x10,types[i]);Memory::Write8(p+0xe1,ranks[i]);
                        }
                        uint32_t next=1;
                        for(uint32_t old=1;old<=n;old++)for(uint32_t i=0;i<n;i++)if(types[i]!=5&&ranks[i]==old)expected[i]=next++;
                        for(uint32_t i=0;i<n;i++)if(types[i]!=5&&!ranks[i])expected[i]=next++;
                        std::vector<uint8_t> before(l.scenarioSize);std::memcpy(before.data(),Memory::GetPointer(menu,l.scenarioSize),l.scenarioSize);
                        c.gpr[3]=menu;Scenario_ReindexExpandedPreviousRanks(&c);
                        for(uint32_t i=0;i<n;i++)if(Memory::Read8(menu+l.scenarioPlayers+i*l.playerStride+0xe1)!=expected[i])return 18;
                        for(uint32_t o=0;o<l.scenarioSize;o++){
                            const bool rank=o>=l.scenarioPlayers&&o<l.scenarioPlayers+n*l.playerStride&&(o-l.scenarioPlayers)%l.playerStride==0xe1;
                            if(!rank&&Memory::Read8(menu+o)!=before[o])return 19;
                        }
                        Memory::Write32(menu+l.scenarioSettings+8,6);
                        std::memcpy(before.data(),Memory::GetPointer(menu,l.scenarioSize),l.scenarioSize);
                        c.gpr[3]=menu;Scenario_ReindexExpandedPreviousRanks(&c);
                        if(std::memcmp(before.data(),Memory::GetPointer(menu,l.scenarioSize),l.scenarioSize))return 20;
                    }
                    for(uint32_t locals:{1u,2u}){
                        std::memset(Memory::GetPointer(scenario,l.scenarioSize),0x55,l.scenarioSize);
                        for(uint32_t i=0;i<16;i++){Memory::Write8(scenario-16+i,0x77);Memory::Write8(scenario+l.scenarioSize+i,0x77);
                            Memory::Write8(gm-16+i,0x77);Memory::Write8(gm+gmSize+i,0x77);}
                        c.gpr[3]=scenario;Scenario_ResetExpandedScores(&c);
                        for(uint32_t i=0;i<n;i++){
                            const auto p=scenario+l.scenarioPlayers+i*l.playerStride;
                            if(Memory::Read16(p+0xd8)||Memory::Read16(p+0xde)||Memory::Read8(p+0xe0)!=i+1||Memory::Read8(p+0xe1)!=i+1)return 4;
                            Memory::Write8(p+0xe2,n-i);Memory::Write16(p+0xda,1000+i);
                            Memory::Write32(p+0x10,i==n-1||(locals==2&&i==0)?0:4);
                            // Unrelated player fields retain their initial pattern.
                            for(uint32_t o=0;o<l.playerStride;o++)if(!(o>=0x10&&o<0x14)&&!(o>=0xd8&&o<0xdc)&&!(o>=0xde&&o<0xe3)&&Memory::Read8(p+o)!=0x55)return 5;
                        }
                        c.gpr[3]=scenario;Scenario_AdvanceExpandedScores(&c);
                        if(c.gpr[3]!=1)return 6;
                        for(uint32_t i=0;i<n;i++){
                            const auto p=scenario+l.scenarioPlayers+i*l.playerStride;
                            if(Memory::Read8(p+0xe1)!=n-i||Memory::Read16(p+0xd8)!=1000+i)return 7;
                        }
                        // Reset race-number wrap behavior applies independently of player capacity.
                        Memory::Write8(scenario+l.scenarioSettings+0x24,100);
                        c.gpr[3]=scenario;Scenario_AdvanceExpandedScores(&c);if(c.gpr[3]!=0)return 8;
                        const auto racedata=scenario-l.racedataRace;
                        Memory::Write32(0x809b8f68,racedata);Memory::Write8(scenario+4,n);
                        std::memset(Memory::GetPointer(gm,gmSize),0x55,gmSize);
                        c.gpr[3]=info;c.gpr[4]=7;func_8052D640(&c);
                        if(c.gpr[3]!=gm||allocationSize!=gmSize||Memory::Read32(gm+4)!=info)return 9;
                        for(uint32_t i=0;i<n;i++)if(Memory::Read32(gm+8+i*20)!=0x808ae694)return 10;
                        c.gpr[3]=gm;func_80538E34(&c);
                        if(Memory::Read8(gm+tail+16)!=(locals==2?0:n-1)||
                            Memory::Read8(gm+tail+17)!=(locals==2?n-1:255)||Memory::Read8(gm+tail+19)!=locals)return 11;
                        // Factory's tail polymorphic objects must survive the last participant's initialization.
                        if(Memory::Read32(gm+tail+52)!=0x808aed10||Memory::Read32(gm+tail+116)!=0x808aecb0)return 12;
                        for(uint32_t i=0;i<16;i++)if(Memory::Read8(scenario-16+i)!=0x77||Memory::Read8(scenario+l.scenarioSize+i)!=0x77||
                            Memory::Read8(gm-16+i)!=0x77||Memory::Read8(gm+gmSize+i)!=0x77)return 13;
                        if(c.gpr[1]!=sp)return 14;
                    }
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(45000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Online VS state compilation timed out");
                Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(5000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Online VS state execution timed out");
            Assert.True(run.ExitCode==0,$"Online VS state lifecycle returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
