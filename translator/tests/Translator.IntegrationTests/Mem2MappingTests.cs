using System.Diagnostics;
using Xunit;

namespace Translator.Tests;

public class Mem2MappingTests
{
    [Fact]
    public void ExtendedMem2MapsAllGuestAliasesAndTracksGxWrites()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "translator/Translator.sln")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException();
        var temp = Directory.CreateTempSubdirectory("mkw-extended-mem2-");
        try
        {
            var harness = Path.Combine(temp.FullName, "guest_ram.cpp");
            File.WriteAllText(harness, """
                #include "gx_guest_write.h"
                #include "memory.h"
                #include <cstdint>

                int main(int argc, char**) {
                    const bool expanded = argc > 1;
                    const auto size = expanded ? Memory::kLargeFieldMem2Size : Memory::kMem2Size;
                    Memory::Init(Memory::Config::WiiDefaults(size));
                    constexpr auto stockEnd = Memory::kMem2CachedBase + Memory::kMem2Size;
                    if (Memory::MappedMem2Size() != size) return 1;
                    if (Memory::Contains(stockEnd, 4) != expanded) return 2;
                    if (Memory::Contains(Memory::kMem2CachedBase + size, 4)) return 3;

                    const auto offset = static_cast<uint32_t>(size - 4);
                    const auto cached = Memory::kMem2CachedBase + offset;
                    const auto uncached = Memory::kMem2UncachedBase + offset;
                    const auto physical = Memory::kMem2PhysicalBase + offset;
                    Memory::Write32(physical, 0x12345678u);
                    if (Memory::Read32(cached) != 0x12345678u ||
                        Memory::Read32(uncached) != 0x12345678u) return 4;
                    const auto before = GxGuestWrite::GenerationForRange(physical, 4);
                    GxGuestWrite::NotifyWrite(uncached, 4);
                    if (GxGuestWrite::GenerationForRange(cached, 4) != before + 1) return 5;
                    return 0;
                }
                """);
            var executable = Path.Combine(temp.FullName, "test");
            var args = TranslatorCppTestHarness.BuildCompileArguments(root, temp.FullName,
                Array.Empty<string>(), harness, executable, includeDataSections: false);
            Run(new ProcessStartInfo(Environment.GetEnvironmentVariable("MKW_TEST_CXX") ?? "clang++", args), 60000);
            foreach (var arguments in new[] { "", "extended" })
                Run(new ProcessStartInfo(executable, arguments), 10000);
        }
        finally { temp.Delete(recursive: true); }
    }

    private static void Run(ProcessStartInfo start, int timeout)
    {
        start.RedirectStandardError = true;
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        var done = process.WaitForExit(timeout);
        if (!done) process.Kill(entireProcessTree: true);
        Assert.True(done, $"{start.FileName} timed out");
        Assert.True(process.ExitCode == 0, $"Exit {process.ExitCode}: {errors.GetAwaiter().GetResult()}");
    }
}
