using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class NetworkCountdownTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(24)]
    [InlineData(32)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealCountdownPathsIncludeHighPeersWithoutReadingPartialPairPadding(int players)
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
        var temp = Path.Combine(Path.GetTempPath(), "mkw-network-countdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var entries = new uint[] { 0x8064fcc8, 0x806513d0, 0x80651568, 0x80020a40, 0x80020a8c };
            var sources = new List<string>();
            foreach (var pc in entries)
            {
                var path = Path.Combine(temp, $"{pc:X8}.cpp");
                File.WriteAllText(path, translator.Translate(pc, new TranslationOptions(EnableLeafInlining: false,
                    KnownFunctionEntryPoints: map.Addresses.ToHashSet())).CxxCode);
                sources.Add(path);
            }
            var declarations = string.Join("\n", entries.Select(pc => $"extern \"C\" void func_{pc:X8}(CpuContext*);"));
            var registrations = string.Join("\n", entries.Select(pc => $"register_entry(0x{pc:X8},func_{pc:X8});"));
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
                {{declarations}}
                constexpr uint32_t capacity={{players}},size={{layout.Symbol("RKNetPacketMgr.size")}},
                    controllerSize={{layout.Symbol("RKNetController.size")}},
                    subs={{layout.Symbol("RKNetController.subs.offset")}},subStride={{layout.Symbol("RKNetSub.size")}},
                    currentSub={{layout.Symbol("RKNetController.currentSub.offset")}},
                    receive={{layout.Symbol("RKNetController.splitReceived.offset")}},rowStride={{layout.Symbol("RKNetPointerRow.size")}},
                    indices={{layout.Symbol("RKNetController.receiveIndices.offset")}},indexStride={{layout.Symbol("RKNetReceivedIndices.size")}},
                    root=0x81010000,controller=0x81030000,racedata=0x81110000,raceinfo=0x81140000,stack=0x81f00000;
                void noop(CpuContext*){}
                void yes(CpuContext* c){c->gpr[3]=1;}
                void no(CpuContext* c){c->gpr[3]=0;}
                void register_entry(uint32_t address,void(*entry)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="original countdown path";
                    info.rawCpuInvoker=entry;info.entryPoint=reinterpret_cast<void*>(entry);TranslatedFunctionRegistry::Register(info);}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);
                    FILE* input=std::fopen(argv[1],"rb");if(!input)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},input);
                    std::fclose(input);if(loaded!={{image.Memory.Length}})return 3;
                    {{registrations}}
                    for(uint32_t pc : {0x80650880u,0x80651018u,0x80657114u,0x8065d270u,0x80650678u})register_entry(pc,noop);
                    register_entry(0x8064fbb0,yes);register_entry(0x805316e8,no);
                    Memory::Write32(0x809bd918,controller);Memory::Write32(0x809b8f68,racedata);Memory::Write32(0x809b8f70,raceinfo);
                    Memory::Write32(0x80381c80,0x81100000);Memory::Write32(0x8110007c,0x3f800000);
                    Memory::Write8(racedata+{{layout.Symbol("Racedata.race.offset")}}+4,0);
                    CpuContext ctx{};ctx.gpr[1]=stack;ctx.gpr[2]=0x8038ac20;ctx.gpr[13]=0x80388880;CpuContextScope scope(&ctx);
                    for(unsigned r=14;r<32;r++)ctx.gpr[r]=0x12340000+r;
                    for(unsigned kind=0;kind<3;kind++)for(unsigned current=0;current<2;current++)
                        for(unsigned buffer=0;buffer<2;buffer++)for(unsigned local : {0u,capacity-1})for(unsigned shortTimer : {0u,3u,150u}){
                        std::memset(Memory::GetPointer(controller-16,controllerSize+32),0,controllerSize+32);
                        Memory::Write32(controller+currentSub,current);
                        auto sub=controller+subs+current*subStride;
                        // Poison membership outside the declared field. The final
                        // unrolled lane must be rejected before chasing its packet.
                        Memory::Write32(sub+16,0xffffffff);Memory::Write8(sub+33,local);
                        unsigned remote=local?0:capacity-1;
                        for(unsigned aid=0;aid<=capacity;aid++){
                            auto row=0x81200000+aid*256,holder=row+64,payload=row+128;
                            Memory::Write32(controller+receive+buffer*rowStride+aid*4,row);
                            Memory::Write32(controller+indices+aid*indexStride+4,buffer);
                            Memory::Write32(row+4,holder);Memory::Write32(holder,payload);Memory::Write32(holder+8,24);
                            Memory::Write16(payload+18,aid==capacity?0:aid==remote?shortTimer:200);
                        }
                        std::array<uint8_t,controllerSize+32> controllerBefore{};
                        std::memcpy(controllerBefore.data(),Memory::GetPointer(controller-16,controllerBefore.size()),controllerBefore.size());
                        std::memset(Memory::GetPointer(root-16,size+32),0x55,size+32);
                        auto duration=root+(kind==0?18:0);Memory::Write16(duration,100);
                        std::array<uint8_t,size+32> expected{};
                        std::memcpy(expected.data(),Memory::GetPointer(root-16,expected.size()),expected.size());
                        unsigned value=shortTimer<(kind==2?100u:99u)?shortTimer:(kind==2?100u:99u);
                        expected[16+duration-root]=value>>8;expected[17+duration-root]=value;
                        ctx.gpr[3]=root;
                        if(kind==0)func_8064FCC8(&ctx);else if(kind==1)func_806513D0(&ctx);else func_80651568(&ctx);
                        if(std::memcmp(expected.data(),Memory::GetPointer(root-16,expected.size()),expected.size())){
                            std::fprintf(stderr,"countdown n=%u kind=%u sub=%u buffer=%u local=%u remote=%u input=%u got=%u expected=%u\n",
                                capacity,kind,current,buffer,local,remote,shortTimer,Memory::Read16(duration),value);return 4;}
                        if(std::memcmp(controllerBefore.data(),Memory::GetPointer(controller-16,controllerBefore.size()),controllerBefore.size()))return 5;
                        for(unsigned r=14;r<32;r++)if(ctx.gpr[r]!=0x12340000+r)return 6;
                        if(ctx.gpr[1]!=stack)return 7;
                    }
                }
                """);
            using (var compile = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX") ?? "clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root, temp, sources, harness, exe, includeDataSections: false)) { RedirectStandardError = true })!)
            {
                var errors = compile.StandardError.ReadToEndAsync();
                var done = compile.WaitForExit(60000);
                if (!done) compile.Kill(entireProcessTree: true);
                Assert.True(done, "Countdown compilation timed out");
                Assert.True(compile.ExitCode == 0, errors.GetAwaiter().GetResult());
            }
            using var run = Process.Start(new ProcessStartInfo(exe, binary) { UseShellExecute = false, RedirectStandardError = true })!;
            var output = run.StandardError.ReadToEndAsync();
            var completed = run.WaitForExit(30000);
            if (!completed) run.Kill(entireProcessTree: true);
            Assert.True(completed, "Countdown execution timed out");
            Assert.True(run.ExitCode == 0, $"Countdown returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally { Directory.Delete(temp, recursive: true); }
    }
}
