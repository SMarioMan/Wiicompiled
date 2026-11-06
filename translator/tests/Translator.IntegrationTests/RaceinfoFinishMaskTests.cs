using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class RaceinfoFinishMaskTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealFinishAndResetRetainEveryParticipantInUniformBitsetWords(int players)
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
        var temp=Path.Combine(Path.GetTempPath(),"mkw-finish-mask-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var entries=new uint[]{0x8052f64c,0x8052fcac};
            var fixtures=new HashSet<uint>{0x8054f5ac,0x805898ec,0x8052f124};
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
                constexpr uint32_t n={{players}},size={{layout.Symbol("RaceinfoPlayer.size")}},
                    words={{layout.Symbol("RaceinfoPlayer.finishedPlayers.count")}},order={{layout.Symbol("RaceinfoPlayer.finishOrder.offset")}},
                    stride=(size+47)&~15u,records=0x81100010,root=0x81010000,pointers=0x81020000,kmp=0x81030000,
                    input=0x81050000,timers=0x81300000,racedata=0x81400000,stack=0x81f00000;
                uint32_t current=0,notifications=0;
                void no_op(CpuContext*){}
                void random(CpuContext* ctx){ctx->gpr[3]=0;}
                void notify(CpuContext* ctx){if(ctx->gpr[3]!=root || ctx->gpr[4]!=current)std::abort();notifications++;}
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="finish state fixture";
                    info.rawCpuInvoker=entry;info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                uint32_t record(uint32_t id){return records+id*stride;}
                bool guards(uint32_t id){auto p=record(id);for(uint32_t i=0;i<16;i++)if(Memory::Read8(p-16+i)!=0x55 || Memory::Read8(p+size+i)!=0x55)return false;return true;}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);FILE* inputFile=std::fopen(argv[1],"rb");if(!inputFile)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},inputFile);
                    std::fclose(inputFile);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    register_entry(0x8054f5ac,random);register_entry(0x805898ec,no_op);register_entry(0x8052f124,notify);
                    Memory::Write32(0x809b8f70,root);Memory::Write32(root+12,pointers);Memory::Write32(0x809b8f28,kmp);
                    Memory::Write32(kmp+32,0);Memory::Write32(0x809b8f68,racedata);
                    Memory::Write16(input+4,7);Memory::Write8(input+6,42);Memory::Write16(input+8,999);Memory::Write8(input+10,1);
                    CpuContext ctx{};ctx.gpr[1]=stack;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                    for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                    for(uint32_t id=0;id<n;id++){
                        auto p=record(id);std::memset(Memory::GetPointer(p-16,size+32),0x55,size+32);
                        Memory::Write32(pointers+id*4,p);Memory::Write8(p+8,id);Memory::Write32(p+64,timers+id*16);
                        Memory::Write8(racedata+46+id*240,0xff);
                        ctx.gpr[3]=p;func_8052F64C(&ctx);
                        for(uint32_t word=0;word<words;word++)if(Memory::Read16(p+80+word*2)!=0)return 4;
                        if(Memory::Read8(p+order)!=n+1 || !guards(id))return 5;
                    }
                    for(uint32_t active=1;active<=n;active++)for(current=0;current<active;current++)for(uint32_t pattern=0;pattern<4;pattern++){
                        Memory::Write8(racedata+36,active);Memory::Write8(root+28,active/2);
                        std::array<uint16_t,words> expected{};
                        for(uint32_t id=0;id<n;id++){
                            auto flag=pattern==1?2:pattern==2?(id%2?8:0):pattern==3 && id>=16?8:0;
                            Memory::Write32(record(id)+56,flag);
                            if(id<active && flag)expected[id/16]|=uint16_t(1u<<(id%16));
                        }
                        auto p=record(current);for(uint32_t word=0;word<words;word++)Memory::Write16(p+80+word*2,0xffff);
                        auto flags=Memory::Read32(p+56);ctx.gpr[3]=p;ctx.gpr[4]=input;ctx.gpr[5]=pattern%2;ctx.gpr[6]=active*100+current;
                        auto before=notifications;func_8052FCAC(&ctx);
                        if(notifications!=before+1 || Memory::Read8(p+order)!=active/2 || Memory::Read32(p+68)!=active*100+current)return 6;
                        for(uint32_t word=0;word<words;word++)if(Memory::Read16(p+80+word*2)!=expected[word]){
                            std::fprintf(stderr,"n=%u active=%u player=%u pattern=%u word=%u got=%04x expected=%04x\n",n,active,current,pattern,word,Memory::Read16(p+80+word*2),expected[word]);return 7;}
                        if(Memory::Read32(p+56)!=(flags|2|(pattern%2?64:0)) || !guards(current))return 8;
                        if(Memory::Read16(timers+current*16+4)!=7 || Memory::Read8(timers+current*16+6)!=42 ||
                            Memory::Read16(timers+current*16+8)!=999 || Memory::Read8(timers+current*16+10)!=1)return 9;
                        ctx.gpr[3]=p;func_8052F64C(&ctx);
                        for(uint32_t word=0;word<words;word++)if(Memory::Read16(p+80+word*2)!=0)return 10;
                        if(Memory::Read8(p+order)!=n+1 || !guards(current))return 11;
                    }
                    for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r)return 12;
                    if(ctx.gpr[1]!=stack)return 13;
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Finish mask compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Finish mask execution timed out");
            Assert.True(run.ExitCode==0,$"Finish mask returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
