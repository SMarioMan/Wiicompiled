using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class TeamResultLoadTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(25)]
    [InlineData(128)]
    [Trait("Requires", "USA game assets and Clang")]
    public void OriginalTeamLoaderAndAnimationsHandleEveryTeamRow(int players)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "translator/Translator.sln"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException();
        var manifest = Path.Combine(root, "projects/mkwii-ntsc-u/layouts/racedata.json");
        using var definition = JsonDocument.Parse(File.ReadAllText(manifest));
        var layout = new GuestLayouts(definition.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!, players);
        var image = new ProgramImageBuilder().Build(Path.Combine(root, "Assets/main.dol"), Path.Combine(root, "Assets/StaticR.rel"), 0x8050bf60);
        var map = FunctionMap.Load(Path.Combine(root, "projects/mkwii-ntsc-u/MAP.txt"));
        var translator = new FunctionTranslator(image, layoutRelocations: GuestLayoutRelocations.Read(manifest, players));
        var temp = Path.Combine(Path.GetTempPath(), "mkw-team-load-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var options = new TranslationOptions(EnableLeafInlining: false, KnownFunctionEntryPoints: map.Addresses.ToHashSet());
            var sources = new List<string>();
            uint[] entries = { 0x807ebef4, 0x80020a30, 0x80020a7c, 0x807ec990, 0x807ecbfc, 0x80020a38, 0x80020a84 };
            foreach (var address in entries)
            {
                var source = Path.Combine(temp, $"{address:X8}.cpp");
                File.WriteAllText(source, translator.Translate(address, options).CxxCode);
                sources.Add(source);
            }
            sources.Add(Path.Combine(root, "runtime/src/hle/team_results.cpp"));
            sources.Add(Path.Combine(root, "runtime/src/guest_region.cpp"));
            var imageFile = Path.Combine(temp, "image.bin");
            File.WriteAllBytes(imageFile, image.Memory);
            var fixture = Path.Combine(temp, "harness.cpp");
            var exe = Path.Combine(temp, "test");
            File.WriteAllText(fixture, $$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include "mkw_guest_layout.h"
                #include <cstdio>
                #include <cstring>
                #include <string>
                #include <vector>
                {{string.Join("\n", entries.Select(pc => $"extern \"C\" void func_{pc:X8}(CpuContext*);"))}}
                extern "C" void TeamResult_UpdateScoreAnimation(CpuContext*);
                extern "C" void TeamResult_IsAnimating(CpuContext*);
                constexpr unsigned rows={{layout.Symbol("TeamResult.items.count")}},
                    size={{layout.Symbol("TeamResult.size")}}, teamOffset={{layout.Symbol("TeamResult.team.offset")}},
                    pointTotal={{layout.Symbol("TeamResult.resultTeamPoint.offset")}},
                    items={{layout.Symbol("TeamResult.items.offset")}}, itemStride={{layout.Symbol("TeamResult.items.stride")}},
                    points={{layout.Symbol("TeamResult.addPoints.offset")}}, scores={{layout.Symbol("TeamResult.players.offset")}},
                    scoreStride={{layout.Symbol("TeamResult.players.stride")}},
                    teamScore={{layout.Symbol("TeamResult.teamScore.offset")}}, currentScore={{layout.Symbol("TeamResult.currentScore.offset")}};
                namespace MkwGuestLayout { const TeamResultLayout kTeamResults{scores,rows,scoreStride,pointTotal,currentScore,teamScore}; }
                constexpr uint32_t result=0x81010000, group=0x81020000, page=0x81021000,
                    table=0x81022000, heap=0x81100000, stack=0x81f00000;
                unsigned team, reserved, loadedRows, loadedPoints;
                uint32_t cursor;
                std::vector<uint32_t> children;
                #define CHECK(condition) do { if(!(condition)){std::fprintf(stderr,"team loader line %d: %s\n",__LINE__,#condition);std::abort();} } while(false)
                const char* text(uint32_t addr){return reinterpret_cast<const char*>(Memory::GetPointer(addr));}
                void format(CpuContext* c){
                    auto buffer=c->gpr[3], bytes=c->gpr[4];
                    std::string pattern=text(c->gpr[5]);
                    CHECK(pattern=="ResultVSTeamNULL%d" || pattern=="BlueRed%d");
                    c->gpr[3]=std::snprintf(reinterpret_cast<char*>(Memory::GetPointer(buffer,bytes)),bytes,pattern.c_str(),int(c->gpr[6]));
                }
                void attach_loader(CpuContext* c){Memory::Write32(c->gpr[3],c->gpr[4]);}
                void load_control(CpuContext* c){
                    auto control=Memory::Read32(c->gpr[3]);
                    if(control==result){CHECK(std::string(text(c->gpr[6]))=="ResultVSTeamNULL"+std::to_string(team));return;}
                    if(control==Memory::Read32(result+pointTotal))return;
                    CHECK(control==result+items+loadedRows*itemStride);
                    CHECK(std::string(text(c->gpr[6]))=="BlueRed"+std::to_string(loadedRows));
                    // This retail table has twelve entries even when rows expand.
                    for(unsigned i=0;i<12;i++) CHECK(Memory::Read32(c->gpr[7]+i*4)==Memory::Read32(0x808a4c20+i*4));
                    ++loadedRows;
                }
                void mode(CpuContext* c){CHECK(c->gpr[3]==page);c->gpr[3]=0x31;}
                void reserve(CpuContext* c){CHECK(c->gpr[3]==result && !reserved && c->gpr[4]==2*rows+1);reserved=c->gpr[4];}
                void add(CpuContext* c){CHECK(c->gpr[3]==result && c->gpr[4]==children.size());children.push_back(c->gpr[5]);}
                void allocate(CpuContext* c){
                    auto bytes=c->gpr[3];CHECK(bytes==0x174 || bytes==0x1a0);
                    c->gpr[3]=cursor;cursor+=0x200;
                }
                void animation(CpuContext* c){c->gpr[3]=heap+0x10000;}
                void no_effect(CpuContext*){}
                void load_points(CpuContext* c){
                    CHECK(c->gpr[4]==team && c->gpr[5]==loadedPoints && loadedPoints<rows);
                    CHECK(c->gpr[3]==Memory::Read32(result+points+4*loadedPoints));
                    ++loadedPoints;
                }
                void entry(uint32_t address,void(*function)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=address;info.name="team results loader fixture";
                    info.rawCpuInvoker=function;info.entryPoint=reinterpret_cast<void*>(function);TranslatedFunctionRegistry::Register(info);
                }
                void initialize_scores(unsigned active){
                    uint32_t total=0;
                    for(unsigned i=0;i<rows;i++){
                        auto record=result+scores+i*scoreStride;
                        Memory::Write32(record,i<active?50+i:0);
                        Memory::WriteFloat32(record+4,0);
                        Memory::Write32(record+12,i<active?i:UINT32_MAX);
                        Memory::Write8(Memory::Read32(result+points+4*i)+0x198,0);
                        if(i<active) total+=50+i;
                    }
                    Memory::Write32(result+teamScore,total);
                    Memory::WriteFloat32(result+currentScore,0);
                }
                int main(int argc,char** argv){
                    CHECK(argc==2);Memory::Init(32*1024*1024);
                    auto f=std::fopen(argv[1],"rb");CHECK(f);
                    CHECK(std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},f)=={{image.Memory.Length}});std::fclose(f);
                    entry(0x80010dd8,format);entry(0x805ba284,attach_loader);entry(0x805ba2cc,load_control);entry(0x805ba28c,no_effect);
                    {{string.Join("\n", entries.Select(pc => $"entry(0x{pc:X8},func_{pc:X8});"))}}
                    entry(0x80007000,mode);entry(0x8060be48,reserve);entry(0x8060be58,add);
                    entry(0x8060d12c,no_effect);entry(0x8060b400,animation);entry(0x8060b4fc,no_effect);entry(0x8060bf84,no_effect);
                    entry(0x8060c89c,no_effect);entry(0x80229a48,allocate);entry(0x8060c378,no_effect);entry(0x8060b700,no_effect);entry(0x807ece40,load_points);
                    entry(0x805c0e2c,no_effect);entry(0x8060c32c,no_effect);entry(0x8083ab38,no_effect);
                    // The retail predicate treats animation state 1 as playing.
                    Memory::Write32(heap+0x10000+0x38,0);
                    Memory::Write32(group+0xc,page);Memory::Write32(page,table);Memory::Write32(page+4,0x33);Memory::Write32(table+0x10,0x80007000);
                    CpuContext c{};CpuContextScope scope(&c);c.gpr[1]=stack;c.gpr[13]=0x80388880;c.gpr[2]=0x8038ac20;c.lr=0x80006000;
                    for(unsigned i=14;i<32;i++)c.gpr[i]=0x12340000+i;
                    for(team=0;team<2;team++){
                        std::memset(Memory::GetPointer(result-8,size+16),0x91,size+16);Memory::Write32(result+0x64,group);
                        reserved=loadedRows=loadedPoints=0;children.clear();cursor=heap;
                        c.gpr[3]=result;c.gpr[4]=team;func_807EBEF4(&c);
                        CHECK(reserved==children.size() && loadedRows==rows && loadedPoints==rows);
                        CHECK(Memory::Read32(result+teamOffset)==team && Memory::Read32(result+pointTotal)==heap);
                        for(unsigned i=0;i<rows;i++){
                            CHECK(children[i]==result+items+i*itemStride);
                            CHECK(children[rows+1+i]==heap+(i+1)*0x200);
                            CHECK(Memory::Read32(result+points+4*i)==children[rows+1+i]);
                        }
                        CHECK(children[rows]==heap && Memory::Read32(result-4)==0x91919191 && Memory::Read32(result+size)==0x91919191);
                        Memory::Write32(page+8,4);
                        initialize_scores(6);
                        c.gpr[3]=result;TeamResult_UpdateScoreAnimation(&c);
                        CHECK(Memory::ReadFloat32(result+scores+4)==.5f && Memory::ReadFloat32(result+currentScore)==1.f);
                        if(rows>6){
                            initialize_scores(rows);
                            for(unsigned tick=0;tick<240+rows;tick++){
                                c.gpr[3]=result;func_807EC990(&c);
                            }
                            c.gpr[3]=result;TeamResult_IsAnimating(&c);CHECK(c.gpr[3]==1);
                            initialize_scores(rows);
                            for(unsigned tick=0;tick<240+rows;tick++){
                                c.gpr[3]=result;TeamResult_UpdateScoreAnimation(&c);
                            }
                            c.gpr[3]=result;TeamResult_IsAnimating(&c);
                            if(c.gpr[3]){
                                std::fprintf(stderr,"summary %f of %u\n",Memory::ReadFloat32(result+currentScore),Memory::Read32(result+teamScore));
                                for(unsigned i=0;i<rows;i++){
                                    auto record=result+scores+i*scoreStride;
                                    if(Memory::ReadFloat32(record+4)!=50+i || !Memory::Read8(Memory::Read32(result+points+4*i)+0x198))
                                        std::fprintf(stderr,"row %u: score %f of %u, child %u\n",i,Memory::ReadFloat32(record+4),50+i,
                                            Memory::Read8(Memory::Read32(result+points+4*i)+0x198));
                                }
                            }
                            CHECK(c.gpr[3]==0);
                            CHECK(Memory::ReadFloat32(result+currentScore)==Memory::Read32(result+teamScore));
                            for(unsigned i=0;i<rows;i++){
                                auto record=result+scores+i*scoreStride;
                                CHECK(Memory::ReadFloat32(record+4)==50+i && Memory::Read32(record)==50+i);
                                CHECK(Memory::Read8(Memory::Read32(result+points+4*i)+0x198)==1);
                            }
                        }
                    }
                    CHECK(c.gpr[1]==stack && c.lr==0x80006000);
                    for(unsigned i=14;i<32;i++)CHECK(c.gpr[i]==0x12340000+i);
                    return 0;
                }
                """);
            var args = TranslatorCppTestHarness.BuildCompileArguments(root, temp, sources, fixture, exe, includeDataSections: false);
            var support = Path.Combine(temp, "translator_test_support.cpp");
            File.WriteAllText(support, File.ReadAllText(support).Replace("const uint8_t kMkwGuestRegion = 0;", "const uint8_t kMkwGuestRegion = 1;", StringComparison.Ordinal));
            using (var compile = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX") ?? "clang++", args) { RedirectStandardError = true })!)
            {
                var errors = compile.StandardError.ReadToEndAsync();
                var completed = compile.WaitForExit(60000);
                if (!completed) compile.Kill(entireProcessTree: true);
                Assert.True(completed, "Team-loader fixture compilation timed out");
                Assert.True(compile.ExitCode == 0, errors.GetAwaiter().GetResult());
            }
            var runInfo = new ProcessStartInfo(exe) { RedirectStandardError = true };
            runInfo.ArgumentList.Add(imageFile);
            using var run = Process.Start(runInfo)!;
            var output = run.StandardError.ReadToEndAsync();
            var finished = run.WaitForExit(10000);
            if (!finished) run.Kill(entireProcessTree: true);
            Assert.True(finished, "Team-loader fixture timed out");
            Assert.True(run.ExitCode == 0, $"Team loader returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }
        finally { Directory.Delete(temp, true); }
    }
}
