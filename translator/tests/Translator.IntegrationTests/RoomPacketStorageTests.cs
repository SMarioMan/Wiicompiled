using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class RoomPacketStorageTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealRoomLifecycleAndMessagesCoverEveryPeerWithoutOverwritingLocalOrAdjacentState(int players)
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
        var temp=Path.Combine(Path.GetTempPath(),"mkw-room-storage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var entries=new uint[]{0x80656600,0x80656704,0x8065680c,0x806569e8,0x80656afc,0x80656dd8,
                0x80656adc,0x80656e64,0x80656e78,0x80656e8c,0x80656e9c,0x80656eac,0x80656ebc};
            var fixtures=new HashSet<uint>{0x80229a48};
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
                constexpr uint32_t capacity={{players}},size={{layout.Symbol("RKNetRoomHandler.size")}},
                    send={{layout.Symbol("RKNetRoomHandler.send.offset")}},received={{layout.Symbol("RKNetRoomHandler.received.offset")}},
                    tail={{layout.Symbol("RKNetRoomHandler.tail.offset")}},
                    controllerSize={{layout.Symbol("RKNetController.size")}},subs={{layout.Symbol("RKNetController.subs.offset")}},
                    subStride={{layout.Symbol("RKNetSub.size")}},currentSub={{layout.Symbol("RKNetController.currentSub.offset")}},
                    status={{layout.Symbol("RKNetController.status.offset")}},
                    root=0x81010000,controller=0x81030000,stack=0x81f00000;
                uint32_t allocations=0;
                void allocate(CpuContext* c){if(c->gpr[3]!=size || allocations++)std::abort();c->gpr[3]=root;}
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="room packet fixture";info.rawCpuInvoker=entry;
                    info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    register_entry(0x80229a48,allocate);Memory::Write32(0x809bd918,controller);
                    CpuContext ctx{};ctx.gpr[1]=stack;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                    for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                    std::array<uint8_t,size> expected{};
                    auto word=[&](uint32_t at,uint32_t value){for(uint32_t b=0;b<4;b++)expected[at+b]=value>>(24-b*8);};
                    auto clear_expected=[&](bool header){if(header)expected[0]=0;std::memset(expected.data()+send,0,capacity*8);
                        expected[tail]=255;std::memset(expected.data()+tail+8,0,16);};
                    auto equal=[&](){return std::memcmp(expected.data(),Memory::GetPointer(root,size),size)==0;};
                    for(uint32_t kind=0;kind<4;kind++){
                        std::memset(Memory::GetPointer(root-16,size+32),0x55,size+32);expected.fill(0x55);
                        ctx.gpr[3]=root;
                        if(kind==0){Memory::Write32(0x809bd920,0);func_80656600(&ctx);
                            if(allocations!=1 || Memory::Read32(0x809bd920)!=root)return 4;
                            func_80656600(&ctx);if(allocations!=1)return 5;
                        }else if(kind==1)func_80656AFC(&ctx);else if(kind==2)func_8065680C(&ctx);else func_80656DD8(&ctx);
                        clear_expected(kind!=3);if(!equal())return 6;
                    }
                    for(uint32_t current=0;current<2;current++)for(uint32_t role=0;role<2;role++){
                        expected.fill(0x55);std::memset(Memory::GetPointer(root-16,size+32),0x55,size+32);
                        std::array<uint8_t,controllerSize> other{};other.fill(0x22);
                        auto other_word=[&](uint32_t at,uint32_t value){for(uint32_t b=0;b<4;b++)other[at+b]=value>>(24-b*8);};
                        other_word(currentSub,current);other_word(subs+12,capacity);other_word(subs+subStride+12,capacity-1);
                        std::memcpy(Memory::GetPointer(controller,controllerSize),other.data(),controllerSize);
                        ctx.gpr[3]=root;ctx.gpr[4]=role;func_80656704(&ctx);
                        clear_expected(false);expected[0]=1;word(4,role);if(!equal())return 7;
                        other[status+4]=255;other[status+5]=role?4:3;other[status+6]=capacity-current;other[status+7]=0;
                        if(std::memcmp(other.data(),Memory::GetPointer(controller,controllerSize),controllerSize))return 8;
                        for(uint32_t local=0;local<capacity;local++){
                            // Preserve distinct receive data while sending one message to
                            // every other peer, including a partial final six-peer group.
                            for(uint32_t p=0;p<capacity;p++){word(send+p*4,0x11110000+p);word(received+p*4,0x22220000+p);}
                            std::memcpy(Memory::GetPointer(root,size),expected.data(),size);
                            Memory::Write8(controller+subs+current*subStride+33,local);
                            ctx.gpr[3]=root;ctx.gpr[4]=0x33445566;func_806569E8(&ctx);
                            for(uint32_t p=0;p<capacity;p++)if(p!=local)word(send+p*4,0x33445566);
                            if(!equal())return 9;
                        }
                    }
                    for(uint32_t p=0;p<capacity;p++){
                        ctx.gpr[3]=root;ctx.gpr[4]=p;ctx.gpr[5]=0x12340000+p;func_80656E8C(&ctx);word(send+p*4,0x12340000+p);
                        ctx.gpr[3]=root;ctx.gpr[4]=p;ctx.gpr[5]=0x56780000+p;func_80656E9C(&ctx);word(received+p*4,0x56780000+p);
                        if(!equal())return 10;
                        ctx.gpr[3]=root;ctx.gpr[4]=p;func_80656ADC(&ctx);if(ctx.gpr[3]!=0x56780000+p)return 11;
                        ctx.gpr[3]=root;ctx.gpr[4]=p;func_80656EAC(&ctx);if(ctx.gpr[3]!=root+send+p*4)return 12;
                        ctx.gpr[3]=root;ctx.gpr[4]=p;func_80656EBC(&ctx);if(ctx.gpr[3]!=root+received+p*4)return 13;
                        ctx.gpr[3]=root;ctx.gpr[4]=p;func_80656E64(&ctx);word(send+p*4,0);
                        ctx.gpr[3]=root;ctx.gpr[4]=p;func_80656E78(&ctx);word(received+p*4,0);if(!equal())return 14;
                    }
                    for(uint32_t i=0;i<16;i++)if(Memory::Read8(root-16+i)!=0x55 || Memory::Read8(root+size+i)!=0x55)return 15;
                    for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r)return 16;
                    if(ctx.gpr[1]!=stack)return 17;
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Room packet storage compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Room packet storage execution timed out");
            Assert.True(run.ExitCode==0,$"Room packet storage returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
