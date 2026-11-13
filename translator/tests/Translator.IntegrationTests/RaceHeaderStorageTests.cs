using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class RaceHeaderStorageTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealRaceHeaderCacheResetsEveryPeerAndReturnsBothLocalPlayerRecords(int players)
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
        var temp=Path.Combine(Path.GetTempPath(),"mkw-race-header-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var entries=new uint[]{0x8065b14c,0x8065b330,0x8065b7b4,0x8065ba68,0x8065ba80,0x8065bbf4,0x8065be78,0x8065c714,0x8065ba98,0x8065bd70,0x8065c2d4,0x8065c5a8,0x8065c6a0,0x8065c444};
            var fixtures=new HashSet<uint>{0x80229a48,0x80652ab8,0x801aacbc,0x80020aa4,0x806609d8,0x8065ca70,0x8065cb28};
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
                constexpr uint32_t capacity={{players}},size={{layout.Symbol("RKNetRH1Handler.size")}},
                    records={{layout.Symbol("RKNetRH1Handler.records.offset")}},recordCount={{layout.Symbol("RKNetRH1Handler.records.count")}},
                    stride={{layout.Symbol("RKNetRH1Data.size")}},ids={{layout.Symbol("RKNetRH1Data.playerAids.offset")}},
                    tail={{layout.Symbol("RKNetRH1Data.tail.offset")}},
                    controllerSize={{layout.Symbol("RKNetController.size")}},biggestTimer={{layout.Symbol("RKNetController.biggestTimer.offset")}},
                    root=0x81010000,controller=0x81050000,stack=0x81f00000;
                uint32_t allocations=0,clears=0;
                void allocate(CpuContext* c){if(c->gpr[3]!=size || allocations++)std::abort();c->gpr[3]=root;}
                void clear_split(CpuContext* c){if(c->gpr[3]!=controller)std::abort();clears++;}
                uint32_t sent=0,last=255,tick=1000;
                void clock(CpuContext* c){c->gpr[3]=0;c->gpr[4]=tick++;}
                void elapsed(CpuContext* c){c->gpr[3]=0;c->gpr[4]=170;}
                void user_data(CpuContext*){}
                void send(CpuContext* c){if(c->gpr[3]!=root || c->gpr[4]>=capacity)std::abort();last=c->gpr[4];++sent;}
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="race header storage fixture";info.rawCpuInvoker=entry;
                    info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    register_entry(0x80229a48,allocate);register_entry(0x80652ab8,clear_split);Memory::Write32(0x809bd918,controller);
                    register_entry(0x801aacbc,clock);register_entry(0x80020aa4,elapsed);register_entry(0x806609d8,user_data);
                    register_entry(0x8065ca70,send);register_entry(0x8065cb28,send);
                    CpuContext ctx{};ctx.gpr[1]=stack;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                    for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                    std::array<uint8_t,size> expected{};
                    auto word=[&](uint32_t at,uint32_t value){for(uint32_t b=0;b<4;b++)expected[at+b]=value>>(24-b*8);};
                    auto reset_record=[&](uint32_t p){auto base=records+p*stride;word(base,0);
                        for(uint32_t at=4;at<=20;at+=4)word(base+at,0xffffffff);word(base+24,0);word(base+28,0);
                        std::memset(expected.data()+base+ids,255,capacity);std::memset(expected.data()+base+tail,0,3);};
                    auto equal=[&](){return std::memcmp(expected.data(),Memory::GetPointer(root,size),size)==0;};
                    for(uint32_t kind=0;kind<3;kind++){
                        std::memset(Memory::GetPointer(root-16,size+32),0x55,size+32);expected.fill(0x55);
                        std::memset(Memory::GetPointer(controller,controllerSize),0x22,controllerSize);clears=0;
                        if(kind==0){Memory::Write32(0x809bd940,0);func_8065B14C(&ctx);
                            if(allocations!=1 || Memory::Read32(0x809bd940)!=root)return 4;expected[1]=0;
                            func_8065B14C(&ctx);if(allocations!=1)return 5;
                        }else{ctx.gpr[3]=root;if(kind==1)func_8065B330(&ctx);else{func_8065C2D4(&ctx);expected[1]=0;} }
                        expected[0]=0;word(4,0);word(8,0);word(12,0);expected[16]=255;word(24,0);word(28,0);
                        for(uint32_t p=0;p<recordCount;p++)reset_record(p);
                        if(!equal()){
                            for(uint32_t at=0;at<size;at++)if(expected[at]!=Memory::Read8(root+at))std::fprintf(stderr,"kind %u offset %u actual %u expected %u\n",kind,at,Memory::Read8(root+at),expected[at]);return 6;}
                        if(clears!=1)return 7;
                        for(uint32_t at=0;at<controllerSize;at++)if(Memory::Read8(controller+at)!=(kind!=1 && at>=biggestTimer && at<biggestTimer+4?0:0x22))return 8;
                    }
                    for(uint32_t p=0;p<recordCount;p++){
                        expected.fill(0x55);std::memset(Memory::GetPointer(root,size),0x55,size);reset_record(p);
                        ctx.gpr[3]=root;ctx.gpr[4]=p;func_8065C6A0(&ctx);if(!equal())return 24;
                    }
                    expected.fill(0x55);std::memset(Memory::GetPointer(root,size),0x55,size);
                    for(uint32_t p=0;p<recordCount;p++)reset_record(p);
                    ctx.gpr[3]=root;func_8065C5A8(&ctx);if(!equal())return 25;
                    for(uint32_t p=0;p<capacity;p++){
                        auto base=records+p*stride;
                        for(uint32_t local=0;local<2;local++){
                            word(base+4+local*4,0x1000+p*2+local);word(base+12+local*4,0x2000+p*2+local);
                            expected[base+tail+1+local]=(p*2+local)%256;
                        }
                    }
                    std::memcpy(Memory::GetPointer(root,size),expected.data(),size);
                    for(uint32_t p=0;p<capacity;p++)for(uint32_t local=0;local<2;local++){
                        ctx.gpr[3]=root;ctx.gpr[4]=p;ctx.gpr[5]=local;func_8065BA68(&ctx);if(ctx.gpr[3]!=0x1000+p*2+local)return 9;
                        ctx.gpr[3]=root;ctx.gpr[4]=p;ctx.gpr[5]=local;func_8065BA80(&ctx);if(ctx.gpr[3]!=0x2000+p*2+local)return 10;
                        ctx.gpr[3]=root;ctx.gpr[4]=p;ctx.gpr[5]=local;func_8065BBF4(&ctx);if(ctx.gpr[3]!=(p*2+local)%256)return 11;
                        if(!equal())return 12;
                    }
                    for(uint32_t p=0;p<(capacity<24?capacity:24);p++){
                        word(4,0xffffffff);word(8,0xffffffff);word(12,0xffffffff);
                        std::memcpy(Memory::GetPointer(root,size),expected.data(),size);
                        ctx.gpr[3]=root;ctx.gpr[4]=p;func_8065B7B4(&ctx);
                        auto flag=p<32?1u<<p:0;word(4,~flag);word(8,~flag);word(12,~flag);reset_record(p);
                        if(!equal())return 13;
                    }
                    // Find a valid header even on a high console, then count the
                    // complete serialized participant map, including partial groups.
                    for(uint32_t selected : {0u,capacity-1})for(uint32_t active : {1u,capacity-1,capacity}){
                        std::memset(Memory::GetPointer(root,size),0x55,size);
                        for(uint32_t r=0;r<recordCount;r++)Memory::Write32(root+records+r*stride+20,0xffffffff);
                        Memory::Write32(root+records+selected*stride+20,0);
                        auto map=root+records+selected*stride+ids;
                        for(uint32_t p=0;p<capacity;p++)Memory::Write8(map+p,p<active?p:255);
                        // Poison the bytes beyond the map with valid AIDs. Rounded
                        // unrolled loads must not count these as extra participants.
                        for(uint32_t p=0;p<6 && map+capacity+p<root+size;p++)Memory::Write8(map+capacity+p,0);
                        ctx.gpr[3]=root;func_8065BE78(&ctx);
                        if(ctx.gpr[3]!=active){std::fprintf(stderr,"RH1 count selected=%u active=%u got=%u capacity=%u\n",selected,active,ctx.gpr[3],capacity);return 21;}
                    }
                    if(capacity<=32)for(uint32_t selected : {0u,capacity-1})for(uint32_t team=0;team<2;team++){
                        for(uint32_t r=0;r<recordCount;r++)Memory::Write32(root+records+r*stride+20,0xffffffff);
                        Memory::Write32(root+records+selected*stride+20,0);
                        Memory::Write32(root+records+selected*stride+24,team?0xffffffffu:0);
                        for(uint32_t p=0;p<capacity;p++)Memory::Write8(controller+{{layout.Symbol("RKNetController.playerAids.offset")}}+p,p);
                        ctx.gpr[3]=root;ctx.gpr[4]=capacity-1;ctx.gpr[5]=0;func_8065BA98(&ctx);
                        if(ctx.gpr[3]!=(team?0:1))return 23;
                        ctx.gpr[3]=root;func_8065BD70(&ctx);if(ctx.gpr[3]!=team)return 26;
                    }
                    if(capacity<=32)for(uint32_t current=0;current<2;current++)for(uint32_t local : {0u,capacity-1})for(bool sparse : {false,true}){
                        uint32_t mask=capacity==32?0xffffffffu:((1u<<capacity)-1);
                        if(sparse)mask=(1u<<local)|(1u<<(local?0:capacity-1));
                        Memory::Write32(controller+{{layout.Symbol("RKNetController.currentSub.offset")}},current);
                        auto sub=controller+{{layout.Symbol("RKNetController.subs.offset")}}+current*{{layout.Symbol("RKNetSub.size")}};
                        Memory::Write32(sub+16,mask);Memory::Write8(sub+33,local);
                        Memory::Write32(root+4,1);Memory::Write32(root+8,0);Memory::Write32(root+12,0);Memory::Write8(root+16,local);
                        uint32_t previous=local;sent=0;
                        for(uint32_t step=0;step<capacity*2;step++){
                            auto next=(previous+1)%capacity;while(next==local || !(mask&(1u<<next)))next=(next+1)%capacity;
                            ctx.gpr[3]=root;func_8065C714(&ctx);
                            if(sent!=step+1 || last!=next || Memory::Read8(root+16)!=next){std::fprintf(stderr,"RH1 scheduler n=%u previous=%u expected=%u got=%u sends=%u\n",capacity,previous,next,last,sent);return 22;}
                            previous=next;
                        }
                    }
                    for(uint32_t current=0;current<2;current++)for(uint32_t local : {0u,capacity-1}){
                        Memory::Write32(controller+{{layout.Symbol("RKNetController.currentSub.offset")}},current);
                        Memory::Write8(controller+{{layout.Symbol("RKNetController.subs.offset")}}+current*{{layout.Symbol("RKNetSub.size")}}+33,local);
                        for(uint32_t p=0;p<recordCount;p++)Memory::Write32(root+records+p*stride,p<capacity?100+p:0);
                        Memory::Write32(controller+biggestTimer,0);
                        ctx.gpr[3]=root;ctx.gpr[4]=0;func_8065C444(&ctx);
                        if(ctx.gpr[3]!=root || Memory::Read32(controller+biggestTimer)!=100+capacity-1-(local==capacity-1))return 27;
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
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Race header storage compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Race header storage execution timed out");
            Assert.True(run.ExitCode==0,$"Race header storage returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
