using Translator.Core.Analysis;
using Translator.Core.Loading;
using Translator.Core.Ir;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class GuestLayoutAuditTests
{
    private const uint Start = 0x80001000;
    private static FunctionDiscoveryResult Discover(params uint[] words)
    {
        var image = TranslatorCppTestHarness.CreateImage(words.Select((word, index) => (Start+(uint)index*4, word)).ToArray());
        return new FunctionTranslator(image).Discover(Start, new TranslationOptions(EnableLeafInlining: false));
    }

    [Fact]
    public void AFixedPhysicalArrayCanMoveAfterAPlayerArrayWithADifferentStride()
    {
        var types=new[] {new GuestLayoutType("Byte",1,1),new GuestLayoutType("Word",4,4),
            new GuestLayoutType("Owner",4,Fields:new[] {new GuestLayoutField("players","Byte",PerPlayer:true),
                new GuestLayoutField("physical","Word",4)})};
        var audit=new GuestLayoutAudit(types,12,24);
        var candidate=Assert.Single(audit.Analyze(Discover(0x1ca40004,0x7ca32a14,0x80c5000c,0x4e800020),"Owner").Candidates);
        Assert.Equal(12,candidate.Before);Assert.Equal(24,candidate.After);Assert.Equal(4,candidate.IndexStride);
        Assert.Empty(audit.Analyze(Discover(0x1ca40003,0x7ca32a14,0x80c5000c,0x4e800020),"Owner").Candidates);
    }

    [Fact]
    public void DeclaredPointerFieldsCarryPointeeIdentityIncludingIndexedPointerArrays()
    {
        var types=new[] {new GuestLayoutType("Word",4,4),
            new GuestLayoutType("Leaf",4,Fields:new[] {new GuestLayoutField("players","Word",PerPlayer:true),new GuestLayoutField("tail","Word")}),
            new GuestLayoutType("Owner",4,Fields:new[] {new GuestLayoutField("pointers","Word",PerPlayer:true,PointerTo:"Leaf")})};
        var audit=new GuestLayoutAudit(types,2,4);
        var direct=Assert.Single(audit.Analyze(Discover(0x80a30000,0x80c50008,0x4e800020),"Owner").Candidates);
        Assert.Equal("Leaf",direct.ReceiverType);Assert.Equal(8,direct.Before);Assert.Equal(16,direct.After);
        var indexed=Assert.Single(audit.Analyze(Discover(0x1ca40004,0x7ca32a14,0x80a50000,0x80c50008,0x4e800020),"Owner").Candidates);
        Assert.Equal("Leaf",indexed.ReceiverType);
        var fixedTypes=types.Append(new GuestLayoutType("FixedOwner",4,Fields:new[] {
            new GuestLayoutField("players","Word",PerPlayer:true),new GuestLayoutField("physical","Word",4,PointerTo:"Leaf")}));
        var physical=new GuestLayoutAudit(fixedTypes,2,4).Analyze(
            Discover(0x1ca40004,0x7ca32a14,0x80a50008,0x80c50008,0x4e800020),"FixedOwner");
        Assert.Equal(2,physical.Candidates.Count);
        Assert.Equal(new[] {"FixedOwner","Leaf"},physical.Candidates.Select(candidate=>candidate.ReceiverType));
        Assert.Empty(audit.Analyze(Discover(0x80a30001,0x80c50008,0x4e800020),"Owner").Candidates);
        var untyped=types.Select(type=>type.Name=="Owner" ? type with {Fields=new[] {new GuestLayoutField("pointers","Word",PerPlayer:true)}} : type);
        Assert.Empty(new GuestLayoutAudit(untyped,2,4).Analyze(Discover(0x80a30000,0x80c50008,0x4e800020),"Owner").Candidates);
        Assert.Throws<InvalidDataException>(()=>new GuestLayouts(types.Select(type=>type.Name=="Owner" ? type with {
            Fields=new[] {new GuestLayoutField("pointer","Word",PointerTo:"Missing")}} : type),2));
        Assert.Throws<InvalidDataException>(()=>new GuestLayouts(new[] {new GuestLayoutType("Wide",4,8),
            new GuestLayoutType("Owner",4,Fields:new[] {new GuestLayoutField("pointer","Wide",PointerTo:"Owner")})},2));
    }

    [Fact]
    public void RotatedIndicesRequireAMaskThatRemovesWrappedLowBits()
    {
        var types = new[] {new GuestLayoutType("Header",4,8), new GuestLayoutType("Word",4,4),
            new GuestLayoutType("Object",4,Fields:new[] {new GuestLayoutField("header","Header"),
                new GuestLayoutField("first","Word",PerPlayer:true), new GuestLayoutField("second","Word",PerPlayer:true)})};
        var audit = new GuestLayoutAudit(types,2,4);
        // rlwinm r5,r4,2,0,29; add r5,r3,r5; lwz r6,second(r5)
        var candidate=Assert.Single(audit.Analyze(Discover(0x5485103a,0x7ca32a14,0x80c50010,0x4e800020),"Object").Candidates);
        Assert.Equal(16,candidate.Before); Assert.Equal(24,candidate.After); Assert.Equal(4,candidate.IndexStride);
        Assert.Empty(audit.Analyze(Discover(0x5485103e,0x7ca32a14,0x80c50010,0x4e800020),"Object").Candidates);
    }

    [Fact]
    public void ScaledPlayerIndicesRelocateOnlyDeclaredArrayFields()
    {
        // mulli r5,r4,240; add r5,r3,r5; lwz r6,menu.players[0].kart(r5); blr
        var function = Discover(0x1ca400f0, 0x7ca32a14, 0x80c50c20, 0x4e800020);
        var audit = new GuestLayoutAudit(GuestLayoutRelocationTests.RaceTypes(), 12, 24);
        var candidate = Assert.Single(audit.Analyze(function, "Racedata").Candidates);
        Assert.Equal(0xc20, candidate.Before);
        Assert.Equal(0x1760, candidate.After);
        Assert.Equal(240, candidate.IndexStride);
        // Settings are not a player array, despite the same arithmetic.
        Assert.Empty(audit.Analyze(Discover(0x1ca400f0, 0x7ca32a14, 0x80c51758, 0x4e800020), "Racedata").Candidates);
        // A different scale, an interior array cursor, and a clobbered index remain unresolved.
        Assert.Empty(audit.Analyze(Discover(0x1ca400ef, 0x7ca32a14, 0x80c50c20, 0x4e800020), "Racedata").Candidates);
        Assert.Empty(audit.Analyze(Discover(0x38630008, 0x1ca400f0, 0x7ca32a14, 0x80c50c20, 0x4e800020), "Racedata").Candidates);
        Assert.Empty(audit.Analyze(Discover(0x1ca400f0, 0x48000021, 0x7ca32a14, 0x80c50c20, 0x4e800020), "Racedata").Candidates);
    }

    private static GuestLayoutType[] ChangingRecordTypes() => new[] {
        new GuestLayoutType("Byte",1,1),new GuestLayoutType("Word",4,4),new GuestLayoutType("Header",4,8),
        new GuestLayoutType("Entry",4,Fields:new[] {new GuestLayoutField("players","Byte",PerPlayer:true),new GuestLayoutField("tail","Word")}),
        new GuestLayoutType("Owner",4,Fields:new[] {new GuestLayoutField("header","Header"),new GuestLayoutField("entries","Entry",PerPlayer:true)})};

    [Fact]
    public void ChangingRecordStrideAndFollowingFieldAreProvedTogether()
    {
        var types=ChangingRecordTypes();
        var words=new uint[] {0x1ca40008,0x7ca32a14,0x80c5000c,0x80e5000c,0x4e800020};
        var report=new GuestLayoutAudit(types,2,5).Analyze(Discover(words),"Owner");
        Assert.Equal(3,report.Candidates.Count); // The multiply is deduplicated.
        var scale=Assert.Single(report.Candidates.Where(c=>c.Reference.Instruction==Start));
        Assert.Equal("Owner.entries.stride",scale.Reference.Symbol);Assert.Equal(8,scale.Before);Assert.Equal(12,scale.After);
        Assert.All(report.Candidates.Where(c=>c.Reference.Instruction!=Start),c=>{Assert.Equal(12,c.Before);Assert.Equal(16,c.After);});
        var image=TranslatorCppTestHarness.CreateImage(words.Select((word,index)=>(Start+(uint)index*4,word)).ToArray());
        var references=report.Candidates.Select(c=>c.Reference).ToArray();
        Assert.Equal(new FunctionTranslator(image).Translate(Start).CxxCode,
            new FunctionTranslator(image,layoutRelocations:new GuestLayoutRelocations(types,2,2,references)).Translate(Start).CxxCode);
        var nodes=new FunctionTranslator(image,layoutRelocations:new GuestLayoutRelocations(types,2,5,references))
            .Discover(Start,TranslationOptions.Default).LinearIr.Blocks.SelectMany(b=>b.Instructions).ToArray();
        Assert.Contains(nodes,n=>n is IrBinary {Op:"mul",Right.Constant:12});
        Assert.All(nodes.OfType<IrLoad>(),load=>Assert.Equal(16,load.Address.Offset));
    }

    [Fact]
    public void ChangingStridesRequireAnUnambiguousLiteralMultiplyAtADeclaredArray()
    {
        var audit=new GuestLayoutAudit(ChangingRecordTypes(),2,5);
        // Rotated/scaled shifts and a register-held multiplier need separate review.
        Assert.Empty(audit.Analyze(Discover(0x54851838,0x7ca32a14,0x80c5000c,0x4e800020),"Owner").Candidates);
        Assert.Empty(audit.Analyze(Discover(0x38e00008,0x7ca439d6,0x7ca32a14,0x80c5000c,0x4e800020),"Owner").Candidates);
        // Matching arithmetic does not turn a header into an array.
        Assert.Empty(audit.Analyze(Discover(0x1ca40008,0x7ca32a14,0x80c50004,0x4e800020),"Owner").Candidates);
    }

    [Fact]
    public void ASharedMultiplyCannotSilentlyChangeTwoDifferentRecordTypes()
    {
        var types=ChangingRecordTypes().Where(t=>t.Name!="Owner").Concat(new[] {
            new GuestLayoutType("Fixed",4,8),new GuestLayoutType("Owner",4,Fields:new[] {
                new GuestLayoutField("growing","Entry",PerPlayer:true),new GuestLayoutField("fixedRecords","Fixed",2)})}).ToArray();
        var error=Assert.Throws<InvalidDataException>(()=>new GuestLayoutAudit(types,2,5)
            .Analyze(Discover(0x1ca40008,0x7ca32a14,0x80c50004,0x80e50014,0x4e800020),"Owner"));
        Assert.Contains("Shared guest index scale",error.Message);
    }

    [Fact]
    public void DeclaredReceiverFindsAFieldButDoesNotGuessOtherTwelves()
    {
        var function = Discover(0x80830b48, 0x38a0000c, 0x4e800020); // lwz r4,settings(r3); li r5,12; blr
        var audit = new GuestLayoutAudit(GuestLayoutRelocationTests.RaceTypes(), 12, 24);
        var report = audit.Analyze(function, "Scenario");
        var candidate = Assert.Single(report.Candidates);
        Assert.Equal(Start, candidate.Reference.Instruction);
        Assert.Equal(0xb48, candidate.Before);
        Assert.Equal(0x1688, candidate.After);
        Assert.Equal("Scenario.settings.offset", candidate.Reference.Symbol);
        Assert.Empty(audit.Analyze(function).Candidates);
    }

    [Fact]
    public void DeclaredGlobalFindsNestedFieldsAndCarriesTheirCheckedWord()
    {
        var function = Discover(0x3ca08000, 0x80652000, 0x80831758, 0x4e800020);
        var audit = new GuestLayoutAudit(GuestLayoutRelocationTests.RaceTypes(), 12, 24,
            new Dictionary<uint, string> { [0x80002000] = "Racedata" });
        var candidate = Assert.Single(audit.Analyze(function).Candidates);
        Assert.Equal(Start+8, candidate.Reference.Instruction);
        Assert.Equal(0x80831758u, candidate.Reference.ExpectedWord);
        Assert.Equal(0x1758, candidate.Before);
        Assert.Equal(0x2dd8, candidate.After);
    }

    [Fact]
    public void GuestCallsDiscardVolatilePointersButKeepSavedReceivers()
    {
        var audit = new GuestLayoutAudit(GuestLayoutRelocationTests.RaceTypes(), 12, 24);
        Assert.Empty(audit.Analyze(Discover(0x48000021, 0x80830b48, 0x4e800020), "Scenario").Candidates);
        var saved = audit.Analyze(Discover(0x7c7f1b78, 0x48000021, 0x809f0b48, 0x4e800020), "Scenario");
        Assert.Single(saved.Candidates);
    }

    [Fact]
    public void InteriorArrayCursorsRemainUnresolvedWhileSavedReceiversCrossBranches()
    {
        var audit = new GuestLayoutAudit(GuestLayoutRelocationTests.RaceTypes(), 12, 24);
        var cursor = audit.Analyze(Discover(0x38a30008, 0x38a50b40, 0x4e800020), "Scenario");
        Assert.Empty(cursor.Candidates);
        Assert.True(cursor.UnresolvedPointerOperations > 0);
        var branches = audit.Analyze(Discover(0x7c7f1b78, 0x2c040000, 0x4182000c,
            0x807f0b48, 0x48000008, 0x807f0b48, 0x4e800020), "Scenario");
        Assert.Equal(2, branches.Candidates.Count);
    }

    [Fact]
    public void ADisagreeingMergeOrLoopBackEdgeInvalidatesSavedReceiverKnowledge()
    {
        var audit = new GuestLayoutAudit(GuestLayoutRelocationTests.RaceTypes(), 12, 24);
        var merge = Discover(0x7c7f1b78, 0x2c040000, 0x4182000c, 0x7cbf2b78,
            0x48000008, 0x60000000, 0x80df0b48, 0x4e800020);
        Assert.Empty(audit.Analyze(merge, "Scenario").Candidates);
        var loop = Discover(0x7c7f1b78, 0x2c040000, 0x41820010, 0x80df0b48,
            0x7cbf2b78, 0x4bfffff0, 0x4e800020);
        Assert.Empty(audit.Analyze(loop, "Scenario").Candidates);
        var stableLoop = Discover(0x7c7f1b78, 0x2c040000, 0x41820010, 0x80df0b48,
            0x3884ffff, 0x4bfffff0, 0x4e800020);
        Assert.Single(audit.Analyze(stableLoop, "Scenario").Candidates);
    }

    [Fact]
    public void ConflictingGlobalAddressConstantsCannotInventAPointerAtAJoin()
    {
        var audit = new GuestLayoutAudit(GuestLayoutRelocationTests.RaceTypes(), 12, 24,
            new Dictionary<uint, string> { [0x80002000] = "Racedata" });
        var function = Discover(0x3ca08000, 0x2c040000, 0x4182000c, 0x3ca09000,
            0x48000008, 0x60000000, 0x80652000, 0x80831758, 0x4e800020);
        Assert.Empty(audit.Analyze(function).Candidates);
    }

    [Fact]
    public void UndeclaredAlignmentPaddingRemainsUnresolved()
    {
        var types = new[]
        {
            new GuestLayoutType("Byte", 1, 1), new GuestLayoutType("Word", 4, 4),
            new GuestLayoutType("Padded", 4, Fields: new[]
            { new GuestLayoutField("flag", "Byte"), new GuestLayoutField("value", "Word") })
        };
        var audit = new GuestLayoutAudit(types, 12, 24).Analyze(Discover(0x88830001, 0x4e800020), "Padded");
        Assert.Empty(audit.Candidates);
        Assert.Equal(1, audit.UnresolvedPointerOperations);
    }

    [Fact]
    public void UnsupportedInstructionsInvalidatePointerKnowledge()
    {
        var image = TranslatorCppTestHarness.CreateImage((Start, 0x04000000u), (Start+4, 0x80830b48u),
            (Start+8, 0x4e800020u));
        var function = new FunctionTranslator(image).Discover(Start,
            new TranslationOptions(AllowUnsupportedInstructions: true, EnableLeafInlining: false));
        Assert.Contains(function.LinearIr.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction is Translator.Core.Ir.IrUndefined);
        var result = new GuestLayoutAudit(GuestLayoutRelocationTests.RaceTypes(), 12, 24)
            .Analyze(function, "Scenario");
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void UnmodeledHostHelpersCannotPreservePointerFactsByTheirNamePrefix()
    {
        var function = Discover(0x48000021, 0x80830b48, 0x4e800020);
        var blocks = function.LinearIr.Blocks.Select(block => block with
        {
            Instructions = block.Instructions.Select(instruction => instruction is IrCall call
                ? call with { Target = "PPC_Unmodeled" } : instruction).ToArray()
        }).ToArray();
        var result = new GuestLayoutAudit(GuestLayoutRelocationTests.RaceTypes(), 12, 24).Analyze(
            function with { LinearIr = function.LinearIr with { Blocks = blocks } }, "Scenario");
        Assert.Empty(result.Candidates);
        var saved = Discover(0x7c7f1b78, 0x48000021, 0x809f0b48, 0x4e800020);
        var savedBlocks = saved.LinearIr.Blocks.Select(block => block with {
            Instructions = block.Instructions.Select(instruction => instruction is IrCall call
                ? call with { Target = "PPC_Unmodeled" } : instruction).ToArray()
        }).ToArray();
        Assert.Empty(new GuestLayoutAudit(GuestLayoutRelocationTests.RaceTypes(), 12, 24).Analyze(
            saved with { LinearIr = saved.LinearIr with { Blocks = savedBlocks } }, "Scenario").Candidates);
    }
}
