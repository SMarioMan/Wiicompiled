using System.Diagnostics;
using Xunit;

namespace Translator.Tests;

public class LayoutAuditHeaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GuestAbiAndAuditMetadataCompileAsCpp17(bool instrumented)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "translator/Translator.sln")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException();
        var file = Path.Combine(Path.GetTempPath(), "mkw-audit-header-" + Guid.NewGuid().ToString("N") + ".cpp");
        try
        {
            File.WriteAllText(file, """
                #include "guest_abi/guest_abi_pch.h"
                constexpr LayoutAccessAudit::Field fields[] = {
                    {"players", 0, 48, 12, 4, 0, 512, 128, 4, true},
                    {"tail", 48, 4, 1, 4, 512, 4, 1, 4}
                };
                constexpr LayoutAccessAudit::Type type{"fixture", 52, 516, fields};
                static_assert(type.fields.end() - type.fields.begin() == 2);
                static_assert(type.fields.begin()[1].newOffset == 512);
                void probe() {
                    LayoutAccessAudit::RegisterType(type);
                    MKW_LAYOUT_AUDIT_FUNCTION(0x80001000);
                    MKW_LAYOUT_AUDIT_SITE(0x80001004);
                    MKW_LAYOUT_AUDIT_READ(0x80010030, 4);
                    MKW_LAYOUT_AUDIT_WRITE(0x80010030, 4);
                }
                """);
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX") ?? "clang++")
                { RedirectStandardError = true };
            foreach (var arg in new[] { "-std=gnu++17", "-w", "-fsyntax-only", "-I" + Path.Combine(root, "runtime/include"), file })
                start.ArgumentList.Add(arg);
            if (instrumented) start.ArgumentList.Add("-DMKW_ENABLE_LAYOUT_ACCESS_AUDIT=1");
            using var process = Process.Start(start)!;
            var errors = process.StandardError.ReadToEndAsync();
            var done = process.WaitForExit(60000);
            if (!done) process.Kill(entireProcessTree: true);
            Assert.True(done, "Guest ABI header compilation timed out");
            Assert.True(process.ExitCode == 0, errors.GetAwaiter().GetResult());
        }
        finally { File.Delete(file); }
    }
}
