using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class EffectActivityStorageTests
{
    [Fact]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealEffectMemoryManagerBacksEveryExpandedNw4rActivityList()
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName??throw new DirectoryNotFoundException("Repository root missing");
        using var document=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json")));
        var bootstrap=document.RootElement.GetProperty("References").EnumerateArray().Single(r=>r.GetProperty("Instruction").GetString()=="0x806770A4");
        Assert.Equal("players",bootstrap.GetProperty("Symbol").GetString());
        var reserved=bootstrap.GetProperty("Addend").GetInt32();
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));
        var translator=new FunctionTranslator(image);
        var temp=Path.Combine(Path.GetTempPath(),"mkw-effect-activity-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try{
            var entries=new uint[]{0x80225184,0x802246c8,0x80224f54,0x80224f6c,0x80224b64,0x80224c08,0x80224cac,0x80224d50,
                0x80224df4,0x80224dfc,0x80224e04,0x800270a0,0x800271b0,0x800202d4,0x800aeec0,0x80020a3c,0x80020a88};
            var fixtures=new HashSet<uint>{0x80229a80,0x80229490,0x80229a5c,0x80027090,0x800125c0};
            var sources=new List<string>();
            foreach(var pc in entries){var path=Path.Combine(temp,$"{pc:X8}.cpp");
                File.WriteAllText(path,translator.Translate(pc,new TranslationOptions(EnableLeafInlining:false,
                    KnownFunctionEntryPoints:map.Addresses.ToHashSet(),LeafInliningBlockedTargets:fixtures)).CxxCode);sources.Add(path);}
            var declarations=string.Join("\n",entries.Select(pc=>$"extern \"C\" void func_{pc:X8}(CpuContext*);"));
            var registrations=string.Join("\n",entries.Select(pc=>$"entry(0x{pc:X8},func_{pc:X8});"));
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <cstdio>
                #include <cstring>
                {{declarations}}
                constexpr uint32_t manager=0x81010000,slots=0x81020000,allocator=0x81030000,heap=0x81040000,
                    effectRoot=0x81050000,config=0x81060000,arena=0x81100000,stack=0x81f00000;
                uint32_t groups=0,budget=0,tableCalls=0,arenaCalls=0,managerCalls=0;
                uint32_t aligned(uint32_t n){return(n+31)&~31u;}
                void entry(uint32_t address,void(*fn)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="real effect backing fixture";
                    info.rawCpuInvoker=fn;info.entryPoint=reinterpret_cast<void*>(fn);TranslatedFunctionRegistry::Register(info);}
                void allocTable(CpuContext* c){if(c->gpr[3]!=groups*4||c->gpr[4]!=heap||c->gpr[5]!=4)std::abort();++tableCalls;c->gpr[3]=slots;}
                void allocArena(CpuContext* c){
                    // Other particle/effect pools are configured empty here;
                    // their fixed pool headers still run through the real allocator.
                    if(c->gpr[3]!=budget||c->gpr[4]!=32||c->gpr[5]!=heap)std::abort();
                    ++arenaCalls;c->gpr[3]=arena;
                }
                void allocManager(CpuContext* c){if(c->gpr[3]!=0x4c||c->gpr[4]!=heap||c->gpr[5]!=4)std::abort();++managerCalls;c->gpr[3]=allocator;}
                void effectSystem(CpuContext* c){c->gpr[3]=effectRoot;}
                void noOp(CpuContext*){}
                bool guarded(uint32_t base,uint32_t bytes){for(uint32_t i=0;i<16;i++)if(Memory::Read8(base-16+i)!=0x55||Memory::Read8(base+bytes+i)!=0x55)return false;return true;}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);FILE* f=std::fopen(argv[1],"rb");if(!f)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},f);std::fclose(f);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    entry(0x80229a80,allocTable);entry(0x80229490,allocArena);entry(0x80229a5c,allocManager);
                    entry(0x80027090,effectSystem);entry(0x800125c0,noOp);
                    CpuContext c{};CpuContextScope scope(&c);c.gpr[1]=stack;c.gpr[13]=0x80388880;
                    for(uint32_t players:{12u,13u,24u,25u,26u,48u,52u,64u,100u,116u,118u,127u,128u}){
                        groups=players+{{reserved}};const auto activity=groups*4;budget=4*32+aligned(activity*28+32)+0x220;
                        for(unsigned cycle=0;cycle<2;cycle++){
                            tableCalls=arenaCalls=managerCalls=0;
                            std::memset(Memory::GetPointer(manager,48),0,48);
                            std::memset(Memory::GetPointer(config,20),0,20);Memory::Write32(config,heap);
                            std::memset(Memory::GetPointer(arena-16,budget+32),0x55,budget+32);
                            std::memset(Memory::GetPointer(allocator-16,0x4c+32),0x55,0x4c+32);
                            std::memset(Memory::GetPointer(slots-16,groups*4+32),0x55,groups*4+32);
                            for(uint32_t r=14;r<32;r++)c.gpr[r]=0x12340000+r;
                            c.gpr[3]=manager;c.gpr[4]=groups;c.gpr[5]=heap;func_80225184(&c);
                            if(Memory::Read32(manager)!=activity||Memory::Read32(manager+4)!=groups||Memory::Read32(manager+40)!=slots)return 4;
                            c.gpr[3]=manager;c.gpr[4]=config;func_802246C8(&c);
                            if(c.gpr[3]!=budget||c.gpr[1]!=stack||tableCalls!=1||arenaCalls!=1||managerCalls!=1)return 5;
                            for(uint32_t r=14;r<32;r++)if(c.gpr[r]!=0x12340000+r)return 6;
                            if(Memory::Read32(manager+44)!=arena||Memory::Read32(effectRoot)!=allocator||Memory::Read32(effectRoot+0xc014)!=activity)return 7;
                            auto lists=Memory::Read32(effectRoot+0xc018);
                            if(lists<arena+16||lists+activity*28>arena+budget||Memory::Read32(lists-16)!=28||Memory::Read32(lists-12)!=activity)return 8;
                            for(uint32_t i=0;i<activity;i++){
                                auto p=lists+i*28;
                                if(Memory::Read32(p)||Memory::Read32(p+4)||Memory::Read16(p+8)||Memory::Read16(p+10)!=20||
                                   Memory::Read32(p+12)||Memory::Read32(p+16)||Memory::Read16(p+20)||Memory::Read16(p+22)!=20||Memory::Read16(p+24))return 9;
                            }
                            for(uint32_t i=0;i<groups;i++)if(Memory::Read32(slots+i*4))return 10;
                            if(!guarded(arena,budget)||!guarded(allocator,0x4c)||!guarded(slots,groups*4))return 11;
                            const auto highestRacerGroup=(players-1+2)*4+3;
                            if(highestRacerGroup>=activity)return 12;
                        }
                    }
                }
                """);
            var args=TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",args){RedirectStandardError=true})!){
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);if(!done)compile.Kill(entireProcessTree:true);
                Assert.True(done,"Effect backing compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());}
            using var run=Process.Start(new ProcessStartInfo(exe,binary){RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(30000);if(!finished)run.Kill(entireProcessTree:true);
            Assert.True(finished,"Effect backing execution timed out");Assert.True(run.ExitCode==0,$"Effect backing returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }finally{Directory.Delete(temp,recursive:true);}
    }
}
