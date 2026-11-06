using System.Diagnostics;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class AuthoredCountIndexNativeTests
{
    [Theory]
    [InlineData(12,false)] [InlineData(13,false)] [InlineData(24,false)]
    [InlineData(24,true)] [InlineData(48,false)] [InlineData(100,false)]
    [Trait("Requires","Clang")]
    public void EmittedCountIndicesPreserveGuestRegistersAndFlags(int players,bool alias)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        uint word=alias?0x3b9cfffeu:0x381cfffeu;
        var image=TranslatorCppTestHarness.CreateImage((0x80001000,word),(0x80001004,0x4e800020));
        var plan=new GuestLayoutRelocations(new[]{new GuestLayoutType("Byte",1,1)},12,players,Array.Empty<GuestLayoutReference>(),
            authoredCountIndices:new[]{new GuestAuthoredCountIndex(0x80001000,word,"r28",alias?"r28":"r0",2,12)});
        var temp=Path.Combine(Path.GetTempPath(),"mkw-authored-count-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var source=Path.Combine(temp,"source.cpp");var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(source,new FunctionTranslator(image,layoutRelocations:plan).Translate(0x80001000).CxxCode);
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <algorithm>
                #include <array>
                extern "C" void func_80001000(CpuContext*);
                int main(){
                    Memory::Init(32*1024*1024);CpuContext ctx{};CpuContextScope scope(&ctx);
                    auto check=[&](uint32_t input){
                        for(uint32_t r=0;r<32;r++)ctx.gpr[r]=0x12340000+r;
                        ctx.gpr[28]=input;ctx.cr=0xa5a55a5a;ctx.xer=0xe000007f;ctx.lr=0x80003000;ctx.ctr=0x80004000;
                        auto before=ctx;func_80001000(&ctx);
                        auto expected={{players}}==12?input-2:std::clamp(input,2u,12u)-2;
                        constexpr uint32_t destination={{(alias?28:0)}};
                        if(ctx.gpr[destination]!=expected || ctx.cr!=before.cr || ctx.xer!=before.xer || ctx.lr!=before.lr || ctx.ctr!=before.ctr)return false;
                        for(uint32_t r=0;r<32;r++)if(r!=destination && ctx.gpr[r]!=before.gpr[r])return false;
                        return true;
                    };
                    for(uint32_t input=0;input<{{players+3}};input++)if(!check(input))return 1;
                    for(auto input:std::array{0x7fffffffu,0x80000000u,0xffffffffu})if(!check(input))return 2;
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,new[]{source},harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Count-index compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Count-index execution timed out");
            Assert.True(run.ExitCode==0,$"Count index returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
