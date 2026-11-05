using Translator.Core.Disassembly;
using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class GuestLayoutRelocationTests
{
    // ABI facts checked against the USA Racedata constructors, rather than host C++ layouts.
    internal static GuestLayoutType[] RaceTypes() => new[]
    {
        new GuestLayoutType("Header", 4, 0x20),
        new GuestLayoutType("ScenarioHeader", 4, 8),
        new GuestLayoutType("Player", 4, 0xf0),
        new GuestLayoutType("Settings", 4, 0x34),
        new GuestLayoutType("Mission", 4, 0x70),
        new GuestLayoutType("Pointer", 4, 4),
        new GuestLayoutType("Ghost", 4, 0x2800),
        new GuestLayoutType("Scenario", 4, Fields: new[]
        {
            new GuestLayoutField("header", "ScenarioHeader"),
            new GuestLayoutField("players", "Player", PerPlayer: true),
            new GuestLayoutField("settings", "Settings"),
            new GuestLayoutField("mission", "Mission"),
            new GuestLayoutField("ghost", "Pointer")
        }),
        new GuestLayoutType("Racedata", 4, Fields: new[]
        {
            new GuestLayoutField("header", "Header"),
            new GuestLayoutField("scenarios", "Scenario", 3),
            new GuestLayoutField("ghosts", "Ghost", 2)
        })
    };

    [Theory]
    [InlineData(12, 0xb48, 0xbf0, 0x23f0, 0x73f0)]
    [InlineData(24, 0x1688, 0x1730, 0x45b0, 0x95b0)]
    [InlineData(48, 0x2d08, 0x2db0, 0x8930, 0xd930)]
    public void CapacityMovesFollowingFieldsAndAllThreeScenarios(int players, int settings,
        int scenarioSize, int ghosts, int total)
    {
        var layout = new GuestLayouts(RaceTypes(), players);
        Assert.Equal(settings, layout.Symbol("Scenario.settings.offset"));
        Assert.Equal(scenarioSize, layout.Symbol("Scenario.size"));
        Assert.Equal(ghosts, layout.Symbol("Racedata.ghosts.offset"));
        Assert.Equal(total, layout.Symbol("Racedata.size"));
        Assert.Equal(players, layout.Symbol("Scenario.players.count"));
        Assert.Equal(0xf0, layout.Symbol("Scenario.players.stride"));
        Assert.Equal(settings, layout.Symbol("Scenario.players.end"));
    }

    [Fact]
    public void InvalidOrRecursiveLayoutsFailBeforeTranslation()
    {
        Assert.Throws<InvalidDataException>(() => new GuestLayouts(new[] { new GuestLayoutType("Bad", 3, 12) }, 24));
        Assert.Throws<InvalidDataException>(() => new GuestLayouts(new[]
        {
            new GuestLayoutType("Cycle", 4, Fields: new[] { new GuestLayoutField("self", "Cycle") })
        }, 24));
        Assert.Throws<InvalidDataException>(() => new GuestLayouts(RaceTypes(), 24).Symbol("Scenario.typo.offset"));
    }

    [Fact]
    public void ChangedOriginalInstructionOrOperandCannotSilentlyRelocate()
    {
        const uint pc = 0x80001000;
        var relocations = new GuestLayoutRelocations(RaceTypes(), 12, 24, new[]
        {
            new GuestLayoutReference(pc, 0x386073f0, 0, "value", "Racedata.size")
        });
        Assert.Throws<InvalidDataException>(() => relocations.ValidateImage(
            TranslatorCppTestHarness.CreateImage((pc, 0x386073f1))));
        var origin = PpcInstruction.Synthetic(pc, 0x386073f0, "li", Array.Empty<PpcOperand>());
        Assert.Throws<InvalidDataException>(() => relocations.Apply(origin,
            new IrInstruction[] { new IrAssign("r3", IrValue.Imm(0x73f1)) }));
    }

    [Fact]
    public void LoadsStoresAndLoopBoundsUseTheExpandedLayout()
    {
        const uint pc = 0x80001000;
        var image = TranslatorCppTestHarness.CreateImage(
            (pc, 0x90830b48),      // stw r4, Scenario.settings(r3)
            (pc+4, 0x80630b48),    // lwz r3, Scenario.settings(r3)
            (pc+8, 0x2c04000c),    // cmpwi r4, original capacity
            (pc+12, 0x4e800020));
        var relocations = new GuestLayoutRelocations(RaceTypes(), 12, 24, new[]
        {
            new GuestLayoutReference(pc, 0x90830b48, 0, "offset", "Scenario.settings.offset"),
            new GuestLayoutReference(pc+4, 0x80630b48, 0, "offset", "Scenario.settings.offset"),
            new GuestLayoutReference(pc+8, 0x2c04000c, 0, "right", "players")
        });
        var translated = new FunctionTranslator(image, layoutRelocations: relocations).Discover(pc);
        var instructions = translated.LinearIr.Blocks.SelectMany(b => b.Instructions).ToArray();
        Assert.Equal(0x1688, Assert.Single(instructions.OfType<IrLoad>()).Address.Offset);
        Assert.Equal(0x1688, Assert.Single(instructions.OfType<IrStore>()).Address.Offset);
        Assert.Equal(24, Assert.Single(instructions.OfType<IrSetCrField>()).Right.Constant);
    }

    [Fact]
    public void SignedFrameRelocationMovesBothStackStoreAndStackPointer()
    {
        const uint pc = 0x80001000;
        var types = new[]
        {
            new GuestLayoutType("Score", 2, 2),
            new GuestLayoutType("Frame", 16, Fields: new[]
            { new GuestLayoutField("scores", "Score", PerPlayer: true) })
        };
        var image = TranslatorCppTestHarness.CreateImage((pc, 0x9421ffe0), (pc+4, 0x4e800020));
        var relocations = new GuestLayoutRelocations(types, 12, 24, new[]
        {
            new GuestLayoutReference(pc, 0x9421ffe0, 0, "offset", "Frame.size", Scale: -1),
            new GuestLayoutReference(pc, 0x9421ffe0, 1, "right", "Frame.size", Scale: -1)
        });
        relocations.ValidateImage(image);
        var translated = new FunctionTranslator(image, layoutRelocations: relocations).Discover(pc);
        var instructions = translated.LinearIr.Blocks.SelectMany(b => b.Instructions).ToArray();
        Assert.Equal(-48, Assert.Single(instructions.OfType<IrStore>()).Address.Offset);
        Assert.Equal(-48, Assert.Single(instructions.OfType<IrBinary>()).Right.Constant);
        Assert.Throws<OverflowException>(() => new GuestLayoutRelocations(types, 12, 24, new[]
        { new GuestLayoutReference(pc, 0x9421ffe0, 0, "offset", "Frame.size", Scale: int.MaxValue) }));
    }

    [Fact]
    public void AddressArithmeticAndItsCarryHelperRelocateTogether()
    {
        const uint pc = 0x80001000;
        const string symbol = "Racedata.scenarios.offset+Scenario.size+Scenario.size";
        var image = TranslatorCppTestHarness.CreateImage((pc, 0x34631800), (pc+4, 0x4e800020));
        var relocations = new GuestLayoutRelocations(RaceTypes(), 12, 24, new[]
        {
            new GuestLayoutReference(pc, 0x34631800, 1, "right", symbol),
            new GuestLayoutReference(pc, 0x34631800, 2, "argument:1", symbol)
        });
        var translated = new FunctionTranslator(image, layoutRelocations: relocations).Discover(pc);
        var instructions = translated.LinearIr.Blocks.SelectMany(b => b.Instructions).ToArray();
        Assert.Equal(0x2e80, Assert.Single(instructions.OfType<IrBinary>()).Right.Constant);
        var carry = Assert.Single(instructions.OfType<IrCall>(), call => call.Target == "PPC_UpdateCarryAdd");
        Assert.Equal(0x2e80, carry.Arguments[1].Constant);
        foreach (var operand in new[] { "argument:-1", "argument:99" })
        {
            var invalid = new GuestLayoutRelocations(RaceTypes(), 12, 24, new[]
            { new GuestLayoutReference(pc, 0x34631800, 2, operand, symbol) });
            Assert.Throws<InvalidDataException>(() => invalid.ValidateImage(image));
        }
    }

    internal static FunctionTranslationResult ExpandedAllocationFunction()
    {
        const uint pc = 0x80001000;
        var image = TranslatorCppTestHarness.CreateImage(
            (pc, 0x386073f0),      // li r3, original sizeof(Racedata)
            (pc+4, 0x2c040000),    // cmpwi r4, 0
            (pc+8, 0x4182000c),    // beq equal
            (pc+12, 0x38630001),   // addi r3, r3, 1
            (pc+16, 0x48000008),   // b end
            (pc+20, 0x38630002),   // equal: addi r3, r3, 2
            (pc+24, 0x4e800020));  // end: blr
        var relocations = new GuestLayoutRelocations(RaceTypes(), 12, 24, new[]
        {
            new GuestLayoutReference(pc, 0x386073f0, 0, "value", "Racedata.size")
        });
        return new FunctionTranslator(image, layoutRelocations: relocations).Translate(pc,
            new TranslationOptions(EnableLeafInlining: false));
    }

    [Fact]
    public void ExpandedAllocationBeyondSignedImmediateRangePreservesBranchPaths()
    {
        var translated = ExpandedAllocationFunction();
        Assert.Contains(translated.LinearIr.Blocks.SelectMany(b => b.Instructions).OfType<IrAssign>(),
            a => a.Value.Constant == 0x95b0);
    }
}
