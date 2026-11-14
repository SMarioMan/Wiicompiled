using System.Buffers.Binary;
using System.Text.Json;
using Translator.Core.Analysis;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class BattleBalloonCountAuditTests
{
    [Fact]
    [Trait("Requires", "USA game assets")]
    public void EveryDiscoveredLiteralGlobalBalloonCountReadHasAWideRule()
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"translator/Translator.sln")))directory=directory.Parent;
        var root=directory?.FullName??throw new DirectoryNotFoundException("Repository root missing");
        using var document=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"projects/mkwii-ntsc-u/layouts/racedata.json")));
        var types=JsonSerializer.Deserialize<GuestLayoutType[]>(document.RootElement.GetProperty("Types").GetRawText())!;
        var image=new ProgramImageBuilder().Build(Path.Combine(root,"Assets/main.dol"),Path.Combine(root,"Assets/StaticR.rel"),0x8050bf60);
        var map=FunctionMap.Load(Path.Combine(root,"projects/mkwii-ntsc-u/MAP.txt"));
        var entries=map.Addresses.OrderBy(x=>x).ToArray();var selected=new HashSet<uint>();
        // Superset of direct materializations of the actual USA singleton.
        // The field audit below proves the complete effective global address;
        // matching low bits alone never becomes a rewrite rule.
        for(var i=0;i<entries.Length;i++){
            var end=i+1<entries.Length?entries[i+1]:entries[i]+4;
            for(var pc=entries[i];pc<end;pc+=4){
                var word=BinaryPrimitives.ReadUInt32BigEndian(image.Memory.AsSpan(image.GetOffset(pc,4),4));
                if((word&0xffff)==0xff98 && word>>26 is 14 or 24 or 32 or 36){selected.Add(entries[i]);break;}
            }
        }
        foreach(var entry in new uint[]{0x80865454,0x808655a8,0x80865694,0x80865818,0x808659c4,0x80865ba0,0x80865cac})selected.Add(entry);
        var auditor=new GuestLayoutAudit(types,12,128,new Dictionary<uint,string>{{0x809bff98,"BattleBalloonManager"}},
            0x80388880,0x8038ac20,image,includeUnchanged:true);
        var translator=new FunctionTranslator(image);var options=new TranslationOptions(EnableLeafInlining:false,KnownFunctionEntryPoints:entries.ToHashSet());
        var reads=new Dictionary<uint,GuestLayoutReference>();var writes=new HashSet<uint>();
        foreach(var entry in selected){
            var receiver=entry is >=0x80865454 and <=0x80865cac?"BattleBalloonManager":null;
            foreach(var candidate in auditor.Analyze(translator.Discover(entry,options),receiver).Candidates){
                var site=candidate.Reference;
                if(!site.Symbol.Contains("BattleBalloonHeader.halfCount.offset",StringComparison.Ordinal)&&
                   !site.Symbol.Contains("BattleBalloonHeader.objectCount.offset",StringComparison.Ordinal))continue;
                if(site.ExpectedWord>>26==34)reads[site.Instruction]=site;
                if(site.ExpectedWord>>26==38)writes.Add(site.Instruction);
            }
        }
        Assert.True(reads.Count>=9,$"Only found {reads.Count} balloon count reads");
        var rules=document.RootElement.TryGetProperty("ByteOperations",out var operations)?operations.EnumerateArray().ToArray():Array.Empty<JsonElement>();
        var missing=reads.Keys.Where(pc=>!rules.Any(rule=>rule.GetProperty("Instruction").GetString()==$"0x{pc:X8}"&&rule.GetProperty("Kind").GetString()=="derived-count")).OrderBy(x=>x).ToArray();
        Assert.True(missing.Length==0,"Unreviewed byte count reads: "+string.Join(", ",missing.Select(pc=>$"0x{pc:X8}")));
        Assert.Contains(0x80591d8cu,reads.Keys);
        var original=new GuestLayouts(types,12);
        foreach(var (pc,site) in reads){
            var rule=Assert.Single(rules.Where(r=>r.GetProperty("Instruction").GetString()==$"0x{pc:X8}"));
            Assert.Equal($"0x{site.ExpectedWord:X8}",rule.GetProperty("ExpectedWord").GetString());
            Assert.Equal("BattleBalloonManager.objects.count",rule.GetProperty("Count").GetString());
            Assert.Equal(site.Symbol.Contains("halfCount",StringComparison.Ordinal)?5:10,rule.GetProperty("Factor").GetInt32());
            Assert.Equal(original.Symbol(site.Symbol)+site.Addend,original.Symbol(rule.GetProperty("Field").GetString()!));
            Assert.Equal(0,original.Symbol(rule.GetProperty("Source").GetString()!));
        }
        Assert.Equal(new uint[]{0x80865494,0x8086549c},writes.OrderBy(x=>x).ToArray());
    }
}
