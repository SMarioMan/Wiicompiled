using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class UserHandlerStorageTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealUserHandlerAllocatesAndClearsEveryPeerPacket(int players)
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
        var temp=Path.Combine(Path.GetTempPath(),"mkw-user-handler-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var entries=new uint[]{0x80660694,0x80660d24,0x80660768,0x80660860,0x800cc0f0,0x80006038,0x80005f84,0x80660b08,0x80660b74,0x80660b84,0x80660ba4,0x80660c3c,0x80660ed4};
            var fixtures=new HashSet<uint>{0x80229a48,0x80229a6c,0x80660f28,0x800cc110,0x800cc3e0,0x800cc3f0,0x800bc3e0};
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
                constexpr uint32_t capacity={{players}},size={{layout.Symbol("RKNetUserHandler.size")}},
                    send={{layout.Symbol("RKNetUserHandler.send.offset")}},received={{layout.Symbol("RKNetUserHandler.received.offset")}},
                    info={{layout.Symbol("RKNetUserHandler.rflInfo.offset")}},bufferPtr={{layout.Symbol("RKNetUserHandler.rflBuffer.offset")}},
                    unknown={{layout.Symbol("RKNetUserHandler.unknown.offset")}},aidFlags={{layout.Symbol("RKNetUserHandler.aidBitflag.offset")}},
                    miis={{layout.Symbol("RKNetUserHandler.miisReceived.offset")}},aidFlags2={{layout.Symbol("RKNetUserHandler.aidBitflag2.offset")}},
                    controllerSize={{layout.Symbol("RKNetController.size")}},currentSub={{layout.Symbol("RKNetController.currentSub.offset")}},
                    subs={{layout.Symbol("RKNetController.subs.offset")}},subStride={{layout.Symbol("RKNetSub.size")}},
                    bufferSize=160*(capacity-1),root=0x81010000,buffer=0x81050000,controller=0x81080000,stack=0x81f00000;
                uint32_t allocations=0,bufferAllocations=0,prepares=0,rflInitializations=0,toggles=0,asyncResult=0;
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo entryInfo{};entryInfo.address=address;entryInfo.name="USER handler fixture";entryInfo.rawCpuInvoker=entry;
                    entryInfo.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(entryInfo);}
                void allocate(CpuContext* c){if(c->gpr[3]!=size || allocations++)std::abort();c->gpr[3]=root;}
                void allocate_buffer(CpuContext* c){if(c->gpr[3]!=bufferSize)std::abort();bufferAllocations++;c->gpr[3]=buffer;}
                void prepare_send(CpuContext* c){if(c->gpr[3]!=root)std::abort();prepares++;std::memset(Memory::GetPointer(root+send,192),0x39,192);}
                void initialize_rfl(CpuContext* c){
                    if(c->gpr[3]!=root+info || c->gpr[4]!=buffer || c->gpr[5]!=1 || c->gpr[6]!=capacity || c->gpr[7]!=2)std::abort();
                    rflInitializations++;
                    // RFL transport itself retains its SDK limit. This fixture verifies
                    // the relocated call arguments and leaves SDK behavior separate.
                }
                void toggle(CpuContext* c){if(c->gpr[3]!=root+info)std::abort();toggles++;}
                void async_status(CpuContext* c){c->gpr[3]=asyncResult;}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    register_entry(0x80229a48,allocate);register_entry(0x80229a6c,allocate_buffer);
                    register_entry(0x80660f28,prepare_send);register_entry(0x800cc110,initialize_rfl);
                    register_entry(0x800cc3e0,toggle);register_entry(0x800cc3f0,toggle);register_entry(0x800bc3e0,async_status);
                    Memory::Write32(0x809b8f88,0x81200000);Memory::Write32(0x81224ffc,0x81300000);
                    Memory::Write32(0x809bd918,controller);std::memset(Memory::GetPointer(controller,controllerSize),0,controllerSize);
                    Memory::Write32(controller+currentSub,1);Memory::Write8(controller+subs+subStride+33,1);
                    CpuContext ctx{};ctx.gpr[1]=stack;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                    for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                    std::array<uint8_t,size> expected{};
                    auto word=[&](uint32_t at,uint32_t value){for(uint32_t b=0;b<4;b++)expected[at+b]=value>>(24-b*8);};
                    auto equal=[&](){return std::memcmp(expected.data(),Memory::GetPointer(root,size),size)==0;};
                    for(uint32_t kind=0;kind<2;kind++){
                        expected.fill(0x55);std::memset(Memory::GetPointer(root-16,size+32),0x55,size+32);
                        std::memset(Memory::GetPointer(buffer-16,bufferSize+32),0x55,bufferSize+32);
                        ctx.gpr[3]=root;
                        if(kind==0){Memory::Write32(0x809bd958,0);func_80660694(&ctx);
                            if(allocations!=1 || Memory::Read32(0x809bd958)!=root || bufferAllocations!=1)return 4;
                            func_80660694(&ctx);if(allocations!=1 || bufferAllocations!=1)return 5;
                        }else{func_80660D24(&ctx);if(ctx.gpr[3]!=root || bufferAllocations!=2)return 6;}
                        expected[0]=0;word(aidFlags,0);word(miis,0);word(aidFlags2,0);word(bufferPtr,buffer);
                        if(!equal()){
                            for(uint32_t b=0;b<size;b++)if(expected[b]!=Memory::Read8(root+b))
                                std::fprintf(stderr,"ctor kind=%u byte=%u expected=%02x got=%02x\n",kind,b,expected[b],Memory::Read8(root+b));return 7;}
                        ctx.gpr[3]=root;func_80660768(&ctx);
                        expected[0]=1;std::memset(expected.data()+send,0x39,192);std::memset(expected.data()+received,0,capacity*192);
                        std::memset(expected.data()+info,0,16);word(unknown,0);
                        if(!equal() || prepares!=kind+1 || rflInitializations!=kind+1)return 8;
                        for(uint32_t b=0;b<bufferSize;b++)if(Memory::Read8(buffer+b)!=0)return 9;
                        ctx.gpr[3]=root;func_80660768(&ctx);if(prepares!=kind+1 || rflInitializations!=kind+1 || !equal())return 10;
                        for(uint32_t p=0;p<capacity;p++)std::memset(Memory::GetPointer(root+received+p*192,192),p+1,192);
                        ctx.gpr[3]=root;func_80660860(&ctx);expected[0]=0;std::memset(expected.data()+send,0,192);
                        if(!equal())return 11;
                        for(uint32_t b=0;b<16;b++)if(Memory::Read8(root-16+b)!=0x55 || Memory::Read8(root+size+b)!=0x55 ||
                            Memory::Read8(buffer-16+b)!=0x55 || Memory::Read8(buffer+bufferSize+b)!=0x55)return 12;
                    }
                    constexpr uint32_t lastPeer=capacity<32?capacity-1:31,remoteMask=1u|(1u<<lastPeer);
                    for(uint32_t prepared : {0u,1u})for(uint32_t present : {0u,remoteMask})
                        for(uint32_t packets : {0u,1u,remoteMask})for(uint32_t avatars : {0u,1u,remoteMask}){
                            Memory::Write8(root,prepared);Memory::Write32(controller+subs+subStride+16,present|2);
                            Memory::Write32(root+aidFlags,packets);Memory::Write32(root+miis,avatars);
                            ctx.gpr[3]=root;func_80660BA4(&ctx);
                            if(ctx.gpr[3]!=(prepared && present && (packets&present)==present && (avatars&present)==present))return 15;
                            ctx.gpr[3]=root;func_80660C3C(&ctx);
                            if(ctx.gpr[3]!=(prepared && present && (avatars&present)==present))return 16;
                            for(uint32_t peer : {0u,1u,lastPeer}){
                                ctx.gpr[3]=root;ctx.gpr[4]=peer;func_80660B84(&ctx);
                                if(ctx.gpr[3]!=bool(packets&(1u<<peer)))return 17;
                            }
                        }
                    for(uint32_t privacy : {0u,32u}){
                        Memory::Write32(0x8132330c,privacy);Memory::Write32(root+unknown,0);
                        Memory::Write32(root+miis,remoteMask);Memory::Write32(root+aidFlags,0x12345678);
                        ctx.gpr[3]=root;func_80660B74(&ctx);if(ctx.gpr[3]!=1)return 18;
                        ctx.gpr[3]=root;func_80660B08(&ctx);
                        if(Memory::Read32(root+unknown)!=1 || Memory::Read32(root+aidFlags2)!=remoteMask || toggles!=(privacy?2:1))return 19;
                        ctx.gpr[3]=root;func_80660B74(&ctx);if(ctx.gpr[3])return 20;
                        asyncResult=6;ctx.gpr[3]=root;func_80660ED4(&ctx);
                        if(Memory::Read32(root+unknown)!=1 || Memory::Read32(root+aidFlags)!=0x12345678 || Memory::Read32(root+aidFlags2)!=remoteMask)return 21;
                        asyncResult=0;ctx.gpr[3]=root;func_80660ED4(&ctx);
                        if(Memory::Read32(root+unknown) || Memory::Read32(root+aidFlags)!=remoteMask || Memory::Read32(root+aidFlags2))return 22;
                        ctx.gpr[3]=root;func_80660B74(&ctx);if(ctx.gpr[3]!=1)return 23;
                    }
                    for(uint32_t b=0;b<16;b++)if(Memory::Read8(root-16+b)!=0x55 || Memory::Read8(root+size+b)!=0x55)return 24;
                    for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r)return 13;
                    if(ctx.gpr[1]!=stack)return 14;
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"USER handler storage compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"USER handler storage execution timed out");
            Assert.True(run.ExitCode==0,$"USER handler storage returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
