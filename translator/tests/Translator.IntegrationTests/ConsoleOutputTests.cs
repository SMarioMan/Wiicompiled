using System.Diagnostics;
using Xunit;

namespace Translator.Tests;

public class ConsoleOutputTests
{
    [Fact]
    public void FormattedGuestConsoleBytesReachHostOutputWithoutStringTruncation()
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var temp=Path.Combine(Path.GetTempPath(),"mkw-console-output-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try {
            var harness=Path.Combine(temp,"harness.cpp");var exe=Path.Combine(temp,"test");
            File.WriteAllText(harness,"""
                #include "memory.h"
                #include <cstring>
                extern "C" uint32_t ConsoleWrite_HLE_80015500(uint32_t,uint32_t,uint32_t,uint32_t);
                int main(){
                    Memory::Init(2*1024*1024);
                    constexpr uint32_t buffer=0x80010000,length=buffer+0x100;
                    const char message[]="SDK handshake error 52100\n\0tail";
                    std::memcpy(Memory::GetPointer(buffer,sizeof(message)-1),message,sizeof(message)-1);
                    Memory::Write32(length,sizeof(message)-1);
                    if(ConsoleWrite_HLE_80015500(1,buffer,length,0)!=0)return 1;
                    if(Memory::Read32(length)!=sizeof(message)-1)return 2;
                    Memory::Write32(length,0);
                    if(ConsoleWrite_HLE_80015500(1,0xffffffff,length,0)!=0)return 3;
                    if(ConsoleWrite_HLE_80015500(1,buffer,0,0)!=1)return 4;
                    Memory::Write32(length,10);
                    if(ConsoleWrite_HLE_80015500(1,0xffffffff,length,0)!=1 || Memory::Read32(length)!=0)return 5;
                    return 0;
                }
                """);
            using(var compile=Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX")??"clang++",
                TranslatorCppTestHarness.BuildCompileArguments(root,temp,new[]{Path.Combine(root,"runtime/src/hle/c_stdio.cpp"),Path.Combine(root,"runtime/src/guest_region.cpp")},harness,exe,includeDataSections:false)+$" -I\"{Path.Combine(root,"runtime/src")}\""){RedirectStandardError=true})!) {
                var errors=compile.StandardError.ReadToEndAsync();var done=compile.WaitForExit(45000);
                if(!done)compile.Kill(entireProcessTree:true);Assert.True(done,"Console output compilation timed out");Assert.True(compile.ExitCode==0,errors.GetAwaiter().GetResult());
            }
            using var run=Process.Start(new ProcessStartInfo(exe){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true})!;
            var output=run.StandardOutput.ReadToEndAsync();var errorsRun=run.StandardError.ReadToEndAsync();var finished=run.WaitForExit(15000);
            if(!finished)run.Kill(entireProcessTree:true);Assert.True(finished,"Console output execution timed out");
            Assert.True(run.ExitCode==0,$"Console output returned {run.ExitCode}: {errorsRun.GetAwaiter().GetResult()}");
            Assert.Equal("SDK handshake error 52100\n\0tail",output.GetAwaiter().GetResult());
        }
        finally {Directory.Delete(temp,recursive:true);}
    }
}
