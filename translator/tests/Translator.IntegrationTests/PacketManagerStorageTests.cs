using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class PacketManagerStorageTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealPacketManagerInitializesUniformTimersAndReadsExpandedPlayerMappings(int players)
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
        var temp=Path.Combine(Path.GetTempPath(),"mkw-packet-manager-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var entries=new uint[]{0x8064ed80,0x8064f96c,0x8064ee50,0x80650020,0x80006038,0x80005f84};
            var fixtures=new HashSet<uint>{0x80229a48,0x80656ecc,0x8065cd74,0x80652ab8};
            var sources=new List<string>();
            foreach(var pc in entries){var path=Path.Combine(temp,$"{pc:X8}.cpp");
                File.WriteAllText(path,translator.Translate(pc,new TranslationOptions(KnownFunctionEntryPoints:map.Addresses.ToHashSet(),LeafInliningBlockedTargets:fixtures)).CxxCode);sources.Add(path);}
            var declarations=string.Join("\n",entries.Select(pc=>$"extern \"C\" void func_{pc:X8}(CpuContext*);"));
            var registrations=string.Join("\n",entries.Select(pc=>$"register_entry(0x{pc:X8},func_{pc:X8});"));
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <array>
                #include <cstdio>
                #include <cstring>
                {{declarations}}
                constexpr uint32_t capacity={{players}},size={{layout.Symbol("RKNetPacketMgr.size")}},
                    timers={{layout.Symbol("RKNetPacketMgr.playerRH1Timers.offset")}},
                    controllerSize={{layout.Symbol("RKNetController.size")}},
                    subs={{layout.Symbol("RKNetController.subs.offset")}},subStride={{layout.Symbol("RKNetSub.size")}},
                    currentSub={{layout.Symbol("RKNetController.currentSub.offset")}},
                    aidMap={{layout.Symbol("RKNetController.playerAids.offset")}},
                    receive={{layout.Symbol("RKNetController.splitReceived.offset")}},rowStride={{layout.Symbol("RKNetPointerRow.size")}},
                    indices={{layout.Symbol("RKNetController.receiveIndices.offset")}},indexStride={{layout.Symbol("RKNetReceivedIndices.size")}},
                    raceSettings={{layout.Symbol("Racedata.race.offset+Scenario.settings.offset")}},
                    root=0x81010000,controller=0x81030000,racedata=0x81060000,stack=0x81f00000;
                uint32_t allocations=0,itemCreates=0,eventCreates=0,clears=0;
                void allocate(CpuContext* c){if(c->gpr[3]!=size || allocations++)std::abort();c->gpr[3]=root;}
                void create_item(CpuContext*){itemCreates++;}
                void create_event(CpuContext*){eventCreates++;}
                void clear_packets(CpuContext* c){if(c->gpr[3]!=controller)std::abort();clears++;}
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="room packet fixture";info.rawCpuInvoker=entry;
                    info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    register_entry(0x80229a48,allocate);register_entry(0x80656ecc,create_item);
                    register_entry(0x8065cd74,create_event);register_entry(0x80652ab8,clear_packets);
                    Memory::Write32(0x809bd918,controller);Memory::Write32(0x809b8f68,racedata);
                    Memory::Write32(0x80381c80,0x81090000);Memory::Write32(0x81090084,0x1234);
                    Memory::Write32(0x809bd508,0x810a0000);Memory::Write32(0x810a0098,0x810a1000);
                    Memory::Write32(0x810a12d0,3);
                    std::memset(Memory::GetPointer(controller-16,controllerSize+32),0,controllerSize+32);
                    CpuContext ctx{};ctx.gpr[1]=stack;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                    for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                    std::array<uint8_t,size> expected{};
                    auto word=[&](uint32_t at,uint32_t value){for(uint32_t b=0;b<4;b++)expected[at+b]=value>>(24-b*8);};
                    auto equal=[&](){return std::memcmp(expected.data(),Memory::GetPointer(root,size),size)==0;};
                    for(uint32_t kind=0;kind<2;kind++){
                        expected.fill(0x55);std::memset(Memory::GetPointer(root-16,size+32),0x55,size+32);
                        ctx.gpr[3]=root;
                        if(kind==0){Memory::Write32(0x809bd790,0);func_8064ED80(&ctx);
                            if(allocations!=1 || Memory::Read32(0x809bd790)!=root || itemCreates!=1 || eventCreates!=1)return 4;
                            func_8064ED80(&ctx);if(allocations!=1 || itemCreates!=1 || eventCreates!=1)return 5;
                        }else{func_8064F96C(&ctx);if(ctx.gpr[3]!=root || itemCreates!=2 || eventCreates!=2)return 6;}
                        expected[0]=expected[1]=0;std::memset(expected.data()+4,0,14);expected[18]=0x0b;expected[19]=0xb8;
                        if(!equal())return 7;
                    }
                    for(uint32_t mode : {0u,6u,7u,8u,9u,10u,11u}){
                        expected.fill(0x55);std::memcpy(Memory::GetPointer(root,size),expected.data(),size);
                        Memory::Write32(racedata+raceSettings+8,mode);ctx.gpr[3]=root;func_8064EE50(&ctx);
                        if(mode>=7 && mode<=10){
                            expected[0]=1;std::memset(expected.data()+4,0,12);std::memset(expected.data()+20,0,376);
                            std::memset(expected.data()+400,0,8);std::memset(expected.data()+timers,0,capacity*4);
                        }
                        if(!equal())return 8;
                    }
                    if(clears!=4)return 9;
                    constexpr uint32_t peerCount=capacity<32?capacity:32;
                    // Player IDs cover the entire capacity; console IDs remain
                    // within the SDK's 32-bit connection mask.
                    for(uint32_t p=0;p<capacity;p++)Memory::Write8(controller+aidMap+p,p%peerCount);
                    for(uint32_t current=0;current<2;current++)for(uint32_t buffer=0;buffer<2;buffer++){
                        Memory::Write32(controller+currentSub,current);
                        Memory::Write32(controller+subs+current*subStride+16,0xffffffff);
                        for(uint32_t aid=0;aid<peerCount;aid++){
                            const uint32_t row=0x81200000+aid*256,holder=row+64,payload=row+128;
                            Memory::Write32(controller+receive+buffer*rowStride+aid*4,row);
                            Memory::Write32(controller+indices+aid*indexStride+4,buffer);
                            Memory::Write32(row+4,holder);Memory::Write32(holder,payload);Memory::Write32(holder+4,64);
                            Memory::Write32(holder+8,64);Memory::Write32(payload,1000+aid);
                        }
                        for(uint32_t p=0;p<capacity;p++)for(uint32_t age : {50u,700u})for(uint32_t gameType : {0u,6u}){
                            const uint32_t aid=p%peerCount,incoming=1000+aid,stored=incoming-age;
                            word(timers+aid*4,stored);Memory::Write32(root+timers+aid*4,stored);
                            Memory::Write32(racedata+raceSettings+12,gameType);
                            ctx.gpr[3]=root;ctx.gpr[4]=p;func_80650020(&ctx);
                            if(ctx.gpr[3]!=(age<600 || gameType==6 ? incoming : stored)){
                                std::fprintf(stderr,"timer current=%u buffer=%u player=%u aid=%u age=%u type=%u got=%u expected=%u\n",
                                    current,buffer,p,aid,age,gameType,ctx.gpr[3],age<600 || gameType==6 ? incoming : stored);return 10;}
                        }
                        for(uint32_t p : {capacity,capacity+1,0xffffffffu}){
                            ctx.gpr[3]=root;ctx.gpr[4]=p;func_80650020(&ctx);if(ctx.gpr[3]!=0)return 11;
                        }
                        Memory::Write32(controller+subs+current*subStride+16,0);
                        ctx.gpr[3]=root;ctx.gpr[4]=capacity-1;func_80650020(&ctx);if(ctx.gpr[3]!=0)return 12;
                        if(!equal())return 13;
                    }
                    for(uint32_t i=0;i<16;i++)if(Memory::Read8(root-16+i)!=0x55 || Memory::Read8(root+size+i)!=0x55)return 14;
                    for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r)return 15;
                    if(ctx.gpr[1]!=stack)return 16;
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Packet manager storage compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Packet manager storage execution timed out");
            Assert.True(run.ExitCode==0,$"Packet manager storage returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
