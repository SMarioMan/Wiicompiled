using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class SelectRaceSetupTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealOnlineRaceSetupClearsEveryAbsentSlot(int players)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var manifest=Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json");
        using var document=JsonDocument.Parse(File.ReadAllText(manifest));
        var layout=new GuestLayouts(document.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!,players);
        var plan=GuestLayoutRelocations.Read(manifest,players);
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));
        var translator=new FunctionTranslator(image,layoutRelocations:plan);
        var temp=Path.Combine(Path.GetTempPath(),"mkw-select-setup-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var entries=new uint[]{0x8061db10,0x8061e2b8};
            var fixtures=new HashSet<uint>{0x8052990c,0x8052d19c,0x8054f444,0x8054f518,0x8054f5ac,0x805c5098,0x805c565c,0x805c57e4,0x805c5c28,0x805cdbfc,0x805cdf6c,0x805d9e64,0x805d9fd4,0x805da1a0,0x805da650,0x80655738,0x80658514,0x80658534,0x80658598,0x806585e8,0x80658688,0x806586d0,0x8065ae04};
            var sources=new List<string>();
            foreach(var pc in entries){var path=Path.Combine(temp,$"{pc:X8}.cpp");
                File.WriteAllText(path,translator.Translate(pc,new TranslationOptions(KnownFunctionEntryPoints:map.Addresses.ToHashSet(),LeafInliningBlockedTargets:fixtures)).CxxCode);sources.Add(path);}
            var declarations=string.Join("\n",entries.Select(pc=>$"extern \"C\" void func_{pc:X8}(CpuContext*);"));
            var registrations=string.Join("\n",entries.Select(pc=>$"register_entry(0x{pc:X8},func_{pc:X8});"));
            var fixtureRegistrations=string.Join("\n",fixtures.Select(pc=>$"register_entry(0x{pc:X8},stub);").Where(s=>!s.Contains("805DA1A0")));
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <array>
                #include <cstdio>
                #include <cstring>
                {{declarations}}
                constexpr uint32_t capacity={{players}},size={{layout.Symbol("SectionParams.size")}},
                    friends={{layout.Symbol("SectionParams.online.offset+OnlineParams.friendIndices.offset")}},
                    regions={{layout.Symbol("SectionParams.online.offset+OnlineParams.regionIds.offset")}},
                    controllerSize={{layout.Symbol("RKNetController.size")}},aidMap={{layout.Symbol("RKNetController.playerAids.offset")}},
                    racedataSize={{layout.Symbol("Racedata.size")}},menuPlayers={{layout.Symbol("Racedata.menu.offset+Scenario.players.offset")}},
                    root=0x81010000,controller=0x81030000,racedata=0x81060000,stage=0x810c0000,stack=0x81f00000;
                std::array<uint32_t,capacity> deleted{};
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="online setup fixture";info.rawCpuInvoker=entry;
                    info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                void stub(CpuContext* c){c->gpr[3]=0;}
                void delete_mii(CpuContext* c){if(c->gpr[3]!=root+392 || c->gpr[4]>=capacity)std::abort();deleted[c->gpr[4]]++;}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    {{fixtureRegistrations}}
                    register_entry(0x805da1a0,delete_mii);
                    Memory::Write32(0x809bd508,0x810a0000);Memory::Write32(0x810a0000,0x810a1000);
                    Memory::Write32(0x810a0098,root);Memory::Write32(0x810a1000,0);Memory::Write32(0x810a1404,0x810b0000);
                    Memory::Write32(0x809bd918,controller);Memory::Write32(0x809b8f68,racedata);
                    std::memset(Memory::GetPointer(root-16,size+32),0x55,size+32);
                    std::memset(Memory::GetPointer(controller,controllerSize),0,controllerSize);
                    std::memset(Memory::GetPointer(controller+aidMap,capacity),255,capacity);
                    std::memset(Memory::GetPointer(racedata-16,racedataSize+32),0x55,racedataSize+32);
                    Memory::Write32(root+{{layout.Symbol("SectionParams.online.offset")}},0);
                    CpuContext ctx{};ctx.gpr[1]=stack;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                    for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                    ctx.gpr[3]=stage;func_8061DB10(&ctx);
                    for(uint32_t p=0;p<capacity;p++){
                        if(deleted[p]!=1 || Memory::Read32(racedata+menuPlayers+p*240+16)!=5 || Memory::Read32(root+regions+p*4)!=15){
                            std::fprintf(stderr,"slot %u deleted=%u type=%u region=%u\n",p,deleted[p],Memory::Read32(racedata+menuPlayers+p*240+16),Memory::Read32(root+regions+p*4));return 4;}
                    }
                    ctx.gpr[3]=stage;func_8061E2B8(&ctx);
                    for(uint32_t p=0;p<capacity;p++)if(Memory::Read8(root+friends+p)!=255)return 5;
                    if(Memory::Read8(root+friends+capacity)!=0x55)return 6;
                    for(uint32_t i=0;i<16;i++)if(Memory::Read8(root-16+i)!=0x55 || Memory::Read8(root+size+i)!=0x55 ||
                        Memory::Read8(racedata-16+i)!=0x55 || Memory::Read8(racedata+racedataSize+i)!=0x55)return 7;
                    for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r)return 8;
                    if(ctx.gpr[1]!=stack)return 9;
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Online setup storage compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Online setup storage execution timed out");
            Assert.True(run.ExitCode==0,$"Online setup storage returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
