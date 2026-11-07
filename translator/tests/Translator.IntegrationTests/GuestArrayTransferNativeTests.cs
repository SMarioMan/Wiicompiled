using System.Diagnostics;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class GuestArrayTransferNativeTests
{
    [Fact]
    [Trait("Requires", "Clang")]
    public void CompiledTransfersAndPointerChainsPreserveMemoryUnderOptimization()
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var types=new[] {new GuestLayoutType("Header",4,80),new GuestLayoutType("Word",4,4),new GuestLayoutType("SourceHeader",4,8),
            new GuestLayoutType("Source",4,Fields:new[] {new GuestLayoutField("header","SourceHeader"),new GuestLayoutField("words","Word",PerPlayer:true,PlayersPerElement:12)}),
            new GuestLayoutType("Target",4,Fields:new[] {new GuestLayoutField("header","Header"),new GuestLayoutField("words","Word",PerPlayer:true,PlayersPerElement:12)}),
            new GuestLayoutType("ChainHeader",4,32),new GuestLayoutType("ChainNode",4,12),
            new GuestLayoutType("ChainFrame",4,Fields:new[] {new GuestLayoutField("header","ChainHeader"),new GuestLayoutField("nodes","ChainNode",PerPlayer:true)})};
        var words=new uint[]{0x80040008,0x901f0050};
        var image=TranslatorCppTestHarness.CreateImage(words.Append(0x4e800020u).Select((word,index)=>(0x80001000u+(uint)index*4,word)).ToArray());
        var plan=new GuestLayoutRelocations(types,12,24,Array.Empty<GuestLayoutReference>(),arrayTransfers:new[] {
            new GuestArrayTransfer(0x80001000,words,"r4","Source.words.offset","r31","Target.words.offset","r0","Word.size","Target.words.count",AllowObjectSource:true)});
        var temp=Path.Combine(Path.GetTempPath(),"mkw-array-transfer-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try{
            var source=Path.Combine(temp,"transfer.cpp");var chain=Path.Combine(temp,"chain.cpp");var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(source,new FunctionTranslator(image,layoutRelocations:plan).Translate(0x80001000,TranslationOptions.Default).CxxCode);
            var chainImage=TranslatorCppTestHarness.CreateImage((0x80001100u,0x90c100a4u),(0x80001104u,0x4e800020u));
            var chainPlan=new GuestLayoutRelocations(types,12,24,Array.Empty<GuestLayoutReference>(),arrayFills:new[] {
                new GuestArrayFill(0x80001100,new[] {0x90c100a4u},"r1","r6",new[] {new GuestFillArray("ChainFrame.nodes.offset","ChainFrame.nodes.stride","ChainFrame.nodes.count",4,StartIndex:11,LinkToNext:true)})});
            File.WriteAllText(chain,new FunctionTranslator(chainImage,layoutRelocations:chainPlan).Translate(0x80001100,TranslationOptions.Default).CxxCode);
            File.WriteAllText(harness,"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                extern "C" void func_80001000(CpuContext*);
                extern "C" void func_80001100(CpuContext*);
                int main(){
                    Memory::Init(32*1024*1024);CpuContext context{};CpuContextScope scope(&context);
                    context.gpr[1]=0x81f00000;context.gpr[4]=0x81001000;context.gpr[31]=0x81000fbc;
                    Memory::Write32(0x81001004,0xfeedbeef);Memory::Write32(0x81001008,0x12345678);
                    Memory::Write32(0x8100100c,0xabcdef01);Memory::Write32(0x81001010,0x55555555);Memory::Write32(0x81001014,0xcafebabe);
                    func_80001000(&context);
                    if(Memory::Read32(0x81001004)!=0xfeedbeef || Memory::Read32(0x81001008)!=0x12345678 ||
                        Memory::Read32(0x8100100c)!=0x12345678 || Memory::Read32(0x81001010)!=0xabcdef01 ||
                        Memory::Read32(0x81001014)!=0xcafebabe)return 1;
                    if(context.gpr[0]!=0x12345678 || context.gpr[4]!=0x81001000 || context.gpr[31]!=0x81000fbc)return 2;
                    context.gpr[1]=0x81002000;context.gpr[6]=0;
                    Memory::Write32(0x8100201c,0xdeadbeef);Memory::Write32(0x81002140,0xcafebabe);
                    for(uint32_t i=0;i<24;i++){
                        const uint32_t at=0x81002020+i*12;
                        Memory::Write32(at,i<11?at+12:0x55555555);Memory::Write32(at+4,0xaabbccdd);Memory::Write32(at+8,0x11223344);
                    }
                    func_80001100(&context);
                    for(uint32_t i=0;i<24;i++){
                        const uint32_t at=0x81002020+i*12;
                        if(Memory::Read32(at)!=(i<23?at+12:0) || Memory::Read32(at+4)!=0xaabbccdd || Memory::Read32(at+8)!=0x11223344)return 3;
                    }
                    if(Memory::Read32(0x8100201c)!=0xdeadbeef || Memory::Read32(0x81002140)!=0xcafebabe || context.gpr[1]!=0x81002000 || context.gpr[6])return 4;
                    return 0;
                }
                """);
            // Exercise optimizer memory folding too, not just the lifted IR.
            var arguments=TranslatorCppTestHarness.BuildCompileArguments(root,temp,new[] {source,chain},harness,exe,includeDataSections:false)+" -O2";
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",arguments){RedirectStandardError=true})!){
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);if(!done)compile.Kill(entireProcessTree:true);
                Assert.True(done,"Array transfer compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);if(!finished)run.Kill(entireProcessTree:true);
            Assert.True(finished,"Array transfer execution timed out");Assert.True(run.ExitCode==0,$"Array transfer returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }finally{Directory.Delete(temp,recursive:true);}
    }
}
