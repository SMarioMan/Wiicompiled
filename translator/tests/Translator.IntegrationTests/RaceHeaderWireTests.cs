using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class RaceHeaderWireTests
{
    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(48)] [InlineData(100)]
    [Trait("Requires","USA game assets and Clang")]
    public void RealHeaderDefaultsAndImportsUseTheWholeParticipantMap(int players)
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
        var temp=Path.Combine(Path.GetTempPath(),"mkw-rh1-wire-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var sources=new List<string>();var entries=new uint[]{0x8065ca70,0x8065cb28,0x8065c85c,0x80650880,0x80650cdc};
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
                extern "C" void func_80650880(CpuContext*);
                extern "C" void func_80650CDC(CpuContext*);
                extern "C" void func_8065CA70(CpuContext*);
                extern "C" void func_8065CB28(CpuContext*);
                extern "C" void func_8065C85C(CpuContext*);
                constexpr uint32_t capacity={{players}},packetSize={{layout.Symbol("RKNetWireRH1.size")}},
                    flags={{layout.Symbol("RKNetWireRH1.flags.offset")}},mapOffset={{layout.Symbol("RKNetWireRH1.playerAids.offset")}},
                    controllerSize={{layout.Symbol("RKNetController.size")}},size={{layout.Symbol("RKNetRH1Handler.size")}},
                    records={{layout.Symbol("RKNetRH1Handler.records.offset")}},recordStride={{layout.Symbol("RKNetRH1Data.size")}},
                    ids={{layout.Symbol("RKNetRH1Data.playerAids.offset")}},tail={{layout.Symbol("RKNetRH1Data.tail.offset")}},
                    currentSub={{layout.Symbol("RKNetController.currentSub.offset")}},subs={{layout.Symbol("RKNetController.subs.offset")}},subStride={{layout.Symbol("RKNetSub.size")}},
                    send={{layout.Symbol("RKNetController.splitSend.offset")}},receive={{layout.Symbol("RKNetController.splitReceived.offset")}},
                    rowStride={{layout.Symbol("RKNetPointerRow.size")}},sendIndices={{layout.Symbol("RKNetController.sendIndices.offset")}},
                    receiveIndices={{layout.Symbol("RKNetController.receiveIndices.offset")}},
                    self=0x81010000,controller=0x81050000,split=0x810e0000,holder=0x810e0100,packet=0x810e1000,stack=0x81f00000;
                uint32_t state=0,exports=0;bool creating=false;
                void local(CpuContext* c){c->gpr[3]=0;}
                void entry(uint32_t address,void(*fn)(CpuContext*)){
                    TranslatedFunctionInfo i{};i.address=address;i.name="RH1 wire fixture";i.rawCpuInvoker=fn;
                    i.entryPoint=reinterpret_cast<void*>(fn);TranslatedFunctionRegistry::Register(i);}
                void copy(CpuContext* c){if(c->gpr[5]!=capacity)std::abort();std::memmove(Memory::GetPointer(c->gpr[3],capacity),Memory::GetPointer(c->gpr[4],capacity),capacity);}
                void export_packet(CpuContext* c){
                    if(creating && c->gpr[3]==holder+256){if(c->gpr[5]!=64)std::abort();return;}
                    if(c->gpr[3]!=holder || c->gpr[5]!=packetSize)std::abort();++exports;auto p=c->gpr[4];
                    if(creating){for(uint32_t i=0;i<capacity;i++)if(Memory::Read8(p+mapOffset+i)!=(i*7)%capacity){std::fprintf(stderr,"map %u got %u\n",i,Memory::Read8(p+mapOffset+i));std::abort();}
                        uint32_t teamBits=0;for(uint32_t i=0;i<capacity;i++)if(i%2==0)teamBits|=1u<<i;
                        if(Memory::Read32(p+8)!=teamBits){std::fprintf(stderr,"teams got %08x expected %08x\n",Memory::Read32(p+8),teamBits);std::abort();}
                        if(Memory::Read8(p+23)!=state)std::abort();
                        if(Memory::Read8(p+flags)!=2){std::fprintf(stderr,"engine got %u\n",Memory::Read8(p+flags));std::abort();}return;}
                    for(uint32_t i=0;i<packetSize;i++){
                        uint8_t expected=i>=mapOffset&&i<mapOffset+capacity?255:i==22?255:i==23?state:i>=14&&i<18?255:i>=18&&i<22?0x66:i>flags?0x66:0;
                        if(Memory::Read8(p+i)!=expected){std::fprintf(stderr,"default byte %u got %02x expected %02x\n",i,Memory::Read8(p+i),expected);std::abort();}
                    }
                }
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);auto input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto n=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);std::fclose(input);if(n!={{image.Memory.Length}})return 3;
                    entry(0x806558d0,local);entry(0x80655ec4,export_packet);entry(0x80005f34,copy);Memory::Write32(0x809bd918,controller);
                    Memory::Write32(split+4,holder);Memory::Write32(holder,packet);
                    CpuContext c{};CpuContextScope scope(&c);
                    auto setup=[&](){c={};c.gpr[1]=stack;c.gpr[2]=0x8038ac20;c.gpr[13]=0x80388880;c.lr=0x80006000;
                        for(uint32_t r=14;r<32;r++)c.gpr[r]=0x12340000+r;
                        std::memset(Memory::GetPointer(stack-1024,1024),0x66,1024);Memory::Write32(stack+8,0xdeadbeef);};
                    auto intact=[&](){if(c.gpr[1]!=stack || c.lr!=0x80006000 || Memory::Read32(stack+8)!=0xdeadbeef)return false;
                        for(uint32_t r=14;r<32;r++)if(c.gpr[r]!=0x12340000+r)return false;return true;};
                    for(uint32_t row=0;row<2;row++)for(uint32_t aid=0;aid<capacity;aid++){
                        Memory::Write32(controller+sendIndices+aid*4,row);Memory::Write32(controller+send+row*rowStride+aid*4,split);
                        for(state=1;state<=2;state++){setup();c.gpr[4]=aid;
                            if(state==1)func_8065CA70(&c);else func_8065CB28(&c);
                            if(!intact())return 4;
                        }
                    }
                    if(exports!=capacity*4)return 5;
                    if(capacity<=32){
                        constexpr uint32_t data=0x81100000,info=0x81150000,manager=0x81160000,params=0x81170000,user=0x81180000;
                        Memory::Write32(0x809b8f68,data);Memory::Write32(0x809b8f70,info);Memory::Write32(0x809bd508,manager);Memory::Write32(manager+0x98,params);Memory::Write32(0x809bd958,user);
                        std::memset(Memory::GetPointer(data,{{layout.Symbol("Racedata.size")}}),0,{{layout.Symbol("Racedata.size")}});
                        Memory::Write32(data+{{layout.Symbol("Racedata.race.offset+Scenario.settings.offset")}}+0,8);
                        Memory::Write32(data+{{layout.Symbol("Racedata.race.offset+Scenario.settings.offset")}}+4,2);
                        for(uint32_t i=0;i<capacity;i++)Memory::Write32(data+{{layout.Symbol("Racedata.race.offset+Scenario.players.offset")}}+i*{{layout.Symbol("Player.size")}}+0xcc,i%2);
                        for(uint32_t i=0;i<capacity;i++)Memory::Write8(controller+{{layout.Symbol("RKNetController.playerAids.offset")}}+i,(i*7)%capacity);
                        Memory::Write32(split+16,holder+256);Memory::Write8(user,0);
                        for(uint32_t snapshot=0;snapshot<2;snapshot++)for(uint32_t row=0;row<2;row++){
                            auto aid=capacity-1,sub=controller+subs+snapshot*subStride;Memory::Write32(controller+currentSub,snapshot);
                            Memory::Write32(sub+16,1u|(1u<<aid));Memory::Write8(sub+32,1);Memory::Write8(sub+33,0);
                            Memory::Write32(controller+sendIndices+aid*4,row);Memory::Write32(controller+send+row*rowStride+aid*4,split);
                            creating=true;state=0;auto before=exports;setup();c.gpr[3]=self;func_80650880(&c);if(!intact() || exports!=before+1){std::fprintf(stderr,"race export capacity=%u exports=%u before=%u intact=%u sp=%x lr=%x\n",capacity,exports,before,intact(),c.gpr[1],c.lr);return 14;}
                            before=exports;state=2;setup();c.gpr[3]=self;func_80650CDC(&c);if(!intact() || exports!=before+1){std::fprintf(stderr,"select export capacity=%u exports=%u before=%u intact=%u\n",capacity,exports,before,intact());return 15;}creating=false;
                        }
                    }
                    // Actual imports are limited to the currently representable console masks.
                    if(capacity<=32)for(uint32_t snapshot=0;snapshot<2;snapshot++)for(uint32_t row=0;row<2;row++)for(uint32_t invalid=0;invalid<2;invalid++){
                        auto aid=capacity-1,sub=controller+subs+snapshot*subStride;
                        std::memset(Memory::GetPointer(self-16,size+32),0x55,size+32);Memory::Write32(self+4,0);Memory::Write32(self+8,0);Memory::Write32(self+12,0);
                        Memory::Write32(controller+currentSub,snapshot);Memory::Write32(sub+16,1u<<aid);Memory::Write8(sub+33,0);
                        Memory::Write32(controller+receiveIndices+aid*32+4,row);Memory::Write32(controller+receive+row*rowStride+aid*4,split);
                        Memory::Write32(holder+8,packetSize-invalid);std::memset(Memory::GetPointer(packet,packetSize),0,packetSize);
                        Memory::Write32(packet,0x12345678);Memory::Write32(packet+4,0x87654321);Memory::Write32(packet+8,0x10203040);
                        Memory::Write16(packet+14,0x3344);Memory::Write16(packet+16,0x5566);Memory::Write8(packet+20,7);Memory::Write8(packet+21,9);
                        Memory::Write8(packet+22,0x42);Memory::Write8(packet+23,1);Memory::Write8(packet+flags,3);
                        for(uint32_t i=0;i<capacity;i++)Memory::Write8(packet+mapOffset+i,(i*7)%capacity);
                        setup();c.gpr[3]=self;func_8065C85C(&c);if(!intact())return 6;
                        if(Memory::Read32(self+4)!=(1u<<aid) || Memory::Read32(self+8)!=(invalid?0:1u<<aid) || Memory::Read32(self+12)!=(invalid?0:1u<<aid))return 7;
                        auto record=self+records+aid*recordStride;
                        if(!invalid){
                            if(Memory::Read32(record)!=0x12345678 || Memory::Read32(record+20)!=0x42 || Memory::Read32(record+24)!=0x10203040 || Memory::Read32(record+28)!=0x87654321)return 8;
                            for(uint32_t i=0;i<capacity;i++)if(Memory::Read8(record+ids+i)!=(i*7)%capacity)return 9;
                            if(Memory::Read8(record+tail)!=3 || Memory::Read8(record+tail+1)!=7 || Memory::Read8(record+tail+2)!=9)return 10;
                            if(Memory::Read32(record+4)!=0x44 || Memory::Read32(record+12)!=0x33 || Memory::Read32(record+8)!=0x66 || Memory::Read32(record+16)!=0x55)return 11;
                        }else for(uint32_t i=0;i<recordStride;i++)if(Memory::Read8(record+i)!=0x55)return 12;
                        for(uint32_t i=0;i<16;i++)if(Memory::Read8(self-16+i)!=0x55 || Memory::Read8(self+size+i)!=0x55)return 13;
                    }
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!){
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(120000);if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"RH1 wire compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());}
            using var run=Process.Start(new ProcessStartInfo(exe,binary){RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);if(!finished)run.Kill(entireProcessTree:true);
            Assert.True(finished,"RH1 wire execution timed out");Assert.True(run.ExitCode==0,$"RH1 wire returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
