using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using Translator.Core.Analysis;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class ArchiveManagerGlobalAuditTests
{
    [Fact]
    [Trait("Requires", "USA game assets")]
    public void DirectSingletonConsumersOutsideManagerMethodsHaveCheckedFieldRelocations()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "translator/Translator.sln"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json")));
        var types = document.RootElement.GetProperty("Types").Deserialize<GuestLayoutType[]>()!;
        var references = document.RootElement.GetProperty("References").EnumerateArray().ToDictionary(
            r => (uint.Parse(r.GetProperty("Instruction").GetString()![2..],NumberStyles.HexNumber),r.GetProperty("Node").GetInt32(),r.GetProperty("Operand").GetString()!));
        var layout = new GuestLayouts(types,128);
        var image = new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        var entries = FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt")).Addresses.OrderBy(x=>x).ToArray();
        var selected = new HashSet<uint>();
        // Low bits select a superset, never a rewrite. The semantic auditor
        // must prove the full singleton address and field/pointer derivation.
        for(var i=0;i<entries.Length;i++) {
            var end=i+1<entries.Length?entries[i+1]:entries[i]+4;
            for(var pc=entries[i];pc<end;pc+=4) {
                var word=BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(pc,4),4));
                if((word&0xffff)==0x8f78 && word>>26 is 14 or 24 or 32 or 36) { selected.Add(entries[i]);break; }
            }
        }
        var auditor = new GuestLayoutAudit(types,12,128,new Dictionary<uint,string>{{0x809b8f78,"ArchiveManager"}},0x80388880,0x8038ac20,image);
        var translator = new FunctionTranslator(image);
        var options = new TranslationOptions(EnableLeafInlining:false,KnownFunctionEntryPoints:entries.ToHashSet());
        var candidates = selected.SelectMany(entry=>auditor.Analyze(translator.Discover(entry,options)).Candidates)
            .Where(c=>c.Reference.Symbol.StartsWith("ArchiveManager.",StringComparison.Ordinal)).ToArray();
        Assert.Contains(candidates,c=>c.Reference.Instruction==0x8053d518);
        var missing = new List<string>();
        foreach(var candidate in candidates) {
            var site=candidate.Reference;
            if(!references.TryGetValue((site.Instruction,site.Node,site.Operand),out var known)) {
                missing.Add(JsonSerializer.Serialize(site));continue;
            }
            var expected=layout.Symbol(known.GetProperty("Symbol").GetString()!)*(known.TryGetProperty("Scale",out var scale)?scale.GetInt32():1)+
                (known.TryGetProperty("Addend",out var addend)?addend.GetInt32():0);
            Assert.Equal(expected,candidate.After);
        }
        Assert.True(missing.Count==0,"Unreviewed external ArchiveMgr operands:\n"+string.Join("\n",missing));
    }
}
