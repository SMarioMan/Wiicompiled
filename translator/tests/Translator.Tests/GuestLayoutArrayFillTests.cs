using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class GuestLayoutArrayFillTests
{
    private const uint Start = 0x80001000;
    private static readonly uint[] Words = {0x90030008, 0x90030010, 0x9003000c, 0x90030014};
    private static readonly GuestLayoutType[] Types = {
        new("Header", 4, 8), new("Word", 4, 4),
        new("Object", 4, Fields: new[] {new GuestLayoutField("header", "Header"),
            new GuestLayoutField("first", "Word", PerPlayer:true), new GuestLayoutField("second", "Word", PerPlayer:true),
            new GuestLayoutField("tail", "Word")})};
    private static GuestArrayFill Fill(IReadOnlyList<uint>? words = null) => new(Start, words ?? Words, "r3", "r0", new[] {
        new GuestFillArray("Object.first.offset", "Object.first.stride", "Object.first.count", 4),
        new GuestFillArray("Object.second.offset", "Object.second.stride", "Object.second.count", 4)});
    private static ProgramImage Image(IReadOnlyList<uint>? words = null) => TranslatorCppTestHarness.CreateImage(
        (words ?? Words).Append(0x4e800020u).Select((word,index)=>(Start+(uint)index*4,word)).ToArray());
    private static GuestLayoutRelocations Layout(int players, GuestArrayFill? fill = null,
        GuestLayoutReference[]? references = null) => new(Types, 2, players, references ?? Array.Empty<GuestLayoutReference>(),
            arrayFills:new[] {fill ?? Fill()});

    [Fact]
    public void ManifestReadersRequireVersionFiveForFillSemantics()
    {
        var path=Path.GetTempFileName();
        try
        {
            foreach(var version in new[] {4,5,6,7,8,9,10,11,12})
            {
                File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(new {
                    Version=version,OriginalPlayers=2,Types,References=Array.Empty<object>(),
                    ArrayFills=new[] {new {Begin="0x80001000",ExpectedWords=Words.Select(word=>$"0x{word:X8}"),
                        BaseRegister="r3",ValueRegister="r0",Arrays=Fill().Arrays}}
                }));
                if(version is 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12) Assert.Equal(3,GuestLayoutRelocations.Read(path,3).Players);
                else Assert.Throws<InvalidDataException>(()=>GuestLayoutRelocations.Read(path,3));
            }
        }
        finally {File.Delete(path);}
    }

    [Fact]
    public void ExactUnrolledStoresBecomeUniformExpandedArraysAndPreserveOriginalCode()
    {
        var image = Image();
        var baseline = new FunctionTranslator(image).Translate(Start, TranslationOptions.Default).CxxCode;
        Assert.Equal(baseline, new FunctionTranslator(image, layoutRelocations:Layout(2)).Translate(Start, TranslationOptions.Default).CxxCode);
        var result = new FunctionTranslator(image, layoutRelocations:Layout(3)).Discover(Start, TranslationOptions.Default);
        var stores = result.LinearIr.Blocks.SelectMany(block=>block.Instructions).OfType<IrStore>().ToArray();
        Assert.Equal(new[] {8,20,12,24,16,28}, stores.Select(store=>store.Address.Offset));
        Assert.All(stores,store=> {Assert.Equal("r3",store.Address.Base);Assert.Equal("r0",store.Source.RegisterName);Assert.Equal(4,store.SizeBytes);});
    }

    [Fact]
    public void DeclaredInterleavedAddressSetupMovesWithItsArrayAndRejectsAliasing()
    {
        var words=new uint[] {Words[0],0x38a30010,Words[1],Words[2],Words[3]}; // addi r5,r3,16
        var fill=Fill(words) with {Addresses=new[] {new GuestFillAddress("r5","Object.second.offset")}};
        var baseline=new FunctionTranslator(Image(words)).Translate(Start,TranslationOptions.Default).CxxCode;
        Assert.Equal(baseline,new FunctionTranslator(Image(words),layoutRelocations:Layout(2,fill)).Translate(Start,TranslationOptions.Default).CxxCode);
        var nodes=new FunctionTranslator(Image(words),layoutRelocations:Layout(3,fill)).Discover(Start,TranslationOptions.Default)
            .LinearIr.Blocks.SelectMany(block=>block.Instructions).ToArray();
        var address=Assert.Single(nodes.OfType<IrBinary>());
        Assert.Equal(new IrBinary("r5",IrValue.Register("r3"),IrValue.Imm(20),"add"),address);
        Assert.Equal(6,nodes.OfType<IrStore>().Count());
        foreach(var register in new[] {"r3","r0","r1","r14"})
            Assert.Throws<InvalidDataException>(()=>Layout(3,fill with {Addresses=new[] {new GuestFillAddress(register,"Object.second.offset")}}));
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(words),layoutRelocations:Layout(3,Fill(words))));
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(words),layoutRelocations:Layout(3,
            fill with {Addresses=new[] {new GuestFillAddress("r5","Object.first.offset")}})));
    }

    [Fact]
    public void AddressSetupRequiresVersionSeven()
    {
        var path=Path.GetTempFileName();
        try
        {
            foreach(var version in new[] {6,7,8,9,10,11,12})
            {
                File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(new {
                    Version=version,OriginalPlayers=2,Types,References=Array.Empty<object>(),
                    ArrayFills=new[] {new {Begin="0x80001000",ExpectedWords=Words.Select(word=>$"0x{word:X8}"),
                        BaseRegister="r3",ValueRegister="r0",Arrays=Fill().Arrays,
                        Addresses=new[] {new GuestFillAddress("r5","Object.second.offset")}}}
                }));
                if(version is 7 or 8 or 9 or 10 or 11 or 12)Assert.Equal(3,GuestLayoutRelocations.Read(path,3).Players);
                else Assert.Throws<InvalidDataException>(()=>GuestLayoutRelocations.Read(path,3));
            }
        }
        finally {File.Delete(path);}
    }

    [Fact]
    public void DeclaredConstantSetupPreservesItsValueWithoutAliasingFillInputs()
    {
        var words=new uint[] {Words[0],0x3ca0809c,Words[1],Words[2],Words[3]};
        var fill=Fill(words) with {Constants=new[] {new GuestFillConstant("r5",0x809c0000)}};
        var image=Image(words);
        Assert.Equal(new FunctionTranslator(image).Translate(Start,TranslationOptions.Default).CxxCode,
            new FunctionTranslator(image,layoutRelocations:Layout(2,fill)).Translate(Start,TranslationOptions.Default).CxxCode);
        var nodes=new FunctionTranslator(image,layoutRelocations:Layout(3,fill)).Discover(Start,TranslationOptions.Default)
            .LinearIr.Blocks.SelectMany(block=>block.Instructions).ToArray();
        Assert.Contains(nodes,node=>node is IrAssign {Destination:"r5",Value.Constant:-2137260032});
        Assert.Equal(6,nodes.OfType<IrStore>().Count());
        foreach(var register in new[] {"r0","r3","r14","f5"})
            Assert.Throws<InvalidDataException>(()=>Layout(3,fill with {Constants=new[] {new GuestFillConstant(register,0x809c0000)}}));
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(image,layoutRelocations:Layout(3,
            fill with {Constants=new[] {new GuestFillConstant("r5",0x809d0000)}})));
        Assert.Throws<InvalidDataException>(()=>Layout(3,fill with {Addresses=new[] {new GuestFillAddress("r5","Object.first.offset")}}));
    }

    [Fact]
    public void ConstantSetupRequiresVersionTen()
    {
        var path=Path.GetTempFileName();
        try
        {
            foreach(var version in new[] {9,10,11,12})
            {
                File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(new {Version=version,OriginalPlayers=2,Types,
                    References=Array.Empty<object>(),ArrayFills=new[] {new {Begin="0x80001000",ExpectedWords=Words.Select(word=>$"0x{word:X8}"),
                        BaseRegister="r3",ValueRegister="r0",Arrays=Fill().Arrays,Constants=new[] {new GuestFillConstant("r5",0x809c0000)}}}}));
                if(version is 10 or 11 or 12)Assert.Equal(3,GuestLayoutRelocations.Read(path,3).Players);
                else Assert.Throws<InvalidDataException>(()=>GuestLayoutRelocations.Read(path,3));
            }
        }
        finally{File.Delete(path);}
    }

    [Fact]
    public void InterleavedBaseCopySurvivesExpandedStoresWithoutChangingTheBase()
    {
        var words=new uint[] {Words[0],0x7c781b78,Words[1],Words[2],Words[3]}; // mr r24,r3
        var fill=Fill(words) with {Copies=new[] {new GuestFillCopy("r24","r3")}};
        var image=Image(words);
        Assert.Equal(new FunctionTranslator(image).Translate(Start,TranslationOptions.Default).CxxCode,
            new FunctionTranslator(image,layoutRelocations:Layout(2,fill)).Translate(Start,TranslationOptions.Default).CxxCode);
        var nodes=new FunctionTranslator(image,layoutRelocations:Layout(24,fill)).Discover(Start,TranslationOptions.Default)
            .LinearIr.Blocks.SelectMany(block=>block.Instructions).ToArray();
        Assert.Equal(48,nodes.OfType<IrStore>().Count());
        Assert.Contains(new IrAssign("r24",IrValue.Register("r3")),nodes);
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(image,layoutRelocations:Layout(24,Fill(words))));
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(image,layoutRelocations:Layout(24,
            fill with {Copies=new[] {new GuestFillCopy("r25","r3")}})));
        foreach(var register in new[] {"r0","r1","r2","r3","f5"})
            Assert.Throws<InvalidDataException>(()=>Layout(24,fill with {Copies=new[] {new GuestFillCopy(register,"r3")}}));
        Assert.Throws<InvalidDataException>(()=>Layout(24,fill with {Copies=new[] {new GuestFillCopy("r24","r4")}}));
        Assert.Throws<InvalidDataException>(()=>Layout(24,fill with {Constants=new[] {new GuestFillConstant("r5",1)},
            Copies=new[] {new GuestFillCopy("r5","r3")}}));
    }

    [Fact]
    public void BaseCopySetupRequiresVersionTwelve()
    {
        var path=Path.GetTempFileName();
        try
        {
            foreach(var version in new[] {11,12})
            {
                File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(new {Version=version,OriginalPlayers=2,Types,
                    References=Array.Empty<object>(),ArrayFills=new[] {new {Begin="0x80001000",ExpectedWords=Words.Select(word=>$"0x{word:X8}"),
                        BaseRegister="r3",ValueRegister="r0",Arrays=Fill().Arrays,Copies=new[] {new GuestFillCopy("r24","r3")}}}}));
                if(version==12) Assert.Equal(24,GuestLayoutRelocations.Read(path,24).Players);
                else Assert.Throws<InvalidDataException>(()=>GuestLayoutRelocations.Read(path,24));
            }
        }
        finally{File.Delete(path);}
    }

    [Fact]
    public void ExtraEffectsWrongSourceOrderAndMismatchingWordsAreRejected()
    {
        foreach(var word in new uint[] {0x48000021, 0x38630004, 0x90830008, 0x9003000c})
        {
            var words=Words.ToArray(); words[0]=word;
            Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(words),layoutRelocations:Layout(3,Fill(words))));
        }
        var changed=Words.ToArray();changed[0]^=4;
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(changed),layoutRelocations:Layout(3)));
    }

    [Fact]
    public void SpeculativeLeafDecodeOfPartialFillKeepsTheOrdinaryCall()
    {
        const uint caller=Start-0x100;
        var image=TranslatorCppTestHarness.CreateImage(
            new[] {(caller,0x48000101u),(caller+4,0x4e800020u)}.Concat(
                Words.Append(0x4e800020u).Select((word,index)=>(Start+(uint)index*4,word))).ToArray());
        var translator=new FunctionTranslator(image,layoutRelocations:Layout(3));
        var options=TranslationOptions.Default with {LeafInliningMaxCalleeInstructions=2};
        var result=translator.Discover(caller,options);
        Assert.Contains(result.LinearIr.Blocks.SelectMany(block=>block.Instructions),
            instruction=>instruction is IrCall {Target:"0x80001000"});
        // Actual translation still requires the entire verified span.
        Assert.Throws<InvalidDataException>(()=>translator.Discover(Start+4,options));
        Assert.NotEmpty(translator.Translate(Start,options).CxxCode);
    }

    [Fact]
    public void BranchIntoTheMiddlePartialFunctionsOverlapAndShrinkAreRejected()
    {
        var image=TranslatorCppTestHarness.CreateImage((Start-4,0x4800000cu), (Start,Words[0]),
            (Start+4,Words[1]),(Start+8,Words[2]),(Start+12,Words[3]),(Start+16,0x4e800020u));
        var translator=new FunctionTranslator(image,layoutRelocations:Layout(3));
        Assert.Throws<InvalidDataException>(()=>translator.Discover(Start-4,TranslationOptions.Default));
        Assert.Throws<InvalidDataException>(()=>translator.Discover(Start+4,TranslationOptions.Default));
        Assert.Throws<InvalidDataException>(()=>Layout(1));
        Assert.Throws<InvalidDataException>(()=>Layout(3,references:new[] {new GuestLayoutReference(Start,Words[0],0,"offset","Object.first.offset")}));
        Assert.Throws<InvalidDataException>(()=>Layout(3,Fill() with {Arrays=new[] {Fill().Arrays[0],Fill().Arrays[0]}}));
    }
}
