using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class SelectNegotiationTests
{
    [Theory]
    // Live LAN uses 32-bit console masks; larger-capacity storage is tested separately.
    [InlineData(12)] [InlineData(13)] [InlineData(24)]
    [Trait("Requires","USA game assets and Clang")]
    public void RealVotingChecksAllTeamWordsAndCopiesTheHostsCompleteSettings(int players)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var manifest=Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json");
        using var doc=JsonDocument.Parse(File.ReadAllText(manifest));
        var layout=new GuestLayouts(doc.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!,players);
        var plan=GuestLayoutRelocations.Read(manifest,players);
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));
        var translator=new FunctionTranslator(image,layoutRelocations:plan);
        var temp=Path.Combine(Path.GetTempPath(),"mkw-select-negotiation-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var sources=new List<string>();var entries=new uint[]{0x80659574,0x80658e20};
            foreach(var pc in entries){var path=Path.Combine(temp,$"{pc:X8}.cpp");File.WriteAllText(path,translator.Translate(pc,
                new TranslationOptions(EnableLeafInlining:false,KnownFunctionEntryPoints:map.Addresses.ToHashSet())).CxxCode);sources.Add(path);}
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <array>
                #include <algorithm>
                #include <cstdio>
                #include <cstring>
                extern "C" void func_80659574(CpuContext*);
                extern "C" void func_80658E20(CpuContext*);
                constexpr uint32_t capacity={{players}},words={{layout.Symbol("RKNetSelectPacket.teams.count")}},
                    size={{layout.Symbol("RKNetSelectHandler.size")}},send={{layout.Symbol("RKNetSelectHandler.send.offset")}},
                    received={{layout.Symbol("RKNetSelectHandler.received.offset")}},packetSize={{layout.Symbol("RKNetSelectPacket.size")}},
                    teams={{layout.Symbol("RKNetSelectPacket.teams.offset")}},flags={{layout.Symbol("RKNetSelectPacket.flags.offset")}},
                    state={{layout.Symbol("RKNetSelectHandler.flags.offset")}},controllerSize={{layout.Symbol("RKNetController.size")}},
                    currentSub={{layout.Symbol("RKNetController.currentSub.offset")}},subs={{layout.Symbol("RKNetController.subs.offset")}},subStride={{layout.Symbol("RKNetSub.size")}},
                    self=0x81010000,controller=0x81050000,stack=0x81f00000,peer=std::min(capacity-1,31u);
                void entry(uint32_t address,void(*fn)(CpuContext*)){
                    TranslatedFunctionInfo i{};i.address=address;i.name="SELECT negotiation fixture";i.rawCpuInvoker=fn;
                    i.entryPoint=reinterpret_cast<void*>(fn);TranslatedFunctionRegistry::Register(i);}
                void connected(CpuContext* c){if(c->gpr[3]!=controller)std::abort();c->gpr[3]=1;}
                void copy(CpuContext* c){if(c->gpr[5]!=capacity)std::abort();std::memmove(Memory::GetPointer(c->gpr[3],capacity),Memory::GetPointer(c->gpr[4],capacity),capacity);}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);auto input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto n=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);std::fclose(input);if(n!={{image.Memory.Length}})return 3;
                    entry(0x80652a78,connected);entry(0x80005f34,copy);Memory::Write32(0x809bd918,controller);
                    CpuContext c{};CpuContextScope scope(&c);
                    auto reset=[&](uint32_t snapshot,bool host){
                        std::memset(Memory::GetPointer(self-16,size+32),0,size+32);
                        std::memset(Memory::GetPointer(controller,controllerSize),0,controllerSize);
                        Memory::Write32(controller+currentSub,snapshot);auto sub=controller+subs+snapshot*subStride;
                        Memory::Write32(sub+16,1u|(1u<<peer));Memory::Write8(sub+33,0);Memory::Write8(sub+34,host?0:peer);
                        Memory::Write32(self,1);Memory::Write32(self+state,1u<<peer);
                        Memory::Write32(self+send+32,0x12345678);Memory::Write8(self+send+flags+3,1);
                        auto packet=self+received+peer*packetSize;
                        Memory::Write32(packet+32,0x12345678);Memory::Write8(packet+flags+3,1);
                        for(uint32_t r=14;r<32;r++)c.gpr[r]=0x12340000+r;
                        c.gpr[1]=stack;c.gpr[2]=0x8038ac20;c.gpr[13]=0x80388880;c.gpr[3]=self;c.lr=0x80006000;
                    };
                    auto intact=[&](){if(c.gpr[1]!=stack || c.lr!=0x80006000)return false;for(uint32_t r=14;r<32;r++)if(c.gpr[r]!=0x12340000+r)return false;return true;};
                    for(uint32_t snapshot=0;snapshot<2;snapshot++){
                        // Only a later team word can be nonzero; it still makes settings ready.
                        const uint32_t selectedWord=peer/12,selectedBit=(peer%12)*2;
                        for(uint32_t changed=0;changed<=words;changed++){
                            reset(snapshot,true);auto packet=self+received+peer*packetSize;
                            Memory::Write32(self+send+teams+selectedWord*4,1u<<selectedBit);
                            Memory::Write32(packet+teams+selectedWord*4,1u<<selectedBit);
                            if(changed<words)Memory::Write32(packet+teams+changed*4,Memory::Read32(packet+teams+changed*4)^2u);
                            func_80659574(&c);if(!intact())return 4;
                            bool same=changed==words;
                            if(Memory::Read32(self+state+8)!=(same?1u<<peer:0) || Memory::Read8(self+send+flags+1)!=(same?1:0)){
                                std::fprintf(stderr,"ready capacity=%u changed=%u mask=%08x state=%u\n",capacity,changed,Memory::Read32(self+state+8),Memory::Read8(self+send+flags+1));return 5;}
                        }
                        // Every nonzero word must make EveryoneHasAccurateRaceSettings include self.
                        for(uint32_t w=0;w<words;w++){
                            reset(snapshot,true);Memory::Write32(self+state+8,1u<<peer);Memory::Write32(self+send+teams+w*4,1);
                            func_80658E20(&c);if(c.gpr[3]!=1 || !intact())return 6;
                        }
                        reset(snapshot,true);Memory::Write32(self+state+8,1u<<peer);func_80658E20(&c);if(c.gpr[3]!=0 || !intact())return 7;
                        // Client imports all host team words and the following moved flag bytes.
                        reset(snapshot,false);auto packet=self+received+peer*packetSize;
                        Memory::Write8(packet+flags+1,1);
                        for(uint32_t w=0;w<words;w++)Memory::Write32(packet+teams+w*4,0x80000001u+w);
                        func_80659574(&c);if(!intact())return 8;
                        for(uint32_t w=0;w<words;w++)if(Memory::Read32(self+send+teams+w*4)!=0x80000001u+w)return 9;
                        if(Memory::Read8(self+send+flags+1)!=1 || Memory::Read32(self+send+32)!=0x12345678 || Memory::Read8(self+send+flags+3)!=1)return 10;
                    }
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!){
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(120000);if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"SELECT negotiation compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());}
            using var run=Process.Start(new ProcessStartInfo(exe,binary){RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);if(!finished)run.Kill(entireProcessTree:true);
            Assert.True(finished,"SELECT negotiation execution timed out");Assert.True(run.ExitCode==0,$"SELECT negotiation returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
