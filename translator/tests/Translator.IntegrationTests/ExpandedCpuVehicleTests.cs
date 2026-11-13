using System.Diagnostics;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;
namespace Translator.Tests;
public class ExpandedCpuVehicleTests
{
    [Theory]
    [InlineData(13)] [InlineData(24)] [InlineData(100)]
    public void ActualVehicleSelectorAssignsEveryExpandedCpuAWeightCompatibleVehicle(int players)
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
            var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
            var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));
            var translator=new FunctionTranslator(image,layoutRelocations:GuestLayoutRelocations.Read(Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json"),players));
            uint[] entries={0x8082a584,0x80809f90,0x80809dc4,0x80809d3c,0x8080a104,0x8080a208};
            var translated=new List<string>();foreach(var pc in entries){var file=Path.Combine(temp,$"{pc:X8}.cpp");File.WriteAllText(file,translator.Translate(pc,new TranslationOptions(EnableLeafInlining:false,KnownFunctionEntryPoints:map.Addresses.ToHashSet())).CxxCode);translated.Add(file);}
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include "mkw_guest_layout.h"
                #include <array>
                #include <cstring>
                #include <cstdio>
                {{string.Join("\n",entries.Select(pc=>$"extern \"C\" void func_{pc:X8}(CpuContext*);"))}}
                extern "C" void GetActiveRacedataPlayers_Expanded(CpuContext*);
                extern "C" void CharacterSelect_AssignExpandedCpuCharacters(CpuContext*);
                constexpr uint32_t data=0x81010000,stack=0x81f00000,capacity={{players}},manager=0x81080000,section=0x81090000;
                uint32_t rngCalls=0;
                void original(CpuContext*){std::abort();}
                void unlocked(CpuContext* c){c->gpr[3]=1;}
                void rng_init(CpuContext*){}
                void rng_destroy(CpuContext*){}
                void rng(CpuContext* c){if(!c->gpr[4] || c->gpr[4]>24)std::abort();c->gpr[3]=(rngCalls++*7)%c->gpr[4];}
                void locals(CpuContext* c){c->gpr[3]=1;}
                void entry(uint32_t address,void(*fn)(CpuContext*)){TranslatedFunctionInfo info{};info.address=address;info.name="actual vehicle fixture";
                    info.rawCpuInvoker=fn;info.entryPoint=reinterpret_cast<void*>(fn);TranslatedFunctionRegistry::Register(info);}
                int main(int argc,char** argv){if(argc!=2)return 1;Memory::Init(32*1024*1024);auto file=std::fopen(argv[1],"rb");if(!file)return 2;
                    auto bytes=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},file);std::fclose(file);if(bytes!={{image.Memory.Length}})return 3;
                    const auto& l=MkwGuestLayout::kLayout;
                    Memory::Write32(0x809bd728,data);Memory::Write32(0x809b8f68,data);auto base=data+l.racedataMenu+l.scenarioPlayers;
                    Memory::Write32(0x809bd508,manager);Memory::Write32(manager,section);Memory::Write32(section,0x20);
                    {{string.Join("\n",entries.Select(pc=>$"entry(0x{pc:X8},func_{pc:X8});"))}}
                    entry(0x80860500,original);entry(0x8083ec28,original);
                    entry(0x8081cd3c,func_80809F90);entry(0x8081d020,unlocked);entry(0x80555464,rng_init);entry(0x80555538,rng_destroy);entry(0x805555cc,rng);
                    entry(0x8083b8b4,GetActiveRacedataPlayers_Expanded);entry(0x8083b9b0,locals);
                    entry(0x8054f444,rng_init);entry(0x8054f518,rng_destroy);entry(0x8054f5ac,rng);entry(0x80550e68,unlocked);
                    CpuContext c{};CpuContextScope scope(&c);c.gpr[1]=stack;c.gpr[2]=0x8038ac20;c.gpr[13]=0x80388880;c.lr=0x80006000;
                    for(uint32_t r=14;r<32;r++)c.gpr[r]=0x12340000+r;
                    for(uint32_t i=0;i<capacity;i++){auto p=base+i*l.playerStride;Memory::Write32(p+0x10,i?1:0);Memory::Write32(p+0xc,i?0xffffffffu:0);Memory::Write32(p+8,0xffffffffu);}
                    CharacterSelect_AssignExpandedCpuCharacters(&c);func_8082A584(&c);
                    if(c.gpr[1]!=stack || c.lr!=0x80006000)return 4;
                    for(uint32_t r=14;r<32;r++)if(c.gpr[r]!=0x12340000+r)return 5;
                    std::array<uint32_t,24> uses{};
                    for(uint32_t i=0;i<capacity;i++){
                        auto p=base+i*l.playerStride,ch=Memory::Read32(p+0xc),kart=Memory::Read32(p+8);if(ch>=24)return 6;++uses[ch];
                        if(!i){if(ch || kart!=0xffffffffu)return 7;continue;}
                        c.gpr[3]=ch;func_80809F90(&c);auto weight=c.gpr[3];c.gpr[3]=kart;func_80809DC4(&c);
                        if(weight!=c.gpr[3] || kart>=36){std::fprintf(stderr,"slot %u ch %u kart %u weights %u/%u\n",i,ch,kart,weight,c.gpr[3]);return 8;}
                    }
                    for(auto count:uses)if(count>(capacity+23)/24)return 9;
                    return 0;
                }
                """);
            var sources=translated.Concat(new[]{glue,Path.Combine(root,"runtime/src/hle/cpu_roster.cpp"),Path.Combine(root,"runtime/src/guest_region.cpp")});
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!) {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);if(!done)compile.Kill(entireProcessTree:true);
                Assert.True(done,"CPU roster compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());}
            using var run=Process.Start(new ProcessStartInfo(exe,binary){RedirectStandardError=true})!;var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"CPU roster execution timed out");Assert.True(run.ExitCode==0,$"Roster returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        } finally {Directory.Delete(temp,recursive:true);}
    }
}
