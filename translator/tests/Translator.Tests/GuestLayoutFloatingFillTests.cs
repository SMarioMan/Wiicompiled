using System.Text.Json;
using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class GuestLayoutFloatingFillTests
{
    private const uint Start=0x80001000;
    private static readonly uint[] Words={0xd0030010,0xd003000c,0xd0030008,0xd003001c,0xd0030018,0xd0030014};
    private static readonly GuestLayoutType[] Types={new("Header",4,8),new("Float",4,4),
        new("Vector",4,Fields:new[] {new GuestLayoutField("x","Float"),new GuestLayoutField("y","Float"),new GuestLayoutField("z","Float")}),
        new("Object",4,Fields:new[] {new GuestLayoutField("header","Header"),new GuestLayoutField("positions","Vector",PerPlayer:true)})};
    private static GuestArrayFill Fill()=>new(Start,Words,"r3","f0",new[] {"z","y","x"}.Select(field=>
        new GuestFillArray("Object.positions.offset+Vector."+field+".offset","Object.positions.stride","Object.positions.count",4)).ToArray());
    private static GuestLayoutRelocations Layout(int players,GuestArrayFill? fill=null)=>new(Types,2,players,Array.Empty<GuestLayoutReference>(),arrayFills:new[] {fill??Fill()});
    private static ProgramImage Image()=>TranslatorCppTestHarness.CreateImage(Words.Append(0x4e800020u).Select((word,index)=>(Start+(uint)index*4,word)).ToArray());

    [Fact]
    public void InterleavedFloatingCoordinatesExpandAsOneUniformVectorArray()
    {
        var image=Image();
        Assert.Equal(new FunctionTranslator(image).Translate(Start,TranslationOptions.Default).CxxCode,
            new FunctionTranslator(image,layoutRelocations:Layout(2)).Translate(Start,TranslationOptions.Default).CxxCode);
        var nodes=new FunctionTranslator(image,layoutRelocations:Layout(3)).Discover(Start,TranslationOptions.Default)
            .LinearIr.Blocks.SelectMany(block=>block.Instructions).OfType<IrStore>().ToArray();
        Assert.Equal(new[] {16,12,8,28,24,20,40,36,32},nodes.Select(node=>node.Address.Offset));
        Assert.All(nodes,node=>{Assert.Equal("f0",node.Source.RegisterName);Assert.Equal(4,node.SizeBytes);});
        Assert.Throws<InvalidDataException>(()=>Layout(3,Fill() with {ValueRegister="f32"}));
        Assert.Throws<InvalidDataException>(()=>Layout(3,Fill() with {Arrays=new[] {Fill().Arrays[0],Fill().Arrays[0]}}));
        Assert.Throws<InvalidDataException>(()=>Layout(3,Fill() with {Arrays=new[] {Fill().Arrays[0] with {Width=8}}}));
    }

    [Fact]
    public void ReadersRequireVersionNineForFloatingOrStridedStores()
    {
        var path=Path.GetTempFileName();
        try
        {
            foreach(var version in new[] {8,9,10,11,12})
            foreach(var valueRegister in new[] {"f0","r0"})
            {
                File.WriteAllText(path,JsonSerializer.Serialize(new {Version=version,OriginalPlayers=2,Types,
                    References=Array.Empty<object>(),ArrayFills=new[] {new {Begin="0x80001000",
                        ExpectedWords=Words.Select(word=>$"0x{word:X8}"),BaseRegister="r3",ValueRegister=valueRegister,Arrays=Fill().Arrays}}}));
                if(version is 9 or 10 or 11 or 12)Assert.Equal(3,GuestLayoutRelocations.Read(path,3).Players);
                else Assert.Throws<InvalidDataException>(()=>GuestLayoutRelocations.Read(path,3));
            }
        }
        finally{File.Delete(path);}
    }
}
