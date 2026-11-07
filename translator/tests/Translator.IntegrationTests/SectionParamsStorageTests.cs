using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class SectionParamsStorageTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealMenuParametersInitializeUniformOnlineArraysAndAllocatePlayerMiis(int players)
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
        var temp=Path.Combine(Path.GetTempPath(),"mkw-section-params-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var entries=new uint[]{0x805cd184,0x805cd2a4,0x805cddcc,0x805ce2cc,0x80006038,0x80005f84};
            var fixtures=new HashSet<uint>{0x805d9594,0x805d9768,0x805d9a8c,0x80517dfc};
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
                constexpr uint32_t capacity={{players}},size={{layout.Symbol("SectionParams.size")}},
                    online={{layout.Symbol("SectionParams.online.offset")}},onlineSize={{layout.Symbol("OnlineParams.size")}},
                    friends={{layout.Symbol("OnlineParams.friendIndices.offset")}},teams={{layout.Symbol("OnlineParams.teams.offset")}},
                    unknown={{layout.Symbol("OnlineParams.unknown.offset")}},ranks={{layout.Symbol("OnlineParams.rankBMG.offset")}},
                    regions={{layout.Symbol("OnlineParams.regionIds.offset")}},tail={{layout.Symbol("SectionParams.tail.offset")}},
                    root=0x81010000,stack=0x81f00000;
                uint32_t constructors=0,initializations=0,resets=0,ghosts=0;
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="menu parameter fixture";info.rawCpuInvoker=entry;
                    info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                void mii_ctor(CpuContext* c){
                    if(c->gpr[3]!=root+(constructors==0?392:568) || constructors++>=2)std::abort();}
                void mii_init(CpuContext* c){
                    const bool player=initializations==0;
                    if(c->gpr[3]!=root+(player?392:568) || c->gpr[4]!=(player?capacity:2) ||
                        c->gpr[5]!=(player?36:0) || c->gpr[6]!=0x1234 || initializations++>=2)std::abort();
                    Memory::Write32(c->gpr[3]+40,c->gpr[4]);}
                void mii_reset(CpuContext* c){
                    const bool player=resets%2==0;
                    if(c->gpr[3]!=root+(player?392:568) || Memory::Read32(c->gpr[3]+40)!=(player?capacity:2))std::abort();resets++;}
                void ghost_ctor(CpuContext* c){if(c->gpr[3]!=root+tail+32 || ghosts++)std::abort();}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    register_entry(0x805d9594,mii_ctor);register_entry(0x805d9768,mii_init);
                    register_entry(0x805d9a8c,mii_reset);register_entry(0x80517dfc,ghost_ctor);
                    Memory::Write32(0x809b8f80,0x81090000);Memory::Write32(0x81090034,0x1234);
                    Memory::Write32(0x809bd508,0x810a0000);Memory::Write32(0x810a0000,0);
                    Memory::Write32(0x809b8f88,0x810b0000);Memory::Write16(0x810b0036,0xffff);
                    CpuContext ctx{};ctx.gpr[1]=stack;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                    for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                    std::array<uint8_t,onlineSize> expected{};
                    auto word=[&](uint32_t at,uint32_t value){for(uint32_t b=0;b<4;b++)expected[at+b]=value>>(24-b*8);};
                    auto initialize_expected=[&](){
                        expected.fill(0x55);word(0,0xffffffff);word(4,0);
                        for(uint32_t p=0;p<capacity;p++){expected[friends+p]=255;word(ranks+p*4,0);word(regions+p*4,15);}
                        if(capacity==12){
                            // Golden bytes from the original reset's interleaved stores.
                            // Stock overlapping writes are preserved at capacity 12.
                            const char* golden="ffffffff00000000ffffffffffffffffffffffff00000002000000020000000200000002000000020000000200000002000000020000000200000002000000020000000200000002000000020000000200000002000000020000000200000002000000020000000200000002000000020000000200000002000000020000000200000002000000020000000200000002000000ffffff00020000000200000000000000000000000000000000000000000000000000000000000000000000000f0000000f0000000f0000000f0000000f0000000f0000000f0000000f0000000f0000000f0000000f0000000f";
                            auto nibble=[](char c){return c<='9'?c-'0':c-'a'+10;};
                            for(uint32_t b=0;b<onlineSize;b++)expected[b]=(nibble(golden[b*2])<<4)|nibble(golden[b*2+1]);
                        }else for(uint32_t p=0;p<capacity;p++){
                            word(teams+p*8,2);word(teams+p*8+4,2);
                            expected[unknown+p*2]=expected[unknown+p*2+1]=255;
                        }
                    };
                    initialize_expected();
                    std::memset(Memory::GetPointer(root-16,size+32),0x55,size+32);
                    ctx.gpr[3]=root+online;func_805CDDCC(&ctx);
                    if(std::memcmp(expected.data(),Memory::GetPointer(root+online,onlineSize),onlineSize)){
                        for(uint32_t b=0;b<onlineSize;b++)if(expected[b]!=Memory::Read8(root+online+b))
                            std::fprintf(stderr,"online byte %u expected %02x got %02x\n",b,expected[b],Memory::Read8(root+online+b));return 4;}
                    for(uint32_t b=0;b<size;b++)if((b<online || b>=online+onlineSize) && Memory::Read8(root+b)!=0x55)return 5;
                    std::memset(Memory::GetPointer(root,size),0x55,size);ctx.gpr[3]=root;func_805CD184(&ctx);
                    if(ctx.gpr[3]!=root || constructors!=2 || initializations!=2 || resets!=2 || ghosts!=1)return 6;
                    if(std::memcmp(expected.data(),Memory::GetPointer(root+online,onlineSize),onlineSize))return 7;
                    if(Memory::Read32(root+tail)!=8 || Memory::Read32(root+tail+4)!=0 ||
                        Memory::Read32(root+tail+12)!=0xffffffff || Memory::Read32(root+tail+332)!=0xffffffff)return 8;
                    std::memset(Memory::GetPointer(root+online,onlineSize),0x55,onlineSize);
                    ctx.gpr[3]=root;func_805CD2A4(&ctx);
                    if(resets!=4 || std::memcmp(expected.data(),Memory::GetPointer(root+online,onlineSize),onlineSize))return 9;
                    for(uint32_t i=0;i<16;i++)if(Memory::Read8(root-16+i)!=0x55 || Memory::Read8(root+size+i)!=0x55)return 10;
                    for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r)return 11;
                    if(ctx.gpr[1]!=stack)return 12;
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Menu parameter storage compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Menu parameter storage execution timed out");
            Assert.True(run.ExitCode==0,$"Menu parameter storage returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
