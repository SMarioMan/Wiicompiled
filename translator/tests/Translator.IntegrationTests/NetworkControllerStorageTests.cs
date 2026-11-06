using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class NetworkControllerStorageTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealControllerConstructorBuildsSeparatePacketGraphsForEveryPeer(int players)
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
        var temp=Path.Combine(Path.GetTempPath(),"mkw-network-storage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var entries=new uint[]{0x8065169c,0x80652b7c,0x806548b4,0x80005f84,0x80006038};
            var fixtures=new HashSet<uint>{0x801a7e0c,0x80226744,0x80229a48,0x80229a5c,0x80229a6c,0x80241a2c,0x8065a83c,0x80660694,0x800ccbe0};
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
                #include <vector>
                #include <set>
                #include <cstdio>
                #include <cstring>
                #define FAIL() do { std::fprintf(stderr,"fixture failure at line %d\n",__LINE__);std::abort(); } while(false)
                {{declarations}}
                constexpr uint32_t capacity={{players}},size={{layout.Symbol("RKNetController.size")}},
                    send={{layout.Symbol("RKNetController.splitSend.offset")}},receive={{layout.Symbol("RKNetController.splitReceived.offset")}},
                    rowStride={{layout.Symbol("RKNetPointerRow.size")}},full={{layout.Symbol("RKNetController.fullSend.offset")}},
                    times={{layout.Symbol("RKNetController.lastSendTimes.offset")}},lastAid={{layout.Symbol("RKNetController.lastSendAid.offset")}},
                    disconnected={{layout.Symbol("RKNetController.disconnectedAids.offset")}},
                    root=0x81010000,parentHeap=0x81040000,privateHeap=0x81050000,task=0x81060000,stack=0x81f00000;
                struct Allocation{uint32_t address,size;};std::vector<Allocation> allocations;
                uint32_t cursor=0x81100000,rootAllocations=0,heapCalls=0,threadCalls=0,mutexCalls=0,ends=0;
                void allocate(CpuContext* c){
                    if(c->gpr[3]==size && !rootAllocations){if(c->gpr[4]!=parentHeap || c->gpr[5]!=4)FAIL();rootAllocations++;c->gpr[3]=root;return;}
                    if(c->gpr[3]!=12 && c->gpr[3]!=32)FAIL();
                    auto n=c->gpr[3];cursor=(cursor+31)&~31u;allocations.push_back({cursor,n});
                    std::memset(Memory::GetPointer(cursor-16,n+32),0x77,n+32);c->gpr[3]=cursor;cursor+=n+64;}
                void allocate_buffer(CpuContext* c){
                    auto n=c->gpr[3];if(n>8192)FAIL();cursor=(cursor+31)&~31u;
                    allocations.push_back({cursor,n});std::memset(Memory::GetPointer(cursor-16,n+32),0x77,n+32);c->gpr[3]=cursor;cursor+=n+64;}
                void mutex_init(CpuContext* c){if(c->gpr[3]!=root+8 || mutexCalls++)FAIL();std::memset(Memory::GetPointer(root+8,24),0x33,24);}
                void make_heap(CpuContext* c){if(c->gpr[3]!=0x9b000 || c->gpr[4]!=parentHeap || heapCalls++)FAIL();c->gpr[3]=privateHeap;}
                void make_thread(CpuContext* c){if(threadCalls++)FAIL();c->gpr[3]=task;}
                void end_init(CpuContext*){ends++;}
                void sdk_init(CpuContext* c){if(c->gpr[3]!=1 || c->gpr[5]!=0x524d434a || c->gpr[6]!=0x80654078 || c->gpr[7]!=0x80654104)FAIL();c->gpr[3]=0;}
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="network controller storage fixture";info.rawCpuInvoker=entry;
                    info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    register_entry(0x801a7e0c,mutex_init);register_entry(0x80226744,make_heap);register_entry(0x80241a2c,make_thread);
                    register_entry(0x80229a48,allocate);register_entry(0x80229a5c,allocate);register_entry(0x80229a6c,allocate_buffer);
                    register_entry(0x8065a83c,end_init);register_entry(0x80660694,end_init);register_entry(0x800ccbe0,sdk_init);
                    std::memset(Memory::GetPointer(root-16,size+32),0x55,size+32);Memory::Write32(0x809bd918,0);
                    CpuContext ctx{};ctx.gpr[1]=stack;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                    for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                    ctx.gpr[3]=parentHeap;func_8065169C(&ctx);
                    if(rootAllocations!=1 || heapCalls!=1 || threadCalls!=1 || mutexCalls!=1 || ends!=2 || Memory::Read32(0x809bd918)!=root)return 4;
                    std::array<uint8_t,size> expected{};expected.fill(0x55);
                    auto word=[&](uint32_t at,uint32_t value){for(uint32_t b=0;b<4;b++)expected[at+b]=value>>(24-b*8);};
                    word(0,0x808bc224);word(4,0x808bc230);std::memset(expected.data()+8,0x33,24);word(32,privateHeap);word(36,task);word(40,0);
                    word(disconnected,0);word({{layout.Symbol("RKNetController.disconnectedPlayers.offset")}},0);
                    std::set<uint32_t> used;
                    auto pointer=[&](uint32_t at){auto value=Memory::Read32(root+at);if(!used.insert(value).second || value<0x81100000 || value>=cursor)FAIL();word(at,value);return value;};
                    for(uint32_t p=0;p<capacity;p++){
                        auto h=pointer(full+p*4);if(Memory::Read32(h+4)!=736 || Memory::Read32(h+8)!=0)return 5;
                        auto buffer=Memory::Read32(h);if(!used.insert(buffer).second)return 6;
                        for(uint32_t i=0;i<736;i++)if(Memory::Read8(buffer+i)!=0)return 7;
                        for(uint32_t b=0;b<2;b++)for(auto start:{send,receive}){
                            auto row=pointer(start+b*rowStride+p*4);
                            for(uint32_t section=0;section<8;section++){
                                auto holder=Memory::Read32(row+section*4);if(!used.insert(holder).second)return 8;
                                auto buffer=Memory::Read32(holder),n=Memory::Read32(holder+4);
                                if(!used.insert(buffer).second || n>8192 || Memory::Read32(holder+8)!=0)return 9;
                                for(uint32_t i=0;i<n;i++)if(Memory::Read8(buffer+i)!=0)return 10;
                            }
                        }
                    }
                    std::memset(expected.data()+times,0,capacity*32);expected[lastAid]=255;
                    if(std::memcmp(expected.data(),Memory::GetPointer(root,size),size)){
                        for(uint32_t at=0;at<size;at++)if(expected[at]!=Memory::Read8(root+at))std::fprintf(stderr,"offset %u actual %u expected %u\n",at,Memory::Read8(root+at),expected[at]);return 11;}
                    ctx.gpr[3]=root;func_806548B4(&ctx);
                    for(uint32_t b=0;b<2;b++){
                        auto sub={{layout.Symbol("RKNetController.subs.offset")}}+b*{{layout.Symbol("RKNetSub.size")}};
                        for(uint32_t w=0;w<7;w++)word(sub+w*4,0);word(sub+28,0xffffffff);
                        expected[sub+33]=expected[sub+34]=255;
                        std::memset(expected.data()+sub+{{layout.Symbol("RKNetSub.users.offset")}},0,capacity*4);
                        expected[sub+{{layout.Symbol("RKNetSub.tail.offset")}}]=0;
                    }
                    if(std::memcmp(expected.data(),Memory::GetPointer(root,size),size))return 16;
                    for(const auto& allocation:allocations)for(uint32_t i=0;i<16;i++)
                        if(Memory::Read8(allocation.address-16+i)!=0x77 || Memory::Read8(allocation.address+allocation.size+i)!=0x77)return 12;
                    for(uint32_t i=0;i<16;i++)if(Memory::Read8(root-16+i)!=0x55 || Memory::Read8(root+size+i)!=0x55)return 13;
                    for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r)return 14;
                    if(ctx.gpr[1]!=stack)return 15;
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Network controller storage compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Network controller storage execution timed out");
            Assert.True(run.ExitCode==0,$"Network controller storage returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
