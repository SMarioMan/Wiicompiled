using System.Diagnostics;
using System.Text.Json;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class RetiredLayoutAuditTests
{
    private static GuestLayoutType[] Types() => new[] {
        new GuestLayoutType("AuditHeader",4,8), new GuestLayoutType("AuditWord",4,4),
        new GuestLayoutType("AuditObject",4,Fields:new[] {
            new GuestLayoutField("header","AuditHeader"),
            new GuestLayoutField("slots","AuditWord",PerPlayer:true),
            new GuestLayoutField("tail","AuditWord") }),
        new GuestLayoutType("AuditWideArray",4,Fields:new[]{
            new GuestLayoutField("header","AuditHeader"),
            new GuestLayoutField("pool","AuditWord",Count:10,PerPlayer:true),
            new GuestLayoutField("players","AuditWord",PerPlayer:true),
            new GuestLayoutField("tail","AuditWord") }) };

    [Fact]
    public void MetadataDerivesMovedRangesAndCurrentOverlapsThrough128()
    {
        foreach (var capacity in new[] {12,13,24,25,26,48,52,64,100,116,118,127,128})
        {
            var plan = new GuestLayoutRelocations(Types(),12,capacity,Array.Empty<GuestLayoutReference>());
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(plan.DescribeRetiredAccessMap()));
            var type = json.RootElement.GetProperty("Types").EnumerateArray().Single(t=>t.GetProperty("Name").GetString()=="AuditObject");
            var tail = type.GetProperty("Fields").EnumerateArray().Single(f=>f.GetProperty("Name").GetString()=="tail");
            Assert.Equal(56,tail.GetProperty("Old").GetProperty("Offset").GetInt32());
            Assert.Equal(8+4*capacity,tail.GetProperty("New").GetProperty("Offset").GetInt32());
            Assert.Equal(capacity!=12,tail.GetProperty("Moved").GetBoolean());
            Assert.Contains(capacity==12?"tail":"slots",tail.GetProperty("OverlappingCurrentFields").EnumerateArray().Select(x=>x.GetString()));
        }
    }

    [Fact]
    public void RealTranslatedAndNativeMemoryAccessesRespectFieldIntentOverlapAndLifetime()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln"))) directory=directory.Parent;
        var root=directory?.FullName??throw new DirectoryNotFoundException("Repository root missing");
        var instructions=new (uint Address,uint Value)[] {
            (0x80001000,0x80630038),(0x80001004,0x4e800020),
            (0x80001100,0x90830038),(0x80001104,0x4e800020),
            (0x80002000,0x80630038),(0x80002004,0x4e800020),
            (0x80002100,0x90830038),(0x80002104,0x4e800020),
            (0x80007000,0x9421ffc4),(0x80007004,0x80610038),(0x80007008,0x3821003c),(0x8000700c,0x4e800020),
            (0x8000a000,0x806301e8),(0x8000a004,0x4e800020),(0x8000a100,0x806301e8),(0x8000a104,0x4e800020) };
        var image=TranslatorCppTestHarness.CreateImage(instructions);
        var references=instructions.Where(x=>x.Address<0x80007000&&(x.Address&0xff)==0).Select(x=>
            new GuestLayoutReference(x.Address,x.Value,0,"offset","AuditObject.tail.offset")).Concat(new[] {
                new GuestLayoutReference(0x80007000,0x9421ffc4,0,"offset","AuditObject.size",Scale:-1),
                new GuestLayoutReference(0x80007000,0x9421ffc4,1,"right","AuditObject.size",Scale:-1),
                new GuestLayoutReference(0x80007004,0x80610038,0,"offset","AuditObject.tail.offset"),
                new GuestLayoutReference(0x80007008,0x3821003c,0,"right","AuditObject.size"),
                new GuestLayoutReference(0x8000a000,0x806301e8,0,"offset","AuditWideArray.players.offset"),
                new GuestLayoutReference(0x8000a100,0x806301e8,0,"offset","AuditWideArray.players.offset") }).ToArray();
        var plan=new GuestLayoutRelocations(Types(),12,24,references);
        plan.ValidateImage(image);
        var temp=Path.Combine(Path.GetTempPath(),"mkw-retired-audit-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var sources=new List<string>{Path.Combine(root,"runtime/src/layout_access_audit.cpp")};
            foreach(var entry in new uint[]{0x80001000,0x80001100,0x80002000,0x80002100,0x80007000,0x8000a000,0x8000a100}){
                // The first pair deliberately omits an existing relocation. It
                // still executes real translated guest loads/stores, not a fake
                // direct call to the auditor's observation implementation.
                var partial = new GuestLayoutRelocations(Types(),12,24,references.Where(r=>r.Instruction!=0x80007004));
                var translator=new FunctionTranslator(image,layoutRelocations:entry==0x8000a000?null:entry==0x80007000?partial:entry>=0x80002000?plan:null);
                var path=Path.Combine(temp,$"{entry:X8}.cpp");
                File.WriteAllText(path,translator.Translate(entry,new TranslationOptions(EnableLeafInlining:false)).CxxCode);sources.Add(path);
            }
            File.WriteAllText(Path.Combine(temp,"layout.h"),plan.EmitNativeConstants());
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");var report=Path.Combine(temp,"report.json");
            File.WriteAllText(harness,"""
                #include "ppc_runtime.h"
                #include "abi_bridge.h"
                #include "layout.h"
                #include <cstdlib>
                #include <string>
                extern "C" void func_80001000(CpuContext*);
                extern "C" void func_80001100(CpuContext*);
                extern "C" void func_80002000(CpuContext*);
                extern "C" void func_80002100(CpuContext*);
                extern "C" void func_80007000(CpuContext*);
                extern "C" void func_8000A000(CpuContext*);
                extern "C" void func_8000A100(CpuContext*);
                static uint32_t fiber=1;
                uint32_t current_fiber(){return fiber;}
                int main(int argc,char** argv){
                    if(argc!=2)return 1;
                    using namespace LayoutAccessAudit;
                    Memory::Init(1024*1024);RegisterGeneratedLayoutAccessAuditMap();
                    SetExecutionContextProvider(current_fiber);
                    constexpr uint32_t base=0x80020000,oldTail=base+56,newTail=base+104;
                    Memory::Write32(oldTail,0x11111111);Memory::Write32(newTail,0x22222222);
                    auto generation=RegisterInstance("AuditObject",base,108,"controlled race",123);
                    Configure({Mode::Log,false,argv[1]});SetPhase("race-start");
                    CpuContext c{};CpuContextScope cpu(&c);c.gpr[1]=0x800f0000;
                    c.gpr[3]=base;func_80002000(&c);if(c.gpr[3]!=0x22222222)return 2;
                    for(unsigned i=0;i<2;i++){c.gpr[3]=base;func_80001000(&c);if(c.gpr[3]!=0x11111111)return 3;}
                    c.gpr[3]=base;c.gpr[4]=0x33333333;func_80002100(&c);
                    c.gpr[3]=base;c.gpr[4]=0x44444444;func_80001100(&c);
                    // A correct current-layout slot is at exactly the retired
                    // tail offset. Its explicit field intent must win.
                    {ScopedSite site({.pc=0x80003000,.type="AuditObject",.field="slots",.index=12,.objectBase=base,.native=true});
                        if(MemoryInline::ReadStack32(oldTail)!=0x44444444)return 4;}
                    // HLE/native scalar paths and flat translated paths both
                    // participate. Context changes cannot leak field intent.
                    {ScopedSite site({.pc=0x80004000,.type="AuditObject",.field="tail",.function="native fixture",.native=true});
                        fiber=2;
                        {ScopedSite other({.pc=0x80004004,.type="AuditObject",.field="slots",.index=12,.native=true});Memory::Read32(oldTail);}
                        fiber=1;MemoryInline::FlatRead32(oldTail);
                        MemoryInline::ReadResolved32(Memory::GetPointer(base,108),56,oldTail);}
                    Configure({Mode::Strict,true,argv[1]});
                    SetGuestPc(0x80005000);Memory::Read32(oldTail); // ambiguous lead, never strict
                    const auto before=Memory::Read32(oldTail);
                    bool stopped=false;
                    try{ScopedSite site({.pc=0x80006000,.type="AuditObject",.field="tail",.native=true});
                        Memory::Write32(oldTail,0xdeadbeef);
                    }catch(const StaleAccess&){stopped=true;}
                    if(!stopped||Memory::Read32(oldTail)!=before)return 5;
                    RetireInstance(generation);
                    SetGuestPc(0x80001000);Memory::Write32(oldTail,0x55555555); // ordinary reuse, no watch
                    auto next=RegisterInstance("AuditObject",base,108,"second race",456);
                    if(next==generation)return 6;
                    RetireOwner(456);
                    SetGuestPc(0x80001100);Memory::Write32(oldTail,0x66666666);
                    Configure({Mode::Log,false,argv[1]});
                    const auto sp=c.gpr[1];
                    func_80007000(&c);if(c.gpr[1]!=sp){std::fprintf(stderr,"stack %x != %x\n",c.gpr[1],sp);return 7;}
                    // The invocation watch must have expired; identical RAM
                    // reused after return is no longer a retired stack field.
                    SetGuestPc(0x80007004);Memory::Read32(sp-108+56);
                    // Canceling a suspended guest thread can destroy its host
                    // stack without running the translated scope destructor.
                    fiber=3;
                    {FunctionScope suspended(0x80007000);SetGuestPc(0x80007000);
                        Memory::Write32(0x800d0000,sp);
                        RetireExecutionContext(3);fiber=1;
                        SetGuestPc(0x80007004);Memory::Read32(0x800d0038);}
                    // A flattened/inlined frame cannot borrow the lifetime of
                    // its enclosing translated function. Report lost coverage
                    // instead of watching its reused stack after it returns.
                    {FunctionScope outer(0x80009000);SetGuestPc(0x80007000);
                        Memory::Write32(0x800c0000,sp);
                        if(ReportJson().find("\"activeInstances\":0")==std::string::npos)return 8;
                        SetGuestPc(0x80007004);Memory::Read32(0x800c0038);}
                    // An indexed legacy access can be beyond the entire old
                    // array while still corrupting a different current field.
                    constexpr uint32_t wideBase=0x800a0000,wideOld=wideBase+488+23*4,wideNew=wideBase+968+23*4;
                    Memory::Write32(wideOld,0x11111111);Memory::Write32(wideNew,0x22222222);
                    auto wide=RegisterInstance("AuditWideArray",wideBase,1068,"expanded indexed array");
                    c.gpr[3]=wideBase+23*4;func_8000A100(&c);if(c.gpr[3]!=0x22222222)return 9;
                    c.gpr[3]=wideBase+23*4;func_8000A000(&c);if(c.gpr[3]!=0x11111111)return 10;
                    {ScopedSite valid({.pc=0x8000a200,.type="AuditWideArray",.field="pool",.index=143,.objectBase=wideBase,.native=true});
                        Memory::Read32(wideOld);} // the same bytes are legitimate pool storage
                    RetireInstance(wide);WriteReport();
                }
                """);
            var args=TranslatorCppTestHarness.BuildCompileArguments(root,temp,sources,harness,exe,includeDataSections:false)+" -DMKW_ENABLE_LAYOUT_ACCESS_AUDIT=1";
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",args){RedirectStandardError=true})!){
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(60000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Auditor compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using(var run=Process.Start(new ProcessStartInfo(exe,report){RedirectStandardError=true})!){
                var errors=run.StandardError.ReadToEndAsync();var done=run.WaitForExit(15000);
                if(!done)run.Kill(entireProcessTree:true);Assert.True(done,"Auditor execution timed out");Assert.True(run.ExitCode==0,$"exit {run.ExitCode}: {errors.GetAwaiter().GetResult()}");
            }
            using var output=JsonDocument.Parse(File.ReadAllText(report));
            var hits=output.RootElement.GetProperty("hits").EnumerateArray().ToArray();
            var read=Assert.Single(hits.Where(h=>h.GetProperty("guestPc").GetString()=="0x80001000"));
            Assert.Equal(2,read.GetProperty("count").GetInt32());
            Assert.Equal("read",read.GetProperty("operation").GetString());
            Assert.True(read.GetProperty("highConfidence").GetBoolean());
            Assert.Equal("0x80020068",read.GetProperty("expectedAddress").GetString());
            Assert.Equal("race-start",read.GetProperty("phase").GetString());
            Assert.Contains("0x80001000",read.GetProperty("translatedStack").EnumerateArray().Select(x=>x.GetString()));
            Assert.Contains(hits,h=>h.GetProperty("guestPc").GetString()=="0x80001100"&&h.GetProperty("operation").GetString()=="write");
            var nativeHit=Assert.Single(hits.Where(h=>h.GetProperty("guestPc").GetString()=="0x80004000"));
            Assert.Equal("native",nativeHit.GetProperty("origin").GetString());
            Assert.Equal(2,nativeHit.GetProperty("count").GetInt32());
            Assert.DoesNotContain(hits,h=>h.GetProperty("guestPc").GetString() is "0x80002000" or "0x80002100" or "0x80003000" or "0x80004004");
            Assert.All(hits.Where(h=>h.GetProperty("guestPc").GetString()=="0x80005000"),h=>Assert.False(h.GetProperty("highConfidence").GetBoolean()));
            var stackHit=Assert.Single(hits.Where(h=>h.GetProperty("guestPc").GetString()=="0x80007004"));
            Assert.Equal("translated stack frame",stackHit.GetProperty("lifetime").GetString());
            Assert.Equal(1,stackHit.GetProperty("count").GetInt32());
            var indexed=Assert.Single(hits.Where(h=>h.GetProperty("guestPc").GetString()=="0x8000a000"));
            Assert.True(indexed.GetProperty("highConfidence").GetBoolean());
            Assert.Equal(23,indexed.GetProperty("racerIndex").GetInt32());
            Assert.Equal("0x800a0424",indexed.GetProperty("expectedAddress").GetString());
            Assert.DoesNotContain(hits,h=>h.GetProperty("guestPc").GetString() is "0x8000a100" or "0x8000a200");
            Assert.Equal(0,output.RootElement.GetProperty("activeInstances").GetInt32());
            var skipped=Assert.Single(output.RootElement.GetProperty("unscopedStackEntries").EnumerateArray());
            Assert.Equal("0x80007000",skipped.GetProperty("guestPc").GetString());
            Assert.Equal(1,skipped.GetProperty("count").GetInt32());
        }
        finally{Directory.Delete(temp,recursive:true);}
    }
}
