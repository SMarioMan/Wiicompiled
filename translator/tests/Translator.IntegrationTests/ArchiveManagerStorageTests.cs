using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Translator.Core.Analysis;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class ArchiveManagerStorageTests
{
    [Fact]
    [Trait("Requires", "USA game assets")]
    public void EveryReviewedArchiveMethodCandidateHasACheckedRelocation()
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName??throw new DirectoryNotFoundException("Repository root missing");
        using var document=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json")));
        var types=JsonSerializer.Deserialize<GuestLayoutType[]>(document.RootElement.GetProperty("Types").GetRawText())!;
        var layout=new GuestLayouts(types,128);
        var refs=document.RootElement.GetProperty("References").EnumerateArray().ToDictionary(
            r=>(uint.Parse(r.GetProperty("Instruction").GetString()![2..],NumberStyles.HexNumber),r.GetProperty("Node").GetInt32(),r.GetProperty("Operand").GetString()!));
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        var mapPath=Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt");var map=FunctionMap.Load(mapPath);
        var translator=new FunctionTranslator(image);
        var auditor=new GuestLayoutAudit(types,12,128,new Dictionary<uint,string>{{0x809b8f78,"ArchiveManager"}},0x80388880,0x8038ac20,image);
        var covered=new HashSet<uint>();var count=0;
        foreach(var line in File.ReadLines(mapPath)){
            var text=line.Split(' ',2)[0];
            if(!uint.TryParse(text,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var entry)||entry<0x8053a710||entry>=0x8053d38c||
                entry is >=0x8053a92c and <0x8053a9e0 or >=0x8053c4d4 and <0x8053c70c)continue;
            var discovery=translator.Discover(entry,new TranslationOptions(EnableLeafInlining:false,KnownFunctionEntryPoints:map.Addresses.ToHashSet()));
            foreach(var candidate in auditor.Analyze(discovery,"ArchiveManager").Candidates){
                var site=candidate.Reference;
                Assert.True(refs.TryGetValue((site.Instruction,site.Node,site.Operand),out var known),$"Unreviewed archive operand at 0x{site.Instruction:X8}");
                var expected=layout.Symbol(known.GetProperty("Symbol").GetString()!)*(known.TryGetProperty("Scale",out var scale)?scale.GetInt32():1)+
                    (known.TryGetProperty("Addend",out var addend)?addend.GetInt32():0);
                Assert.Equal(expected,candidate.After);count++;covered.Add(site.Instruction);
            }
        }
        Assert.True(count>=169,$"Expected the reviewed archive inventory; found {count} operands");
        foreach(var site in new uint[]{0x8053b564,0x8053cb40,0x8053bf18,0x8053c0b4,0x8053aa3c})Assert.Contains(site,covered);
    }

    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(25)] [InlineData(26)]
    [InlineData(48)] [InlineData(52)] [InlineData(64)] [InlineData(100)]
    [InlineData(116)] [InlineData(118)] [InlineData(127)] [InlineData(128)]
    [Trait("Requires", "USA game assets and Clang")]
    public void RealArchiveLifecycleKeepsBothHolderArraysAndRetainedFieldsSeparate(int players)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName??throw new DirectoryNotFoundException("Repository root missing");
        var manifest=Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json");
        var plan=GuestLayoutRelocations.Read(manifest,players);
        using var document=System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
        var types=System.Text.Json.JsonSerializer.Deserialize<GuestLayoutType[]>(document.RootElement.GetProperty("Types").GetRawText())!;
        var layout=new GuestLayouts(types,players);
        Assert.Equal(0x61c,new GuestLayouts(types,12).Symbol("ArchiveManager.size"));
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        plan.ValidateImage(image);
        var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));
        var translator=new FunctionTranslator(image,layoutRelocations:plan);
        var temp=Path.Combine(Path.GetTempPath(),"mkw-archive-manager-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try{
            var entries=new uint[]{0x8053a710,0x8053a760,0x8053a7b0,0x8053a9e0,0x80020494,0x8002058c,
                0x8053c088,0x8053c098,0x8053be8c,0x8053befc,0x8053c0a8};
            var fixtures=new HashSet<uint>{0x80229a48,0x80229a6c,0x80229a90,0x8053a9d8,0x80526268,
                0x8051484c,0x80514880,0x8053a92c,0x8053a998,0x8021a050,0x8021a0a4,
                0x80525d54,0x80525c24,0x80241a2c,0x80241cec,0x805267cc,0x8052638c,0x805262ec};
            var sources=new List<string>{Path.Combine(root,"runtime/src/layout_access_audit.cpp"),
                Path.Combine(root,"runtime/src/hle/layout_audit_lifetimes.cpp"),Path.Combine(root,"runtime/src/guest_region.cpp")};
            foreach(var pc in entries){var path=Path.Combine(temp,$"{pc:X8}.cpp");
                File.WriteAllText(path,translator.Translate(pc,new TranslationOptions(EnableLeafInlining:false,
                    KnownFunctionEntryPoints:map.Addresses.ToHashSet(),LeafInliningBlockedTargets:fixtures)).CxxCode);sources.Add(path);}
            File.WriteAllText(Path.Combine(temp,"layout.h"),plan.EmitNativeConstants());
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var declarations=string.Join("\n",entries.Select(pc=>$"extern \"C\" void func_{pc:X8}(CpuContext*);"));
            var registrations=string.Join("\n",entries.Select(pc=>$"entry(0x{pc:X8},func_{pc:X8});"));
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include "layout.h"
                #include <array>
                #include <cstdio>
                #include <cstring>
                {{declarations}}
                constexpr uint32_t capacity={{players}},size={{layout.Symbol("ArchiveManager.size")}},
                    first={{layout.Symbol("ArchiveManager.kartModelsHolders.offset")}},
                    second={{layout.Symbol("ArchiveManager.kartModelsHolders2.offset")}},
                    content={{layout.Symbol("ArchiveManager.contentArchives.offset")}},
                    course={{layout.Symbol("ArchiveManager.courseLoader.offset")}},
                    loaders={{layout.Symbol("ArchiveManager.kartLoaders.offset")}},
                    state={{layout.Symbol("ArchiveManager.state.offset")}},
                    root=0x81010000,extra=0x81100000,stack=0x81f00000,global=0x809b8f78;
                std::array<bool,capacity*2> live{};uint32_t made=0,destroyed=0,contents=0,workers=0,frees=0;
                void entry(uint32_t pc,void(*fn)(CpuContext*)){
                    TranslatedFunctionInfo info{};info.address=pc;info.name="archive storage fixture";
                    info.rawCpuInvoker=fn;info.entryPoint=reinterpret_cast<void*>(fn);TranslatedFunctionRegistry::Register(info);}
                uint32_t slot(uint32_t p){
                    if(p>=root+first&&p<root+first+capacity*28&&(p-root-first)%28==0)return(p-root-first)/28;
                    if(p>=root+second&&p<root+second+capacity*28&&(p-root-second)%28==0)return capacity+(p-root-second)/28;
                    std::abort();}
                void alloc(CpuContext* c){if(c->gpr[3]==size){
                    c->gpr[3]=root;
                }else if(c->gpr[3]==28)c->gpr[3]=extra;else std::abort();}
                void array_alloc(CpuContext* c){if(c->gpr[3]!=36)std::abort();c->gpr[3]=extra+0x100;}
                void release(CpuContext* c){if(c->gpr[3]!=root)std::abort();
                    if(LayoutAccessAudit::ReportJson().find("\"activeInstances\":0")==std::string::npos)std::abort();++frees;}
                void holder_ctor(CpuContext* c){auto n=slot(c->gpr[3]);if(live[n])std::abort();live[n]=true;++made;
                    std::memset(Memory::GetPointer(c->gpr[3],28),0,28);Memory::Write32(c->gpr[3]+4,0x10000+n);}
                void holder_dtor(CpuContext* c){auto n=slot(c->gpr[3]);if(!live[n])std::abort();live[n]=false;++destroyed;}
                void content_ctor(CpuContext* c){auto p=c->gpr[3];if(p<root+content||p>=root+content+144||(p-root-content)%36)std::abort();
                    std::memset(Memory::GetPointer(p,36),0,36);++contents;}
                void worker_ctor(CpuContext* c){auto p=c->gpr[3];if(p<root+loaders||p>=root+loaders+96||(p-root-loaders)%24)std::abort();
                    std::memset(Memory::GetPointer(p,24),0,24);++workers;}
                void course_ctor(CpuContext* c){if(c->gpr[3]!=root+course)std::abort();std::memset(Memory::GetPointer(c->gpr[3],36),0,36);}
                void task(CpuContext* c){c->gpr[3]=extra+0x200;}
                void factory(CpuContext* c){c->gpr[3]=extra+0x300;}
                void no_op(CpuContext*){}
                void has(CpuContext* c){auto n=slot(c->gpr[3]);if(!live[n])std::abort();c->gpr[3]=Memory::Read32(c->gpr[3]+4);}
                void get(CpuContext* c){auto n=slot(c->gpr[3]);if(!live[n])std::abort();c->gpr[3]=0x10000+n;}
                bool guards(){for(uint32_t i=0;i<16;i++)if(Memory::Read8(root-16+i)!=0x55||Memory::Read8(root+size+i)!=0x55)return false;return true;}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);FILE* f=std::fopen(argv[1],"rb");if(!f)return 2;
                    auto loaded=std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},f);std::fclose(f);if(loaded!={{image.Memory.Length}})return 3;
                    RegisterGeneratedLayoutAccessAuditMap();LayoutAccessAudit::Configure({LayoutAccessAudit::Mode::Strict});
                    {{registrations}}
                    entry(0x80229a48,alloc);entry(0x80229a6c,array_alloc);entry(0x80229a90,release);
                    entry(0x8053a9d8,holder_ctor);entry(0x80526268,holder_dtor);entry(0x8051484c,content_ctor);entry(0x80514880,no_op);
                    entry(0x8053a92c,worker_ctor);entry(0x8053a998,no_op);entry(0x8021a050,course_ctor);entry(0x8021a0a4,no_op);
                    entry(0x80525d54,no_op);entry(0x805267cc,no_op);entry(0x80525c24,factory);entry(0x80241a2c,task);entry(0x80241cec,no_op);
                    entry(0x8052638c,has);entry(0x805262ec,get);
                    CpuContext c{};CpuContextScope scope(&c);c.gpr[1]=stack;c.gpr[2]=0x8038ac20;c.gpr[13]=0x80388880;
                    Memory::Write32(global,0);Memory::Write32(0x809b8f80,extra+0x400);Memory::Write32(extra+0x430,extra+0x500);
                    for(unsigned race=0;race<2;race++){
                        made=destroyed=contents=workers=0;
                        std::memset(Memory::GetPointer(root-16,size+32),0x55,size+32);
                        func_8053A710(&c);if(c.gpr[3]!=root||Memory::Read32(global)!=root||made!=capacity*2||contents!=4||workers!=4||!guards())return 4;
                        if(Memory::Read32(root+{{layout.Symbol("ArchiveManager.taskThread.offset")}})!=extra+0x200||Memory::Read8(root+state+13)!=1)return 5;
                        for(uint32_t i=0;i<capacity;i++){
                            c.gpr[3]=root;c.gpr[4]=i;func_8053C088(&c);if(c.gpr[3]!=0x10000+i)return 6;
                            c.gpr[3]=root;c.gpr[4]=i;func_8053C098(&c);if(c.gpr[3]!=0x10000+capacity+i)return 7;
                            c.gpr[3]=root;c.gpr[4]=i;func_8053BE8C(&c);if(c.gpr[3]!=0x10000+i)return 8;
                            c.gpr[3]=root;c.gpr[4]=i;func_8053BEFC(&c);if(c.gpr[3]!=0x10000+capacity+i)return 9;
                        }
                        for(uint32_t i=0;i<4;i++){
                            Memory::Write32(root+content+i*36+32,4);c.gpr[3]=root;c.gpr[4]=i;func_8053C0A8(&c);if(c.gpr[3]!=1)return 10;
                        }
                        func_8053A760(&c);if(destroyed!=capacity*2||Memory::Read32(global)!=0||!guards()||c.gpr[1]!=stack)return 11;
                    }
                    if(frees!=2)return 12;
                }
                """);
            var args=TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)+" -DMKW_ENABLE_LAYOUT_ACCESS_AUDIT=1";
            var support=Path.Combine(temp,"translator_test_support.cpp");
            File.WriteAllText(support,File.ReadAllText(support).Replace("const uint8_t kMkwGuestRegion = 0;","const uint8_t kMkwGuestRegion = 1;",StringComparison.Ordinal));
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",args){RedirectStandardError=true})!){
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);if(!done)compile.Kill(entireProcessTree:true);
                Assert.True(done,"Archive fixture compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());}
            using var run=Process.Start(new ProcessStartInfo(exe,binary){RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(30000);if(!finished)run.Kill(entireProcessTree:true);
            Assert.True(finished,"Archive fixture execution timed out");Assert.True(run.ExitCode==0,$"Archive fixture returned {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }finally{Directory.Delete(temp,recursive:true);}
    }
}
