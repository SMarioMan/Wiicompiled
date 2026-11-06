using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class CoinCodecCapacityTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealParticipantCodecsRoundTripEveryIdCountAndFollowingField(int players)
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
        var temp=Path.Combine(Path.GetTempPath(),"mkw-coin-codecs-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var entries=new uint[]{0x80871000,0x8086fe7c,0x805286f8,0x80528830};
            var sources=new List<string>();
            foreach(var pc in entries){var path=Path.Combine(temp,$"{pc:X8}.cpp");
                File.WriteAllText(path,translator.Translate(pc,new TranslationOptions(KnownFunctionEntryPoints:map.Addresses.ToHashSet())).CxxCode);sources.Add(path);}
            var declarations=string.Join("\n",entries.Select(pc=>$"extern \"C\" void func_{pc:X8}(CpuContext*);"));
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <array>
                #include <vector>
                #include <cstdio>
                #include <cstring>
                {{declarations}}
                constexpr uint32_t capacity={{players}},bits={{layout.Symbol("bits(players)")}},
                    valueBuffer=0x81090000,packet=0x810a0000,stack=0x81f00000;
                struct Field {uint32_t value,width,bytes;};
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    CpuContext ctx{};ctx.gpr[1]=stack;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                    for(uint32_t r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                    ctx.cr=0xabcdef12;ctx.xer=0xe000007f;ctx.lr=0x80012340;ctx.ctr=0x80056780;
                    func_80871000(&ctx);if(ctx.gpr[3]!=bits)return 4;
                    func_8086FE7C(&ctx);if(ctx.gpr[3]!=bits || ctx.cr!=0xabcdef12 || ctx.xer!=0xe000007f || ctx.lr!=0x80012340 || ctx.ctr!=0x80056780)return 5;
                    for(uint32_t active=1;active<=capacity;active++)for(uint32_t start:{0u,3u,7u}){
                        std::vector<Field> fields;fields.push_back({active-1,bits,1});
                        for(uint32_t i=0;i<active;i++)fields.push_back({active-1-i,bits,1});
                        for(uint32_t i=0;i<active;i++)fields.push_back({i%3==0,1,1});
                        fields.push_back({0x12345678,32,4});fields.push_back({0xa5,8,1});
                        std::memset(Memory::GetPointer(packet-16,544),0xaa,544);
                        std::array<uint8_t,512> expected{};expected.fill(0xaa);uint32_t position=start;
                        for(const auto& f:fields){
                            if(f.bytes==1)Memory::Write8(valueBuffer,f.value);else Memory::Write32(valueBuffer,f.value);
                            ctx.gpr[3]=valueBuffer;ctx.gpr[4]=f.width;ctx.gpr[5]=packet;ctx.gpr[6]=position;ctx.gpr[7]=f.bytes;
                            func_805286F8(&ctx);
                            for(uint32_t bit=0;bit<f.width;bit++){
                                auto p=position+bit,mask=1u<<(7-(p%8));
                                expected[p/8]=(expected[p/8]&~mask)|(((f.value>>(f.width-bit-1))&1)?mask:0);
                            }
                            position+=f.width;if(ctx.gpr[3]!=position || ctx.gpr[1]!=stack)return 6;
                        }
                        if(std::memcmp(expected.data(),Memory::GetPointer(packet,512),512))return 7;
                        position=start;
                        for(const auto& f:fields){
                            std::memset(Memory::GetPointer(valueBuffer-4,12),0x55,12);
                            ctx.gpr[3]=valueBuffer;ctx.gpr[4]=f.width;ctx.gpr[5]=packet;ctx.gpr[6]=position;ctx.gpr[7]=f.bytes;
                            func_80528830(&ctx);position+=f.width;
                            auto actual=f.bytes==1?Memory::Read8(valueBuffer):Memory::Read32(valueBuffer);
                            if(actual!=f.value || ctx.gpr[3]!=position || ctx.gpr[1]!=stack)return 8;
                            for(uint32_t i=0;i<4;i++)if(Memory::Read8(valueBuffer-4+i)!=0x55 || Memory::Read8(valueBuffer+f.bytes+i)!=0x55)return 9;
                        }
                        for(uint32_t i=0;i<16;i++)if(Memory::Read8(packet-16+i)!=0xaa || Memory::Read8(packet+512+i)!=0xaa)return 10;
                        for(uint32_t r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r)return 11;
                    }
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)){RedirectStandardError=true})!)
            {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Coin codec compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe,binary){UseShellExecute=false,RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(10000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Coin codec execution timed out");
            Assert.True(run.ExitCode==0,$"Coin codec returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
