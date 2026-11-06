using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class MiiManagerStorageTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealMiiConstructorAndParticipantDatabasesKeepAllCapacitySlotsSeparate(int players)
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
        var temp=Path.Combine(Path.GetTempPath(),"mkw-mii-storage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            // Exercise the real Init prologue, participant allocation loop and
            // epilogue. Resource loading and SDK startup are outside this fixture.
            foreach(var (from,to) in new (uint,uint)[]{(0x80522944,0x80522b60),(0x80522bbc,0x80522cd0)})
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(from,4),4),0x48000000|((to-from)&0x03fffffc));
            var entries=new uint[]{0x80522788,0x8052280c,0x80522920,0x800c87b0,0x80522e24,0x805250bc,0x8052511c};
            var fixtures=new HashSet<uint>{0x80229a48,0x8021a050,0x80020494,0x8054f444,0x800c87c0,0x800c71f0,0x800cb740,0x800c85e0,0x800bc3e0};
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
                constexpr uint32_t capacity={{players}},size={{layout.Symbol("MiiManager.size")}},
                    buffers={{layout.Symbol("MiiManager.playerBuffers.offset")}},dbs={{layout.Symbol("MiiManager.playerDatabases.offset")}},
                    tail={{layout.Symbol("MiiManager.tail.offset")}},flags=tail+{{layout.Symbol("MiiManagerTail.flags.offset")}},
                    status=tail+{{layout.Symbol("MiiManagerTail.status.offset")}},extra=tail+{{layout.Symbol("MiiManagerTail.extraState.offset")}},
                    indices=tail+{{layout.Symbol("MiiManagerTail.officialIndices.offset")}},officialCount=tail+{{layout.Symbol("MiiManagerTail.officialCount.offset")}},
                    randomOffset=tail+{{layout.Symbol("MiiManagerTail.random.offset")}},
                    root=0x81010000,heap=0x81020000,stack=0x81f00000;
                uint32_t allocations=0,arrays=0,randoms=0,bufferCount=0,dbCount=0;
                void allocate(CpuContext* c){if(c->gpr[3]!=size || allocations++)std::abort();c->gpr[3]=root;}
                void disposer(CpuContext* c){if(c->gpr[3]!=root)std::abort();std::memset(Memory::GetPointer(root,16),0,16);}
                void array_ctor(CpuContext* c){
                    if(c->gpr[3]!=root+tail || c->gpr[4]!=0x805228c8 || c->gpr[5]!=0x805228e0 || c->gpr[6]!=36 || c->gpr[7]!=2 || arrays++)std::abort();
                    std::memset(Memory::GetPointer(root+tail,72),0x33,72);}
                void random_ctor(CpuContext* c){if(c->gpr[3]!=root+randomOffset || randoms++)std::abort();std::memset(Memory::GetPointer(root+randomOffset,24),0x44,24);}
                void buffer_alloc(CpuContext* c){
                    if(c->gpr[3]!=heap || c->gpr[4]!=128 || c->gpr[5]!=4 || bufferCount>=capacity)std::abort();
                    c->gpr[3]=0x81100000+128*bufferCount++;}
                void init_db(CpuContext* c){
                    if(c->gpr[3]!=root+dbs+24*dbCount || c->gpr[4]!=5 || c->gpr[5]!=0x81100000+128*dbCount || c->gpr[6]!=2 || dbCount>=capacity)std::abort();
                    for(uint32_t w=0;w<6;w++)Memory::Write32(c->gpr[3]+w*4,0x12340000+dbCount*16+w);dbCount++;}
                uint32_t selection=0,sdkStatus=0;
                bool valid(uint32_t id){return selection==0 || (selection==1 && id%3==0);}
                bool favourite(uint32_t id){return id%2==0;}
                void available(CpuContext* c){c->gpr[3]=valid(c->gpr[3])?1:0;}
                void store_info(CpuContext* c){
                    auto id=c->gpr[6];if(!valid(id) || c->gpr[4]!=0 || c->gpr[5]!=0)std::abort();
                    std::memset(Memory::GetPointer(c->gpr[3],60),0,60);Memory::Write32(c->gpr[3]+52,favourite(id)?0x20000:0);c->gpr[3]=0;}
                void controller_type(CpuContext* c){if(c->gpr[3]>3)std::abort();c->gpr[3]=6;}
                void sdk_status(CpuContext* c){c->gpr[3]=sdkStatus;}
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="Mii storage fixture";info.rawCpuInvoker=entry;
                    info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    register_entry(0x80229a48,allocate);register_entry(0x8021a050,disposer);register_entry(0x80020494,array_ctor);
                    register_entry(0x8054f444,random_ctor);register_entry(0x800c87c0,init_db);register_entry(0x80010000,buffer_alloc);
                    register_entry(0x800c71f0,available);register_entry(0x800cb740,store_info);
                    register_entry(0x800c85e0,controller_type);register_entry(0x800bc3e0,sdk_status);
                    Memory::Write32(heap,heap+256);Memory::Write32(heap+276,0x80010000);
                    std::memset(Memory::GetPointer(root-16,size+32),0x55,size+32);Memory::Write32(0x809b8f50,0);
                    CpuContext ctx{};ctx.gpr[1]=stack;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                    for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                    func_80522788(&ctx);
                    if(allocations!=1 || arrays!=1 || randoms!=1 || ctx.gpr[3]!=root || Memory::Read32(0x809b8f50)!=root)return 4;
                    std::array<uint8_t,size> expected{};expected.fill(0x55);
                    auto word=[&](uint32_t at,uint32_t value){for(uint32_t b=0;b<4;b++)expected[at+b]=value>>(24-b*8);};
                    std::memset(expected.data(),0,16);word(0,0x808aeabc);word(16,0x808aeaa4);
                    word(20,0);word(24,0);word(28,0);std::memset(expected.data()+tail,0x33,72);
                    expected[flags+3]=expected[flags+4]=expected[flags+5]=0;word(status,2);
                    std::memset(expected.data()+extra,0,20);std::memset(expected.data()+randomOffset,0x44,24);
                    if(std::memcmp(expected.data(),Memory::GetPointer(root,size),size))return 5;
                    func_80522788(&ctx);if(allocations!=1 || arrays!=1 || randoms!=1)return 6;
                    ctx.gpr[3]=root;ctx.gpr[4]=heap;func_80522920(&ctx);
                    if(bufferCount!=capacity || dbCount!=capacity)return 7;
                    for(uint32_t p=0;p<capacity;p++){
                        word(buffers+p*4,0x81100000+p*128);
                        for(uint32_t w=0;w<6;w++)word(dbs+p*24+w*4,0x12340000+p*16+w);
                    }
                    if(std::memcmp(expected.data(),Memory::GetPointer(root,size),size))return 8;
                    for(selection=0;selection<3;selection++){
                        ctx.gpr[3]=root;func_80522E24(&ctx);
                        std::memset(expected.data()+indices,0xff,100);expected[officialCount]=0;
                        uint32_t count=0;
                        for(uint32_t group=0;group<2;group++)for(uint32_t id=0;id<100;id++)
                            if(valid(id) && favourite(id)==(group==0))expected[indices+count++]=id;
                        expected[officialCount]=count;
                        if(std::memcmp(expected.data(),Memory::GetPointer(root,size),size)){for(uint32_t at=0;at<size;at++)if(expected[at]!=Memory::Read8(root+at))std::fprintf(stderr,"selection %u offset %u actual %u expected %u\n",selection,at,Memory::Read8(root+at),expected[at]);return 12;}
                    }
                    for(uint32_t channel=0;channel<4;channel++)for(sdkStatus=0;sdkStatus<8;sdkStatus++){
                        for(uint32_t p=0;p<4;p++)word(extra+p*4,0);
                        std::memcpy(Memory::GetPointer(root,size),expected.data(),size);
                        ctx.gpr[3]=root;ctx.gpr[4]=channel;func_805250BC(&ctx);
                        word(extra+channel*4,1);expected[extra+16+channel]=0;
                        if(std::memcmp(expected.data(),Memory::GetPointer(root,size),size))return 13;
                        ctx.gpr[3]=root;func_8052511C(&ctx);
                        auto result=sdkStatus==0 || sdkStatus==2?1:sdkStatus==6?0:2;
                        if(ctx.gpr[3]!=result)return 14;
                        if(result!=0)word(extra+channel*4,result==1?2:0);
                        if(std::memcmp(expected.data(),Memory::GetPointer(root,size),size))return 15;
                    }
                    for(uint32_t i=0;i<16;i++)if(Memory::Read8(root-16+i)!=0x55 || Memory::Read8(root+size+i)!=0x55)return 9;
                    for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r)return 10;
                    if(ctx.gpr[1]!=stack)return 11;
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Mii storage compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Mii storage execution timed out");
            Assert.True(run.ExitCode==0,$"Mii storage returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
