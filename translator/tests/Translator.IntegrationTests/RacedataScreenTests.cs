using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class RacedataScreenTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealScreenAssignmentIncludesHighLocalPlayersAndBoundsSpectatorFallback(int players)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "translator/Translator.sln"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var manifest = Path.Combine(root, "projects/mkwii-ntsc-u/layouts/racedata.json");
        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        var layout = new GuestLayouts(document.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!, players);
        var plan = GuestLayoutRelocations.Read(manifest, players);
        var image = new ProgramImageBuilder().Build(Path.Combine(root, "Assets/main.dol"), Path.Combine(root, "Assets/StaticR.rel"), 0x8050bf60);
        var map = FunctionMap.Load(Path.Combine(root, "projects/mkwii-ntsc-u/MAP.txt"));
        var translator = new FunctionTranslator(image, layoutRelocations: plan);
        var temp = Path.Combine(Path.GetTempPath(), "mkw-racedata-screens-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var entries = new uint[] { 0x8052a9a0, 0x8052d3d0 };
            var sources = new List<string>();
            foreach (var pc in entries)
            {
                var path = Path.Combine(temp, $"{pc:X8}.cpp");
                File.WriteAllText(path, translator.Translate(pc, new TranslationOptions(EnableLeafInlining: false,
                    KnownFunctionEntryPoints: map.Addresses.ToHashSet())).CxxCode);
                sources.Add(path);
            }
            var binary = Path.Combine(temp, "image.bin");
            File.WriteAllBytes(binary, image.Memory);
            var harness = Path.Combine(temp, "harness.cpp");
            var exe = Path.Combine(temp, "test");
            File.WriteAllText(harness, $$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include <array>
                #include <cstdio>
                #include <cstring>
                extern "C" void func_8052A9A0(CpuContext*);
                extern "C" void func_8052D3D0(CpuContext*);
                constexpr uint32_t capacity={{players}},size={{layout.Symbol("Scenario.size")}},extent=size+720,
                    settings={{layout.Symbol("Scenario.settings.offset")}},raceOffset={{layout.Symbol("Racedata.race.offset")}},
                    root=0x81010000,inputManager=0x81080000,device=0x81090000,vtable=0x810a0000,stack=0x81f00000;
                void controller_type(CpuContext* c){if(c->gpr[3]!=device)std::abort();c->gpr[3]=77;}
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="real screen assignment";
                    info.rawCpuInvoker=entry;info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);
                    FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    register_entry(0x8052a9a0,func_8052A9A0);register_entry(0x8052d3d0,func_8052D3D0);
                    register_entry(0x80010100,controller_type);
                    Memory::Write32(0x809b8f4c,inputManager);Memory::Write32(device,vtable);Memory::Write32(vtable+16,0x80010100);
                    for(unsigned i=0;i<4;i++)Memory::Write32(inputManager+i*0xec+8,device);
                    CpuContext ctx{};ctx.gpr[1]=stack;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                    for(unsigned r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                    for(unsigned kind=0;kind<5;kind++){
                        std::memset(Memory::GetPointer(root-16,extent+32),0x55,extent+32);
                        Memory::Write8(root+4,capacity);
                        std::array<int,capacity> expectedHud{};expectedHud.fill(-1);
                        std::array<bool,capacity> local{};
                        if(kind==0)local[capacity-1]=true;
                        if(kind==1)local[0]=local[capacity-1]=true;
                        if(kind==2)local[0]=local[capacity/3]=local[capacity*2/3]=local[capacity-1]=true;
                        for(unsigned p=0;p<capacity;p++){
                            auto at=root+8+p*240;
                            Memory::Write32(at+16,local[p]?0:(kind>=3?5:4));
                            Memory::Write8(at+5,255);Memory::Write8(at+6,255);
                        }
                        if(kind==3)Memory::Write32(root+8+(capacity-1)*240+16,4);
                        // A partial group must not turn settings/mission bytes into
                        // another participant when every real slot is inactive.
                        if(kind==4)for(unsigned p=capacity;p<(capacity+2)/3*3;p++){
                            Memory::Write32(root+8+p*240+16,4);Memory::Write8(root+8+p*240+5,255);
                        }
                        std::array<uint8_t,extent> expected{};
                        std::memcpy(expected.data(),Memory::GetPointer(root,extent),extent);
                        std::memset(expected.data()+settings+28,255,4);
                        auto word=[&](unsigned at,uint32_t value){for(unsigned b=0;b<4;b++)expected[at+b]=value>>(24-b*8);};
                        unsigned screens=0;
                        for(unsigned p=0;p<capacity;p++)if(local[p]){
                            expectedHud[p]=screens;
                            expected[8+p*240+5]=expected[8+p*240+6]=screens;
                            word(8+p*240+0xd0,77);expected[settings+28+screens]=p;screens++;
                        }
                        if(kind==3){expectedHud[capacity-1]=0;expected[8+(capacity-1)*240+5]=0;expected[settings+28]=capacity-1;}
                        ctx.gpr[3]=root;ctx.gpr[4]=screens?screens:1;func_8052A9A0(&ctx);
                        if(std::memcmp(expected.data(),Memory::GetPointer(root,extent),extent)){
                            for(unsigned at=0;at<extent;at++)if(expected[at]!=Memory::Read8(root+at)){
                                std::fprintf(stderr,"screen n=%u kind=%u offset=%u got=%u expected=%u\n",capacity,kind,at,Memory::Read8(root+at),expected[at]);break;}
                            return 4;
                        }
                        for(unsigned p=0;p<capacity;p++){
                            ctx.gpr[3]=root-raceOffset;ctx.gpr[4]=p;func_8052D3D0(&ctx);
                            if(static_cast<int32_t>(ctx.gpr[3])!=expectedHud[p])return 5;
                        }
                        for(unsigned r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r)return 6;
                        if(ctx.gpr[1]!=stack)return 7;
                        for(unsigned i=0;i<16;i++)if(Memory::Read8(root-16+i)!=0x55 || Memory::Read8(root+extent+i)!=0x55)return 8;
                    }
                }
                """);
            using (var compile = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX") ?? "clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root, temp, sources, harness, exe, includeDataSections: false)) { RedirectStandardError = true })!)
            {
                var errors = compile.StandardError.ReadToEndAsync();
                var done = compile.WaitForExit(60000);
                if (!done) compile.Kill(entireProcessTree: true);
                Assert.True(done, "Screen assignment compilation timed out");
                Assert.True(compile.ExitCode == 0, errors.GetAwaiter().GetResult());
            }
            using var run = Process.Start(new ProcessStartInfo(exe, binary) { UseShellExecute = false, RedirectStandardError = true })!;
            var output = run.StandardError.ReadToEndAsync();
            var completed = run.WaitForExit(30000);
            if (!completed) run.Kill(entireProcessTree: true);
            Assert.True(completed, "Screen assignment execution timed out");
            Assert.True(run.ExitCode == 0, $"Screen assignment returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally { Directory.Delete(temp, recursive: true); }
    }
}
