using System.Text.Json;
using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class GuestPointerChainTests
{
    private const uint Start=0x80001000,Word=0x90c100a4;
    private static readonly GuestLayoutType[] Types={new("Header",4,32),new("Node",4,12),new("Word",4,4),
        new("Frame",4,Fields:new[] {new GuestLayoutField("header","Header"),new GuestLayoutField("nodes","Node",PerPlayer:true),new GuestLayoutField("tail","Word")})};
    private static GuestArrayFill Fill(GuestFillArray? array=null)=>new(Start,new[] {Word},"r1","r6",new[] {
        array??new GuestFillArray("Frame.nodes.offset","Frame.nodes.stride","Frame.nodes.count",4,StartIndex:11,LinkToNext:true)});
    private static GuestLayoutRelocations Plan(int players,GuestArrayFill? fill=null)=>new(Types,12,players,Array.Empty<GuestLayoutReference>(),arrayFills:new[] {fill??Fill()});
    private static ProgramImage Image(uint word=Word)=>TranslatorCppTestHarness.CreateImage((Start,word),(Start+4,0x4e800020u));

    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(48)] [InlineData(100)]
    public void TerminalFillExtendsOneUniformPointerChainAndRetainsNodeData(int players)
    {
        var image=Image();var translator=new FunctionTranslator(image,layoutRelocations:Plan(players));
        if(players==12)Assert.Equal(new FunctionTranslator(image).Translate(Start,TranslationOptions.Default).CxxCode,translator.Translate(Start,TranslationOptions.Default).CxxCode);
        var nodes=translator.Discover(Start,TranslationOptions.Default).LinearIr.Blocks.SelectMany(block=>block.Instructions).ToArray();
        foreach(var terminal in new[] {0u,0xdeadbeefu}){
            var registers=new Dictionary<string,uint>{{"r1",0x1000},{"r6",terminal}};
            var memory=new Dictionary<uint,uint>{{0x101c,0x12345678},{0x1020+(uint)players*12,0x87654321}};
            for(var i=0;i<players;i++){
                var at=0x1020u+(uint)i*12;memory[at]=i<11?at+12:0x55555555;memory[at+4]=0xaabbccdd;memory[at+8]=0x11223344;
            }
            uint Value(IrValue value)=>value.RegisterName is {} register?registers[register]:unchecked((uint)value.Constant!.Value);
            foreach(var node in nodes)switch(node){
                case IrBinary {Op:"add"} add:registers[add.Destination]=unchecked(Value(add.Left)+Value(add.Right));break;
                case IrStore store:Assert.Equal(4,store.SizeBytes);memory[registers[store.Address.Base]+unchecked((uint)store.Address.Offset)]=Value(store.Source);break;
                case IrComment or IrReturn or IrTracePpc:break;
                default:throw new InvalidOperationException(node.ToString());
            }
            uint current=0x1020;
            for(var i=0;i<players;i++){
                Assert.Equal(0x1020u+(uint)i*12,current);Assert.Equal(0xaabbccddu,memory[current+4]);Assert.Equal(0x11223344u,memory[current+8]);current=memory[current];
            }
            Assert.Equal(terminal,current);Assert.Equal(0x12345678u,memory[0x101c]);Assert.Equal(0x87654321u,memory[0x1020+(uint)players*12]);
            Assert.Equal(0x1000u,registers["r1"]);Assert.Equal(terminal,registers["r6"]);
        }
        Assert.NotEmpty(translator.Translate(Start,TranslationOptions.Default).CxxCode);
    }

    [Fact]
    public void NonterminalShapesWrongEffectsAndOlderFormatsAreRejected()
    {
        var array=Fill().Arrays[0];
        foreach(var bad in new[] {array with {Width=2},array with {StartIndex=10},array with {ValueRegister="f0"}})
            Assert.Throws<InvalidDataException>(()=>Plan(24,Fill(bad)));
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(Word^0x200000),layoutRelocations:Plan(24)));
        var path=Path.GetTempFileName();
        try{foreach(var version in new[] {23,24,25,26}){
            File.WriteAllText(path,JsonSerializer.Serialize(new {Version=version,OriginalPlayers=12,Types,References=Array.Empty<object>(),
                ArrayFills=new[] {new {Begin=$"0x{Start:X8}",ExpectedWords=new[] {$"0x{Word:X8}"},BaseRegister="r1",ValueRegister="r6",Arrays=Fill().Arrays}}}));
            if(version is 24 or 25)Assert.Equal(24,GuestLayoutRelocations.Read(path,24).Players);else Assert.Throws<InvalidDataException>(()=>GuestLayoutRelocations.Read(path,24));
        }}finally{File.Delete(path);}
    }
}
