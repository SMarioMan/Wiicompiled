using System.Diagnostics;
using Translator.Core.Loading;
using Xunit;

namespace Translator.Tests;

public class RaceSoundRankTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(128)]
    [Trait("Requires", "USA game assets and Clang")]
    public void ExpandedRankSequenceKeepsTheAuthoredAxisAndOriginalSentinels(int capacity)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName??throw new DirectoryNotFoundException("Repository root missing");
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        uint Word(uint address)=>System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(address,4),4));
        Assert.Equal(0x88040020u,Word(0x8070eca4));Assert.Equal(0x7c000774u,Word(0x8070eca8));
        Assert.Equal(0x2000000cu,Word(0x8070ecac));Assert.Equal(0x7c040734u,Word(0x8070ecb0));
        Assert.Equal(0x4800157du,Word(0x8070ecb4));Assert.Equal(0x38a00000u,Word(0x8070ec94));
        var temp=Path.Combine(Path.GetTempPath(),"mkw-race-sound-rank-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try{
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include "mkw_guest_layout.h"
                #include <array>
                #include <cstdint>
                extern "C" void Audio_WriteExpandedRaceRankVariable(CpuContext*);
                namespace MkwGuestLayout {const Layout kLayout{.playerCapacity={{capacity}},.racedataRace=32};}
                constexpr uint32_t data=0x80020000,handle=0x80030000,caller=0x8070ecb8;
                uint32_t expected=0,variable=0,calls=0;
                void original(CpuContext* c){if(c->gpr[3]!=handle||c->gpr[4]!=expected||c->gpr[5]!=variable)std::abort();++calls;c->gpr[3]=0x12345678;}
                int main(){
                    Memory::Init(16*1024*1024);
                    TranslatedFunctionInfo info{};info.address=0x80710230;info.name="original sequence-variable fixture";
                    info.rawCpuInvoker=original;info.entryPoint=reinterpret_cast<void*>(original);TranslatedFunctionRegistry::Register(info);
                    CpuContext c{};CpuContextScope scope(&c);Memory::Write32(0x809b8f68,data);
                    for(uint32_t count:{0u,1u,2u,12u,13u,24u,25u,26u,48u,52u,64u,100u,116u,118u,127u,128u}){
                        Memory::Write8(data+36,count);uint32_t previous=12;
                        for(uint32_t rank=0;rank<256;rank++)for(uint32_t variant=0;variant<3;variant++){
                            auto old=uint32_t(int32_t(12-int8_t(rank)));
                            expected=old;variable=variant==2?1:0;
                            const bool normalize={{capacity}}>12&&count>12&&count<={{capacity}}&&rank>=1&&rank<=count&&variant==0;
                            if(normalize){
                                expected=11-(rank-1)*12/count;
                                if(expected>11 || expected>previous)return 1;
                                previous=expected;
                                if(rank==1&&expected!=11)return 2;
                                if(rank==count&&expected!=0)return 3;
                            }
                            c.gpr[3]=handle;c.gpr[4]=old;c.gpr[5]=variable;c.lr=variant==1?caller+4:caller;
                            c.gpr[1]=0x800f0000;c.gpr[31]=0x13579bdf;
                            Audio_WriteExpandedRaceRankVariable(&c);
                            if(c.gpr[3]!=0x12345678||c.gpr[1]!=0x800f0000||c.gpr[31]!=0x13579bdf)return 4;
                        }
                    }
                    // No race data is another original-value path.
                    Memory::Write32(0x809b8f68,0);c.gpr[3]=handle;c.gpr[4]=140;c.gpr[5]=0;c.lr=caller;expected=140;variable=0;
                    Audio_WriteExpandedRaceRankVariable(&c);
                    return calls==16*256*3+1?0:5;
                }
                """);
            var sources=new[]{Path.Combine(root,"runtime/src/hle/race_sound_rank.cpp"),Path.Combine(root,"runtime/src/guest_region.cpp")};
            var args=TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false);
            var support=Path.Combine(temp,"translator_test_support.cpp");File.WriteAllText(support,File.ReadAllText(support).Replace("const uint8_t kMkwGuestRegion = 0;","const uint8_t kMkwGuestRegion = 1;",StringComparison.Ordinal));
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",args){RedirectStandardError=true})!){
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);if(!done)compile.Kill(entireProcessTree:true);
                Assert.True(done,"Race sound rank compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());}
            using var run=Process.Start(new ProcessStartInfo(exe){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);if(!finished)run.Kill(entireProcessTree:true);
            Assert.True(finished,"Race sound rank execution timed out");Assert.True(run.ExitCode==0,$"Race sound rank returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }finally{Directory.Delete(temp,recursive:true);}
    }
}
