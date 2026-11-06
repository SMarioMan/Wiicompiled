using System.Text.Json;
using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class GuestLayoutHeapRouteTests
{
    private const uint Start=0x80001000;
    private static readonly uint[] Words={0x809f0c8c,0x38c00000,0x801f0c88,0x901f0c94,0x909f0c98};
    private static readonly GuestLayoutType[] Types={new("Byte",1,1)};
    private static GuestHeapRoute Site()=>new(Start,Words,"r31","r0","r4","r6",0xc88,0xc8c,0xc94,0xc98);
    private static GuestLayoutRelocations Layout(int players,GuestHeapRoute? site=null)=>new(Types,12,players,
        Array.Empty<GuestLayoutReference>(),heapRoutes:new[]{site??Site()});
    private static ProgramImage Image(IReadOnlyList<uint>? words=null)=>TranslatorCppTestHarness.CreateImage(
        (words??Words).Append(0x4e800020u).Select((word,index)=>(Start+(uint)index*4,word)).ToArray());

    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(48)] [InlineData(100)]
    public void ExpandedAllocationSlotUsesTheSecondaryHeapWithoutChangingOwnershipOrRegisters(int players)
    {
        var image=Image();var translator=new FunctionTranslator(image,layoutRelocations:Layout(players));
        if(players==12)Assert.Equal(new FunctionTranslator(image).Translate(Start,TranslationOptions.Default).CxxCode,
            translator.Translate(Start,TranslationOptions.Default).CxxCode);
        var nodes=translator.Discover(Start,TranslationOptions.Default).LinearIr.Blocks.SelectMany(block=>block.Instructions).ToArray();
        var memory=new Dictionary<uint,uint>{{0x10c88,0x81010000},{0x10c8c,0x91020000},{0x10c94,0x11111111},{0x10c98,0x22222222}};
        var registers=new Dictionary<string,uint>{{"r31",0x10000},{"r0",3},{"r4",4},{"r6",6}};
        uint Value(IrValue value)=>value.RegisterName is {} register?registers[register]:unchecked((uint)value.Constant!.Value);
        foreach(var node in nodes)
        {
            switch(node)
            {
                case IrLoad load:Assert.Equal(4,load.SizeBytes);registers[load.Destination]=memory[registers[load.Address.Base]+(uint)load.Address.Offset];break;
                case IrAssign assign:registers[assign.Destination]=Value(assign.Value);break;
                case IrStore store:Assert.Equal(4,store.SizeBytes);memory[registers[store.Address.Base]+(uint)store.Address.Offset]=Value(store.Source);break;
                case IrReturn or IrComment or IrTracePpc:break;
                default:throw new InvalidOperationException(node.ToString());
            }
        }
        Assert.Equal(players==12?0x81010000u:0x91020000u,memory[0x10c94]);Assert.Equal(0x91020000u,memory[0x10c98]);
        Assert.Equal(0x81010000u,memory[0x10c88]);Assert.Equal(0x91020000u,memory[0x10c8c]);
        Assert.Equal(0x81010000u,registers["r0"]);Assert.Equal(0x91020000u,registers["r4"]);
        Assert.Equal(0u,registers["r6"]);Assert.Equal(0x10000u,registers["r31"]);
        Assert.NotEmpty(translator.Translate(Start,TranslationOptions.Default).CxxCode);
    }

    [Fact]
    public void MismatchingLoadsAliasedSlotsOverlapsAndInteriorEntriesAreRejected()
    {
        for(var index=0;index<Words.Length;index++)
        {
            var words=Words.ToArray();words[index]^=4;
            Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(words),layoutRelocations:Layout(24)));
            Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(words),layoutRelocations:Layout(24,Site() with {ExpectedWords=words})));
        }
        Assert.Throws<InvalidDataException>(()=>Layout(11));
        Assert.Throws<InvalidDataException>(()=>Layout(24,Site() with {PrimarySlot=0xc88}));
        Assert.Throws<InvalidDataException>(()=>Layout(24,Site() with {PrimaryHeap=0xc89}));
        Assert.Throws<InvalidDataException>(()=>Layout(24,Site() with {PrimaryRegister="r4"}));
        Assert.Throws<InvalidDataException>(()=>Layout(24,Site() with {Scene="r1"}));
        Assert.Throws<InvalidDataException>(()=>new GuestLayoutRelocations(Types,12,24,
            new[]{new GuestLayoutReference(Start,Words[0],0,"offset","Byte.size")},heapRoutes:new[]{Site()}));
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(),layoutRelocations:Layout(24)).Discover(Start+4,TranslationOptions.Default));
        var branch=TranslatorCppTestHarness.CreateImage(new[]{(Start-4,0x4800000cu)}.Concat(
            Words.Append(0x4e800020u).Select((word,index)=>(Start+(uint)index*4,word))).ToArray());
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(branch,layoutRelocations:Layout(24)).Discover(Start-4,TranslationOptions.Default));
    }

    [Fact]
    public void ReadersRequireVersionFourteen()
    {
        var path=Path.GetTempFileName();
        try
        {
            foreach(var version in new[]{13,14,15,16})
            {
                var site=Site();
                File.WriteAllText(path,JsonSerializer.Serialize(new {Version=version,OriginalPlayers=12,Types,References=Array.Empty<object>(),
                    HeapRoutes=new[]{new {Begin="0x80001000",ExpectedWords=Words.Select(word=>$"0x{word:X8}"),site.Scene,
                        site.PrimaryRegister,site.SecondaryRegister,site.ZeroRegister,site.PrimaryHeap,site.SecondaryHeap,site.PrimarySlot,site.SecondarySlot}}}));
                if(version is 14 or 15)Assert.Equal(24,GuestLayoutRelocations.Read(path,24).Players);
                else Assert.Throws<InvalidDataException>(()=>GuestLayoutRelocations.Read(path,24));
            }
        }
        finally{File.Delete(path);}
    }
}
