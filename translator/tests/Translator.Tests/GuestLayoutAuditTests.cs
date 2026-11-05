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
