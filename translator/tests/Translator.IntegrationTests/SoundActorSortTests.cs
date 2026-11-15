using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class SoundActorSortTests
{
    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(25)] [InlineData(26)]
    [InlineData(48)] [InlineData(52)] [InlineData(64)] [InlineData(100)]
    [InlineData(116)] [InlineData(118)] [InlineData(127)] [InlineData(128)]
    [Trait("Requires", "USA game assets and Clang")]
    public void OriginalActorCollectionAndToggleLoopsProtectExpandedStackScratch(int players)
    {
        Run(players,false);
    }

    [Fact]
    [Trait("Requires", "USA game assets and Clang")]
    public void OriginalFixedActorScratchTriggersTheStrictRetiredLayoutControl() => Run(128,true);

    private static void Run(int players,bool unpatched)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName??throw new DirectoryNotFoundException();
        var manifest=Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json");
        var plan=GuestLayoutRelocations.Read(manifest,players);
        using var doc=JsonDocument.Parse(File.ReadAllText(manifest));
        var layout=new GuestLayouts(doc.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!,players);
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        plan.ValidateImage(image);
        var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));
        var options=new TranslationOptions(EnableLeafInlining:false,KnownFunctionEntryPoints:map.Addresses.ToHashSet());
        var translator=new FunctionTranslator(image,layoutRelocations:unpatched?null:plan);
        if(players==12)Assert.Equal(new FunctionTranslator(image).Translate(0x8070b0b4,options).CxxCode,translator.Translate(0x8070b0b4,options).CxxCode);
        var temp=Path.Combine(Path.GetTempPath(),"mkw-sound-sort-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try{
            var sources=new List<string>{Path.Combine(root,"runtime/src/layout_access_audit.cpp")};
            var entries=new uint[]{0x8070b0b4,0x80020a2c,0x80020a78,0x80010fa0,0x80708bc0};
            foreach(var pc in entries){var file=Path.Combine(temp,$"{pc:X8}.cpp");File.WriteAllText(file,translator.Translate(pc,options).CxxCode);sources.Add(file);}
            File.WriteAllText(Path.Combine(temp,"layout.h"),plan.EmitNativeConstants());
            var binary=Path.Combine(temp,"image.bin");File.WriteAllBytes(binary,image.Memory);
            var harness=Path.Combine(temp,"main.cpp");var exe=Path.Combine(temp,"test");
            var declarations=string.Join("\n",entries.Select(pc=>$"extern \"C\" void func_{pc:X8}(CpuContext*);"));
            var registrations=string.Join("\n",entries.Where(pc=>pc!=0x80010fa0).Select(pc=>$"entry(0x{pc:X8},func_{pc:X8});"));
            File.WriteAllText(harness,$$"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include "layout.h"
                #include <vector>
                #include <set>
                #include <algorithm>
                #include <cstdio>
                #include <cstring>
                {{declarations}}
                constexpr uint32_t n={{players}},frame={{layout.Symbol("SoundActorSortFrame.size")}},root=0x81000000,
                    actors=0x81200000,vt=0x81001000,position=0x81002000,sp=0x81f00000;
                std::vector<uint32_t> roster;std::set<uint32_t> toggled;unsigned sorts=0;
                void entry(uint32_t a,void(*fn)(CpuContext*)){
                    TranslatedFunctionInfo i{};i.address=a;i.name="sound sort fixture";i.rawCpuInvoker=fn;i.entryPoint=reinterpret_cast<void*>(fn);TranslatedFunctionRegistry::Register(i);}
                void next(CpuContext* c){
                    if(c->gpr[3]!=root+0x78)std::abort();
                    const auto old=c->gpr[4];auto it=std::find(roster.begin(),roster.end(),old);
                    c->gpr[3]=!old?(roster.empty()?0:roster.front()):it!=roster.end()&&++it!=roster.end()?*it:0;
                }
                void pos(CpuContext* c){auto i=(c->gpr[3]-actors)/0x100;
                    float x=float(i+1);uint32_t bits;std::memcpy(&bits,&x,4);
                    Memory::Write32(position,bits);Memory::Write32(position+4,0);Memory::Write32(position+8,0);c->gpr[3]=position;}
                void sort(CpuContext* c){
                    if(c->gpr[5]!=8||c->gpr[6]!=0x80708bc0)std::abort();
                    const auto start=c->gpr[3],count=c->gpr[4];
                    if(start<sp-frame+8||uint64_t(start)+count*8>sp-frame+{{layout.Symbol("SoundActorSortFrame.gprs.offset")}})std::abort();
                    for(unsigned i=0;i<count;i++){
                        const auto actor=Memory::Read32(start+i*8),bits=Memory::Read32(start+i*8+4);
                        if(std::find(roster.begin(),roster.end(),actor)==roster.end())std::abort();
                        float expected=float((actor-actors)/0x100+1);uint32_t expectedBits;std::memcpy(&expectedBits,&expected,4);
                        if(bits!=expectedBits)std::abort();
                    }
                    func_80010FA0(c); // actual guest heapsort and distance comparator
                    for(unsigned i=1;i<count;i++)if(Memory::Read32(start+(i-1)*8+4)>Memory::Read32(start+i*8+4))std::abort();
                    sorts++;
                }
                void active(CpuContext* c){c->gpr[3]=1;}
                void toggle(CpuContext* c){if(c->gpr[4]>1||!toggled.insert(c->gpr[3]).second)std::abort();}
                void add(unsigned cls,unsigned count){for(unsigned j=0;j<count;j++){
                    auto p=actors+roster.size()*0x100;roster.push_back(p);Memory::Write32(p,vt);Memory::Write32(p+0xc,cls);Memory::Write16(p+8,0x20b);
                } }
                int main(int argc,char** argv){
                    if(argc!=2)return 1;Memory::Init(32*1024*1024);FILE* file=std::fopen(argv[1],"rb");if(!file)return 2;
                    if(std::fread(Memory::GetPointer(0x80000000,{{image.Memory.Length}}),1,{{image.Memory.Length}},file)!={{image.Memory.Length}})return 3;std::fclose(file);
                    {{registrations}}
                    entry(0x806fe1d8,active);entry(0x800af0e0,next);entry(0x80010000,pos);entry(0x80010fa0,sort);entry(0x806fbfcc,toggle);
                    Memory::Write32(vt+0x14,0x80010000);
                    RegisterGeneratedLayoutAccessAuditMap();LayoutAccessAudit::Configure({LayoutAccessAudit::Mode::Strict});
                    CpuContext c{};CpuContextScope cpu(&c);c.gpr[1]=sp;c.gpr[2]=0x8038ac20;c.gpr[13]=0x80388880;
                    if({{(unpatched?"true":"false")}}){
                        c.gpr[3]=root;
                        try{func_8070B0B4(&c);return 80;}
                        catch(const LayoutAccessAudit::StaleAccess&){
                            const auto report=LayoutAccessAudit::ReportJson();
                            if(report.find("0x8070b0c0")==std::string::npos||report.find("fpr31")==std::string::npos)return 81;
                            return 0;
                        }
                    }
                    for(unsigned pass=0;pass<4;pass++){
                        roster.clear();toggled.clear();sorts=0;
                        const bool objects=pass%2;Memory::Write8(root+0x174,objects);Memory::Write8(root+0x4c,1);Memory::Write8(root+0x70,0);
                        if(objects)add(0,80+10*n);else{add(4,n);add(5,n);add(7,10);add(3,5*n);}
                        std::memset(Memory::GetPointer(sp-frame-0x100,frame+0x120),0x55,frame+0x120);
                        for(unsigned r=14;r<32;r++){c.gpr[r]=0x12340000+r;c.fpr[r].raw=0x1234000000000000ull+r;}
                        c.gpr[3]=root;func_8070B0B4(&c);
                        if(c.gpr[1]!=sp||toggled.size()!=roster.size()||sorts!=(objects?1:4))return 4;
                        for(auto p:roster)if(!toggled.contains(p))return 5;
                        for(unsigned r=14;r<32;r++)if(c.gpr[r]!=0x12340000+r||c.fpr[r].raw!=0x1234000000000000ull+r)return 6;
                        for(unsigned i=0;i<16;i++)if(Memory::Read8(sp-frame-0x100+i)!=0x55||Memory::Read8(sp+8+i)!=0x55)return 7;
                    }
                    const auto report=LayoutAccessAudit::ReportJson();
                    if(report.find("\"hits\":[]")==std::string::npos||report.find("\"activeInstances\":0")==std::string::npos){std::fprintf(stderr,"%s",report.c_str());return 8;}
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)+" -DMKW_ENABLE_LAYOUT_ACCESS_AUDIT=1"){RedirectStandardError=true})!){
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Sound scratch compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());}
            using var run=Process.Start(new ProcessStartInfo(exe,binary){RedirectStandardError=true})!;
            var output=run.StandardError.ReadToEndAsync();var completed=run.WaitForExit(15000);if(!completed)run.Kill(entireProcessTree:true);
            Assert.True(completed,"Sound scratch execution timed out");Assert.True(run.ExitCode==0,$"exit {run.ExitCode}: {output.GetAwaiter().GetResult()}");
        }finally{Directory.Delete(temp,true);}
    }
}
