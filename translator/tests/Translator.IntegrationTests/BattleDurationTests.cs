using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Buffers.Binary;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class BattleDurationTests
{
    [Theory]
    [InlineData(12,false)] [InlineData(13,false)] [InlineData(24,false)]
    [InlineData(25,false)] [InlineData(26,false)] [InlineData(48,false)]
    [InlineData(52,false)] [InlineData(64,false)] [InlineData(100,false)]
    [InlineData(116,false)] [InlineData(118,false)] [InlineData(127,false)]
    [InlineData(128,false)] [InlineData(128,true)]
    [Trait("Requires", "USA game assets and Clang")]
    public void ConstructorAndDurationHelperUseTheAuthoredArenaRow(int players,bool omitInlineFix)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName??throw new DirectoryNotFoundException();
        var manifest=Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json");
        using var definition=JsonDocument.Parse(File.ReadAllText(manifest));
        var layout=new GuestLayouts(definition.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!,players);
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        uint Word(uint a)=>BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(a,4),4));
        Assert.Equal(0x381dfffeu,Word(0x8052e06c));Assert.Equal(0x381dfffeu,Word(0x8052e0a4));
        // Isolate the original inline lookup and timer stores while retaining
        // the constructor's real prologue, preserved registers and epilogue.
        foreach(var (pc,word,target) in new[]{(0x8052dc80u,0x90830000u,0x8052e040u),(0x8052e12cu,0x48000014u,0x8052e1d8u)}){
            Assert.Equal(word,Word(pc));BinaryPrimitives.WriteUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(pc,4),4),0x48000000u|((target-pc)&0x03fffffcu));}
        var temp=Path.Combine(Path.GetTempPath(),"mkw-battle-duration-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try{
            var inputManifest=manifest;
            if(omitInlineFix){
                var node=JsonNode.Parse(File.ReadAllText(manifest))!;var indices=node["AuthoredCountIndices"]!.AsArray();
                foreach(var index in indices.Where(x=>x!["Instruction"]!.GetValue<string>() is "0x8052E06C" or "0x8052E0A4").ToArray())indices.Remove(index);
                inputManifest=Path.Combine(temp,"without-inline-fix.json");File.WriteAllText(inputManifest,node.ToJsonString());
            }
            var plan=GuestLayoutRelocations.Read(inputManifest,players);plan.ValidateImage(image);
            var translator=new FunctionTranslator(image,layoutRelocations:plan);
            var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));
            var options=new TranslationOptions(EnableLeafInlining:false,KnownFunctionEntryPoints:map.Addresses.ToHashSet());
            if(players==12)Assert.Equal(new FunctionTranslator(image).Translate(0x8052dc58,options).CxxCode,translator.Translate(0x8052dc58,options).CxxCode);
            var entries=new uint[]{0x8052dc58,0x8052dba4,0x805337fc};var sources=new List<string>();
            foreach(var pc in entries){var file=Path.Combine(temp,$"{pc:X8}.cpp");File.WriteAllText(file,translator.Translate(pc,options).CxxCode);sources.Add(file);}
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var harness=Path.Combine(temp,"main.cpp");var exe=Path.Combine(temp,"test");
            var declarations=string.Join("\n",entries.Select(pc=>$"extern \"C\" void func_{pc:X8}(CpuContext*);"));
            var registrations=string.Join("\n",entries.Select(pc=>$"entry(0x{pc:X8},func_{pc:X8});"));
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <algorithm>
                #include <cstdio>
                #include <cstring>
                {{declarations}}
                constexpr uint32_t n={{players}},data=0x81000000,info=0x81100000,timers=0x81101000,holder=0x81102000,
                    table=0x81103000,sp=0x81f00000,race=data+{{layout.Symbol("Racedata.race.offset")}},
                    settings=race+{{layout.Symbol("Scenario.settings.offset")}};
                void entry(uint32_t a,void(*fn)(CpuContext*)){TranslatedFunctionInfo i{};i.address=a;i.name="battle duration fixture";i.rawCpuInvoker=fn;i.entryPoint=reinterpret_cast<void*>(fn);TranslatedFunctionRegistry::Register(i);}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);FILE* file=std::fopen(argv[1],"rb");if(!file)return 2;
                    if(std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},file)!={{image.Memory.Length}})return 3;std::fclose(file);
                    {{registrations}}
                    Memory::Write32(0x809b8f68,data);Memory::Write32(info+0x14,timers);Memory::Write32(info+0x3c,holder);Memory::Write32(holder+4,table);
                    for(unsigned i=0;i<0x1000;i+=2)Memory::Write16(table+i,20);
                    Memory::Write32(table,0x524b4d47);
                    for(unsigned mode=0;mode<2;mode++)for(unsigned arena=0;arena<10;arena++)for(unsigned count=2;count<=12;count++)
                        Memory::Write16(table+(mode?0x1dc:0x10)+arena*22+(count-2)*2,180+arena*10+mode*120+count-12);
                    CpuContext c{};CpuContextScope cpu(&c);c.gpr[1]=sp;c.gpr[2]=0x8038ac20;c.gpr[13]=0x80388880;
                    for(unsigned mode=0;mode<2;mode++)for(unsigned arena=0;arena<10;arena++)for(unsigned count=2;count<=n;count++){
                        if({{(omitInlineFix?"true":"false")}} && (mode||arena||count!=128))continue;
                        Memory::Write8(race+4,count);Memory::Write32(settings,Memory::Read32(0x8088bdc8+arena*4));Memory::Write32(settings+0x10,mode);
                        std::memset(Memory::GetPointer(timers,0x60),0x55,0x60);
                        std::memset(Memory::GetPointer(sp-0x90-16,0xc0),0x55,0xc0);
                        for(unsigned r=14;r<32;r++)c.gpr[r]=0x12340000+r;
                        c.gpr[30]=holder;c.gpr[3]=info;
                        const auto authored=180+arena*10+mode*120+std::clamp(count,2u,12u)-12;
                        const auto expected={{(omitInlineFix?"20u":"authored")}};
                        func_8052DC58(&c);
                        if(c.gpr[1]!=sp||c.gpr[3]!=info||Memory::Read32(info+0x3c)!=holder)return 4;
                        for(unsigned offset:{0x14u,0x20u})
                            if(Memory::Read16(timers+offset)!=expected/60||Memory::Read8(timers+offset+2)!=expected%60||Memory::Read16(timers+offset+4)||Memory::Read8(timers+offset+6)!=1){std::fprintf(stderr,"mode=%u arena=%u count=%u expected=%u offset=%x got=%u:%u.%u flag=%u\n",mode,arena,count,expected,offset,Memory::Read16(timers+offset),Memory::Read8(timers+offset+2),Memory::Read16(timers+offset+4),Memory::Read8(timers+offset+6));return 5;}
                        if(Memory::Read32(timers+0x44)!=expected*1000||Memory::Read8(timers+0x42)!=1)return 6;
                        for(unsigned r=14;r<32;r++)if(c.gpr[r]!=(r==30?holder:0x12340000+r))return 7;
                        c.gpr[3]=info;func_8052DBA4(&c);if(c.gpr[3]!=authored||c.gpr[1]!=sp)return 8;
                        for(unsigned i=0;i<16;i++)if(Memory::Read8(sp-0xa0+i)!=0x55||Memory::Read8(sp+8+i)!=0x55||Memory::Read8(timers+i)!=0x55||Memory::Read8(timers+0x48+i)!=0x55)return 9;
                    }
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!){
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(45000);if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Battle duration compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());}
            using var run=Process.Start(new ProcessStartInfo(exe,binary){RedirectStandardError=true})!;var output=run.StandardError.ReadToEndAsync();
            var finished=run.WaitForExit(10000);if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Battle duration execution timed out");
            Assert.True(run.ExitCode==0,$"exit {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }finally{Directory.Delete(temp,true);}
    }
}
