using System.Diagnostics;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class GuestWordArrayNativeTests
{
    [Fact]
    [Trait("Requires","Clang")]
    public void CompiledWordComparisonsAndTableLoadsPreserveRegistersFlagsAndFallbackReads()
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var types=new[]{new GuestLayoutType("Word",4,4),new GuestLayoutType("Head",4,80),new GuestLayoutType("Words",4,Fields:new[]{
            new GuestLayoutField("head","Head"),new GuestLayoutField("items","Word",PerPlayer:true,PlayersPerElement:12)})};
        var temp=Path.Combine(Path.GetTempPath(),"mkw-word-arrays-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var sources=new List<string>();var declarations=new List<string>();var cases=new List<string>();
            foreach(var players in new[]{12,13,24,48,100})foreach(var kind in new[]{0,1,2,3,4,5})
            {
                var name=$"word_array_{players}_{kind}";uint start=0x80001000u+(uint)cases.Count*0x100u;const uint table=0x80003000;
                uint[] words=kind==0?new uint[]{0x80830050,0x80a60050,0x7c042840}:
                    kind==1?new uint[]{0x80830050,0x2c040000}:kind==2?new uint[]{0x80830050,0x28040000}:
                    kind==3?new uint[]{0x80830000}:kind==4?new uint[]{0x7c83002e}:new uint[]{0x7c8903a6};
                var image=TranslatorCppTestHarness.CreateImage(words.Append(0x4e800020u).Select((w,i)=>(start+(uint)i*4,w))
                    .Concat(new[]{(table,16u),(table+4,4u),(table+8,56u)}).ToArray());
                GuestLayoutRelocations plan=kind<3?new(types,12,players,Array.Empty<GuestLayoutReference>(),wordComparisons:new[]{
                    new GuestWordComparison(start,words,"r3","Words.items.offset","r4","Words.items.count",IsUnsigned:kind!=1,
                        RightLoad:kind==0?start+4:null,RightBase:kind==0?"r6":null,RightOffset:kind==0?"Words.items.offset":null,RightValue:kind==0?"r5":null)})
                    :kind==5?new(types,12,players,Array.Empty<GuestLayoutReference>(),countConversions:new[]{new GuestCountConversion(start,words[0],"r4","ceil(players/6)")}):new(types,12,players,Array.Empty<GuestLayoutReference>(),wordTables:new[]{
                        new GuestWordTable(table,new uint[]{16,4,56},new[]{new GuestWordTableEntry(1,"Words.items.size")},
                            new[]{new GuestWordTableLoad(start,words[0],"r3","r4",Index:kind==4?"r0":null)})});
                var source=Path.Combine(temp,name+".cpp");File.WriteAllText(source,new FunctionTranslator(image,layoutRelocations:plan).Translate(start,new TranslationOptions(PreferredName:name)).CxxCode);sources.Add(source);
                declarations.Add($"extern \"C\" void {name}(CpuContext*);");cases.Add($"if(!check({name},{players},{kind}))return {1+cases.Count};");
            }
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <array>
                {{string.Join("\n",declarations)}}
                bool check(void(*fn)(CpuContext*),uint32_t capacity,uint32_t kind){
                    CpuContext c{};CpuContextScope scope(&c);uint32_t words=(capacity+11)/12;
                    for(uint32_t word=0;word<words;word++)for(uint32_t value:std::array{0u,1u,0x80000000u,0xffffffffu}){
                        for(uint32_t r=0;r<32;r++)c.gpr[r]=0x12340000+r;
                        c.gpr[3]=0x81010000;c.gpr[6]=0x81011000;c.cr=0x23456789;c.xer=0x80000000;c.lr=0x80006000;c.ctr=0x80008000;
                        for(uint32_t i=0;i<words;i++){Memory::Write32(0x81010050+i*4,i==word?value:0);Memory::Write32(0x81011050+i*4,0);}
                        auto before=c;
                        if(kind<3){
                            fn(&c);auto nibble=value==0?3u:(kind==1 && int32_t(value)<0?9u:5u);
                            if(c.cr!=((before.cr&0x0fffffffu)|(nibble<<28)) || c.xer!=before.xer || c.lr!=before.lr || c.ctr!=before.ctr)return false;
                            if(c.gpr[4]!=(word==0?value:0) || (kind==0 && c.gpr[5]))return false;
                            for(uint32_t r=0;r<32;r++)if(r!=4 && !(kind==0 && r==5) && c.gpr[r]!=before.gpr[r])return false;
                        }
                    }
                    if(kind>=3 && kind<5){
                        for(uint32_t at:std::array{0x80003000u,0x80003004u,0x80003008u,0x80004000u,0x80003001u}){
                            Memory::Write32(0x80003000,16);Memory::Write32(0x80003004,4);Memory::Write32(0x80003008,56);Memory::Write32(0x80004000,0xabcdef01);
                            for(uint32_t r=0;r<32;r++)c.gpr[r]=0x12340000+r;
                            c.gpr[3]=kind==3?at:0x80003000;c.gpr[0]=at-0x80003000;c.cr=0x23456789;c.xer=0x80000000;c.lr=0x80006000;c.ctr=0x80008000;
                            auto before=c;auto expected=at==0x80003004?words*4:Memory::Read32(at);
                            fn(&c);if(c.gpr[4]!=expected || c.cr!=before.cr || c.xer!=before.xer || c.lr!=before.lr || c.ctr!=before.ctr)return false;
                            for(uint32_t r=0;r<32;r++)if(r!=4 && c.gpr[r]!=before.gpr[r])return false;
                        }
                    }
                    if(kind==5)for(uint32_t count:std::array{0u,1u,2u,3u,0xffffffffu}){
                        for(uint32_t r=0;r<32;r++)c.gpr[r]=0x12340000+r;
                        c.gpr[4]=count;c.cr=0x23456789;c.xer=0x80000000;c.lr=0x80006000;c.ctr=0x80008000;auto before=c;
                        fn(&c);if(c.ctr!=(count==2?(capacity+5)/6:count) || c.cr!=before.cr || c.xer!=before.xer || c.lr!=before.lr)return false;
                        for(uint32_t r=0;r<32;r++)if(c.gpr[r]!=before.gpr[r])return false;
                    }
                    return true;
                }
                int main(){Memory::Init(32*1024*1024);
                    {{string.Join("\n",cases)}}
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!){
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(180000);if(!done)compile.Kill(entireProcessTree:true);
                Assert.True(done,"Word-array compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());}
            using var run=Process.Start(new ProcessStartInfo(exe){RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);if(!finished)run.Kill(entireProcessTree:true);
            Assert.True(finished,"Word-array execution timed out");Assert.True(run.ExitCode==0,$"Word-array case {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
