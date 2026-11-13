using System.Diagnostics;
using System.Globalization;
using Translator.Core.Analysis.Ssa;
using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class BulletBillTargetTests
{
    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(48)] [InlineData(100)]
    [Trait("Requires", "USA game assets and C++ compiler")]
    public void RealActivationTargetsEveryActiveRankAndRetainsDurationAndSafeDropGates(int players)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "translator/Translator.sln"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var image = new ProgramImageBuilder().Build(Path.Combine(root, "Assets/main.dol"), Path.Combine(root, "Assets/StaticR.rel"), 0x8050bf60);
        var plan = GuestLayoutRelocations.Read(Path.Combine(root, "projects/mkwii-ntsc-u/layouts/racedata.json"), players);
        var translator = new FunctionTranslator(image, layoutRelocations: plan);
        var map = FunctionMap.Load(Path.Combine(root, "projects/mkwii-ntsc-u/MAP.txt"));
        var options = new TranslationOptions(EnableLeafInlining: false, KnownFunctionEntryPoints: map.Addresses.ToHashSet());
        if (players == 12)
            Assert.Equal(new FunctionTranslator(image).Translate(0x805b07a0, options).CxxCode, translator.Translate(0x805b07a0, options).CxxCode);

        // Execute the real table copy, count lookup, rank subtraction and clamp.
        // Earlier activation graphics/physics and later audio effects are outside this region.
        const uint begin = 0x805b08f8, end = 0x805b09b0;
        var discovery = translator.Discover(begin, options);
        var blocks = new List<IrBasicBlock>();
        foreach (var block in discovery.LinearIr.Blocks)
        {
            var pc = uint.Parse(block.Label.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (pc > end) continue;
            var instructions = new List<IrInstruction>();
            foreach (var instruction in block.Instructions)
            {
                instructions.Add(instruction);
                if (instruction is IrTracePpc { Address: end }) { instructions.Add(new IrReturn(null)); break; }
            }
            blocks.Add(block with { Instructions = instructions });
        }
        Assert.Contains(blocks.SelectMany(b => b.Instructions).OfType<IrTracePpc>(), t => t.Address == end);
        var region = discovery.LinearIr with { Name = "bullet_target", Blocks = blocks };
        var target = translator.LowerCanonical(begin, new SsaTransformer().Convert(region).Function, options with { PreferredName = "bullet_target" });
        var temp = Path.Combine(Path.GetTempPath(), "mkw-bullet-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var sources = new List<string>();
            var source = Path.Combine(temp, "target.cpp"); File.WriteAllText(source, target.CxxCode); sources.Add(source);
            foreach (var pc in new uint[] { 0x805b1c48, 0x80020a3c, 0x80020a88 })
            {
                source = Path.Combine(temp, $"{pc:X8}.cpp"); File.WriteAllText(source, translator.Translate(pc, options).CxxCode); sources.Add(source);
            }
            var binary = Path.Combine(temp, "image.bin"); File.WriteAllBytes(binary, image.Memory);
            var harness = Path.Combine(temp, "harness.cpp"); var exe = Path.Combine(temp, "test");
            File.WriteAllText(harness, $$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <algorithm>
                #include <array>
                #include <cstdio>
                #include <cstring>
                extern "C" void bullet_target(CpuContext*);
                extern "C" void func_805B1C48(CpuContext*);
                extern "C" void func_80020A3C(CpuContext*);
                extern "C" void func_80020A88(CpuContext*);
                constexpr uint32_t capacity={{players}},self=0x81010000,info=0x81020000,pointers=0x81021000,
                    racer=0x81022000,countOwner=0x81030000,kartLink=0x81040000,status=0x81041000,
                    kartManager=0x81050000,drivers=0x81051000,driver=0x81052000,stack=0x81f00000;
                uint32_t playerId=0,idCalls=0;
                void id(CpuContext* c){if(c->gpr[3]!=self)std::abort();c->gpr[3]=playerId;idCalls++;}
                void entry(uint32_t address,void(*fn)(CpuContext*)){
                    TranslatedFunctionInfo i{};i.address=address;i.name="bullet fixture";i.rawCpuInvoker=fn;
                    i.entryPoint=reinterpret_cast<void*>(fn);TranslatedFunctionRegistry::Register(i);}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);
                    auto f=std::fopen(argv[1],"rb");if(!f)return 2;
                    auto size=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},f);
                    std::fclose(f);if(size!={{image.Memory.Length}})return 3;
                    entry(0x8058a238,id);entry(0x80020a3c,func_80020A3C);entry(0x80020a88,func_80020A88);
                    Memory::Write32(0x809bd110,countOwner);Memory::Write32(0x809b8f70,info);Memory::Write32(info+12,pointers);
                    Memory::Write32(0x809bda10,kartManager);Memory::Write32(kartManager+0x68,drivers);
                    Memory::Write32(self,kartLink);Memory::Write32(kartLink+4,status);
                    CpuContext c{};CpuContextScope scope(&c);
                    for(uint32_t count=1;count<=capacity;count++)for(uint32_t rank=1;rank<=count;rank++){
                        playerId=count-1;Memory::Write32(pointers+playerId*4,racer);Memory::Write32(drivers+playerId*4,driver);
                        Memory::Write8(countOwner+0x24,count);Memory::Write8(racer+0x20,rank);
                        // Trash past the original table must never affect an expanded target.
                        std::memset(Memory::GetPointer(stack,0x100),0x7f,0x100);
                        std::memset(Memory::GetPointer(self+0x14,2),0,2);
                        c.gpr[1]=stack;c.gpr[29]=self;c.gpr[31]=0x8088e150;idCalls=0;
                        bullet_target(&c);
                        const uint32_t overtakes=std::max(1u,(count-1)/2),target=rank>overtakes?rank-overtakes:1;
                        if(c.gpr[4]!=overtakes || Memory::Read16(self+0x14)!=target || idCalls!=1)return 4;
                        for(uint32_t b=0;b<0x100;b++)if((b<0x20 || b>=0x38) && Memory::Read8(stack+b)!=0x7f)return 5;
                        if(c.gpr[1]!=stack || c.gpr[29]!=self || c.gpr[31]!=0x8088e150)return 6;
                        const uint32_t minimum=Memory::Read16(0x808b29d2),maximum=Memory::Read16(0x808b29d0);
                        for(uint32_t atTarget=0;atTarget<2;atTarget++)for(uint32_t unsafe=0;unsafe<2;unsafe++)
                            for(uint32_t protectedMode=0;protectedMode<2;protectedMode++)for(auto timer:std::array{minimum,minimum+1,maximum,maximum+1}){
                                Memory::Write8(racer+0x20,atTarget?target:std::max(target+1,2u));
                                Memory::Write16(self+0x12,timer);Memory::Write8(self+0x30,0);Memory::Write32(self+0x44,protectedMode?8:0);
                                Memory::Write32(status+8,unsafe?16:0);Memory::Write16(driver+0x99c,0x1234);
                                for(uint32_t r=14;r<32;r++)c.gpr[r]=0x12340000+r;
                                c.gpr[1]=stack;c.gpr[2]=0x8038ac20;c.gpr[13]=0x80388880;c.gpr[3]=self;
                                func_805B1C48(&c);
                                const bool drop=(timer>maximum || (timer>minimum && atTarget)) && !unsafe && !protectedMode;
                                if(Memory::Read8(self+0x30)!=drop || Memory::Read16(driver+0x99c)!=(drop?0:0x1234))return 7;
                                if(c.gpr[1]!=stack)return 8;
                                for(uint32_t r=14;r<32;r++)if(c.gpr[r]!=0x12340000+r)return 9;
                            }
                    }
                    return 0;
                }
                """);
            var arguments = TranslatorCppTestHarness.BuildCompileArguments(root, temp, sources, harness, exe, includeDataSections: false);
            using (var compile = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX") ?? "clang++", arguments) { RedirectStandardError = true })!)
            {
                var errors = compile.StandardError.ReadToEndAsync(); var done = compile.WaitForExit(120000);
                if (!done) compile.Kill(entireProcessTree: true);
                Assert.True(done, "Bullet regression compilation timed out"); Assert.True(compile.ExitCode == 0, errors.GetAwaiter().GetResult());
            }
            using var run = Process.Start(new ProcessStartInfo(exe, binary) { RedirectStandardError = true })!;
            var output = run.StandardError.ReadToEndAsync(); var finished = run.WaitForExit(20000);
            if (!finished) run.Kill(entireProcessTree: true);
            Assert.True(finished, "Bullet regression execution timed out"); Assert.True(run.ExitCode == 0, $"Bullet regression returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally { Directory.Delete(temp, recursive: true); }
    }
}
