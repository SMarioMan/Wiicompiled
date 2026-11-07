using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class SelectHandlerStorageTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealSelectHandlerInitializesUniformPeerCachesAndTimers(int players)
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
        var temp=Path.Combine(Path.GetTempPath(),"mkw-select-handler-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var entries=new uint[]{0x80657f50,0x80658830,0x80658068,0x80658ac4,0x80658978,
                0x80658598,0x806585e8,0x80658638,0x80658688,0x806586d0,0x80006038,0x80005f84};
            var fixtures=new HashSet<uint>{0x80229a48,0x80652ab8,0x806558d0,0x80660c9c,0x80660ce0};
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
                constexpr uint32_t capacity={{players}},size={{layout.Symbol("RKNetSelectHandler.size")}},
                    send={{layout.Symbol("RKNetSelectHandler.send.offset")}},received={{layout.Symbol("RKNetSelectHandler.received.offset")}},
                    sendState={{layout.Symbol("RKNetSelectHandler.sendState.offset")}},sentTime={{layout.Symbol("RKNetSelectHandler.lastSentTime.offset")}},
                    recvTimes={{layout.Symbol("RKNetSelectHandler.lastReceivedTimes.offset")}},delays={{layout.Symbol("RKNetSelectHandler.delays.offset")}},
                    unknown={{layout.Symbol("RKNetSelectHandler.unknown.offset")}},flags={{layout.Symbol("RKNetSelectHandler.flags.offset")}},
                    controllerSize={{layout.Symbol("RKNetController.size")}},currentSub={{layout.Symbol("RKNetController.currentSub.offset")}},
                    subs={{layout.Symbol("RKNetController.subs.offset")}},subStride={{layout.Symbol("RKNetSub.size")}},
                    selectTimes={{layout.Symbol("RKNetController.selectTimes.offset")}},
                    userSize={{layout.Symbol("RKNetUserHandler.size")}},userMiis={{layout.Symbol("RKNetUserHandler.miisReceived.offset")}},
                    root=0x81010000,controller=0x81040000,user=0x81090000,stack=0x81f00000;
                uint32_t allocations=0,clears=0,userCalls=0;
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo entryInfo{};entryInfo.address=address;entryInfo.name="SELECT handler fixture";entryInfo.rawCpuInvoker=entry;
                    entryInfo.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(entryInfo);}
                void allocate(CpuContext* c){if(c->gpr[3]!=size || allocations++)std::abort();c->gpr[3]=root;}
                void clear_packets(CpuContext* c){if(c->gpr[3]!=controller)std::abort();clears++;}
                void unexpected_local(CpuContext*){std::abort();}
                void user_call(CpuContext* c){if(c->gpr[3]!=user)std::abort();userCalls++;}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    register_entry(0x80229a48,allocate);register_entry(0x80652ab8,clear_packets);
                    register_entry(0x806558d0,unexpected_local);register_entry(0x80660c9c,user_call);register_entry(0x80660ce0,user_call);
                    Memory::Write32(0x809bd918,controller);Memory::Write32(0x809bd958,user);
                    std::memset(Memory::GetPointer(user-16,userSize+32),0x55,userSize+32);
                    CpuContext ctx{};ctx.gpr[1]=stack;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                    for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                    std::array<uint8_t,size> expected{};std::array<uint8_t,controllerSize> expectedController{};
                    auto word=[&](uint32_t at,uint32_t value){for(uint32_t b=0;b<4;b++)expected[at+b]=value>>(24-b*8);};
                    auto equal=[&](){return std::memcmp(expected.data(),Memory::GetPointer(root,size),size)==0;};
                    auto packet=[&](uint32_t at){
                        std::memset(expected.data()+at,0,56);
                        for(uint32_t local=0;local<2;local++){expected[at+16+local*8+4]=48;expected[at+16+local*8+5]=36;expected[at+16+local*8+6]=67;}
                        std::memset(expected.data()+at+40,255,12);expected[at+52]=expected[at+54]=255;
                    };
                    for(uint32_t kind=0;kind<2;kind++){
                        expected.fill(0x55);std::memset(Memory::GetPointer(root-16,size+32),0x55,size+32);
                        expectedController.fill(0x55);std::memset(expectedController.data()+selectTimes,0,capacity*8);
                        std::memset(Memory::GetPointer(controller,controllerSize),0x55,controllerSize);
                        if(kind==0){Memory::Write32(0x809bd930,0);ctx.gpr[3]=3;func_80657F50(&ctx);
                            if(allocations!=1 || Memory::Read32(0x809bd930)!=root)return 4;
                            ctx.gpr[3]=3;func_80657F50(&ctx);if(allocations!=1)return 5;
                        }else{ctx.gpr[3]=root;ctx.gpr[4]=3;func_80658830(&ctx);}
                        word(0,3);std::memset(expected.data()+flags,0,20);
                        if(!equal() || std::memcmp(expectedController.data(),Memory::GetPointer(controller,controllerSize),controllerSize))return 6;
                        Memory::Write32(controller+currentSub,1);Memory::Write8(controller+subs+subStride+32,0);
                        ctx.gpr[3]=root;func_80658068(&ctx);
                        packet(send);for(uint32_t p=0;p<capacity;p++)packet(received+p*56);
                        expected[sendState]=255;std::memset(expected.data()+sentTime,0,8);
                        std::memset(expected.data()+recvTimes,0,capacity*8);std::memset(expected.data()+delays,0,capacity*8);
                        std::memset(expected.data()+unknown,0,capacity*4);
                        if(!equal()){
                            for(uint32_t b=0;b<size;b++)if(expected[b]!=Memory::Read8(root+b))
                                std::fprintf(stderr,"init kind=%u byte=%u expected=%02x got=%02x\n",kind,b,expected[b],Memory::Read8(root+b));return 7;}
                        if(clears!=kind+1 || userCalls!=2*(kind+1) || Memory::Read32(user+userMiis))return 8;
                        std::memset(Memory::GetPointer(root+received,capacity*56),0x66,capacity*56);
                        ctx.gpr[3]=root;func_80658AC4(&ctx);if(!equal())return 9;
                        std::memset(Memory::GetPointer(root+send,56),0x66,56);
                        ctx.gpr[3]=root;func_80658978(&ctx);if(!equal())return 10;
                    }
                    for(uint32_t aid=0;aid<capacity;aid++)for(uint32_t local=0;local<2;local++){
                        Memory::Write8(controller+subs+subStride+33,1);
                        const uint32_t at=root+(aid==1?send:received+aid*56)+16+local*8;
                        Memory::Write8(at+4,(aid+local)%48);Memory::Write8(at+5,(aid+local)%36);
                        Memory::Write8(at+6,(aid+local)%32);Memory::Write8(at+7,(aid+local)%5);
                        ctx.gpr[3]=root;ctx.gpr[4]=aid;ctx.gpr[5]=local;func_80658598(&ctx);if(ctx.gpr[3]!=(aid+local)%48)return 11;
                        ctx.gpr[3]=root;ctx.gpr[4]=aid;ctx.gpr[5]=local;func_806585E8(&ctx);if(ctx.gpr[3]!=(aid+local)%36)return 12;
                        ctx.gpr[3]=root;ctx.gpr[4]=aid;ctx.gpr[5]=local;func_80658638(&ctx);if(ctx.gpr[3]!=(aid+local)%32)return 13;
                        ctx.gpr[3]=root;ctx.gpr[4]=aid;ctx.gpr[5]=local;func_80658688(&ctx);if(ctx.gpr[3]!=at)return 14;
                        ctx.gpr[3]=root;ctx.gpr[4]=aid;ctx.gpr[5]=local;func_806586D0(&ctx);if(ctx.gpr[3]!=(aid+local)%5)return 15;
                    }
                    for(uint32_t b=0;b<16;b++)if(Memory::Read8(root-16+b)!=0x55 || Memory::Read8(root+size+b)!=0x55 ||
                        Memory::Read8(user-16+b)!=0x55 || Memory::Read8(user+userSize+b)!=0x55)return 16;
                    for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r)return 17;
                    if(ctx.gpr[1]!=stack)return 18;
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"SELECT handler storage compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"SELECT handler storage execution timed out");
            Assert.True(run.ExitCode==0,$"SELECT handler storage returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
