using System.Text.Json;
using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class GuestBoundedAccessTests
{
    private static readonly GuestLayoutType[] Types = { new("Byte", 1, 1) };

    [Fact]
    public void PartialGroupsUseBoundsWhileCompleteGroupsRetainOriginalEmission()
    {
        const uint pc = 0x80001000;
        var image = TranslatorCppTestHarness.CreateImage((pc, 0x90830000), (pc+4, 0x80a30000), (pc+8, 0x4e800020));
        var bounds = new[] { new GuestBoundedAccess(pc, 0x90830000, 0, "r6", 0, 4),
            new GuestBoundedAccess(pc+4, 0x80a30000, 0, "r6", 0, 4, Fallback: 5) };
        var original = new FunctionTranslator(image).Translate(pc).CxxCode;
        var full = new GuestLayoutRelocations(Types, 4, 8, Array.Empty<GuestLayoutReference>(), boundedAccesses: bounds);
        Assert.Equal(original, new FunctionTranslator(image, layoutRelocations: full).Translate(pc).CxxCode);
        var partial = new GuestLayoutRelocations(Types, 4, 5, Array.Empty<GuestLayoutReference>(), boundedAccesses: bounds);
        var translated = new FunctionTranslator(image, layoutRelocations: partial).Translate(pc);
        Assert.Equal(2, translated.LinearIr.Blocks.SelectMany(block => block.Instructions).OfType<IrCall>()
            .Count(call => call.Target.StartsWith("layout_bound_", StringComparison.Ordinal)));
        Assert.Contains("Memory::Write32", translated.CxxCode);
        Assert.Contains("return 5u;", translated.CxxCode);
        var floating = TranslatorCppTestHarness.CreateImage((pc, 0xd0830000), (pc+4, 0x4e800020));
        Assert.Throws<InvalidDataException>(() => new FunctionTranslator(floating, layoutRelocations:
            new GuestLayoutRelocations(Types, 4, 5, Array.Empty<GuestLayoutReference>(), boundedAccesses:
                new[] { new GuestBoundedAccess(pc, 0xd0830000, 0, "r6", 0, 4) })));
    }

    [Fact]
    public void FieldRelocationRunsBeforeTheBoundedMemoryOperation()
    {
        const uint pc = 0x80001000;
        var types = new[] { new GuestLayoutType("Byte", 1, 1), new GuestLayoutType("Header", 1, 8),
            new GuestLayoutType("Tail", 1, 4), new GuestLayoutType("Object", 1, Fields: new[] {
                new GuestLayoutField("header", "Header"), new GuestLayoutField("players", "Byte", PerPlayer: true),
                new GuestLayoutField("tail", "Tail") }) };
        var image = TranslatorCppTestHarness.CreateImage((pc, 0x9083000c), (pc+4, 0x4e800020));
        var layout = new GuestLayoutRelocations(types, 4, 5,
            new[] { new GuestLayoutReference(pc, 0x9083000c, 0, "offset", "Object.tail.offset") },
            boundedAccesses: new[] { new GuestBoundedAccess(pc, 0x9083000c, 0, "r6", 0, 4) });
        var translated = new FunctionTranslator(image, layoutRelocations: layout).Translate(pc);
        var call = Assert.Single(translated.LinearIr.Blocks.SelectMany(block => block.Instructions).OfType<IrCall>(),
            call => call.Target == "layout_bound_80001000");
        Assert.Equal(13, call.Arguments[1].Constant);
    }

    [Fact]
    public void BoundedOperationsRequireTheVersionFourManifestContract()
    {
        var path = Path.GetTempFileName();
        try
        {
            foreach (var version in new[] { 3, 4 })
            {
                File.WriteAllText(path, JsonSerializer.Serialize(new {
                    Version = version, OriginalPlayers = 4, Types, References = Array.Empty<object>(),
                    BoundedAccesses = new[] { new { Instruction = "0x80001000", ExpectedWord = "0x90830000",
                        Node = 0, Index = "r6", IndexBias = 0, GroupWidth = 4 } }
                }));
                if (version == 3) Assert.Throws<InvalidDataException>(() => GuestLayoutRelocations.Read(path, 5));
                else Assert.Equal(5, GuestLayoutRelocations.Read(path, 5).Players);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ScaledLoopCountersCanGuardEvenAnAlignedExpandedCapacity()
    {
        const uint pc=0x80001000;
        var image=TranslatorCppTestHarness.CreateImage((pc,0x90830000),(pc+4,0x4e800020));
        var bounds=new[] {new GuestBoundedAccess(pc,0x90830000,0,"r6",-2,2,
            IndexScale:2,AlwaysWhenResized:true)};
        var original=new FunctionTranslator(image).Translate(pc).CxxCode;
        var same=new GuestLayoutRelocations(Types,12,12,Array.Empty<GuestLayoutReference>(),boundedAccesses:bounds);
        Assert.Equal(original,new FunctionTranslator(image,layoutRelocations:same).Translate(pc).CxxCode);
        var expanded=new GuestLayoutRelocations(Types,12,24,Array.Empty<GuestLayoutReference>(),boundedAccesses:bounds);
        var code=new FunctionTranslator(image,layoutRelocations:expanded).Translate(pc).CxxCode;
        Assert.Contains("int64_t(relative/1u)*2+(-2)",code);
        Assert.Contains("logical >= 24",code);
        Assert.Contains("layout_bound_80001000",code);
        foreach(var scale in new[] {0,-1})Assert.Throws<InvalidDataException>(()=>new GuestLayoutRelocations(
            Types,12,24,Array.Empty<GuestLayoutReference>(),boundedAccesses:new[] {bounds[0] with {IndexScale=scale}}));
    }

    [Fact]
    public void ResizedCounterBoundsRequireVersionTwenty()
    {
        var path=Path.GetTempFileName();
        try {
            foreach(var version in new[] {19,20,21,22,23})foreach(var always in new[] {false,true}){
                File.WriteAllText(path,JsonSerializer.Serialize(new {
                    Version=version,OriginalPlayers=12,Types,References=Array.Empty<object>(),
                    BoundedAccesses=new[] {new {Instruction="0x80001000",ExpectedWord="0x90830000",Node=0,
                        Index="r6",IndexBias=-2,GroupWidth=2,IndexScale=always?1:2,AlwaysWhenResized=always}}
                }));
                if(version is not (20 or 21 or 22))Assert.Throws<InvalidDataException>(()=>GuestLayoutRelocations.Read(path,24));
                else Assert.Equal(24,GuestLayoutRelocations.Read(path,24).Players);
            }
        }finally{File.Delete(path);}
    }
}
