using System.Text.Json;
using Translator.Core.Ir;
using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class GuestPackedTransferTests
{
    private const uint Start=0x80001000;
    private static readonly GuestLayoutType[] Types={new("Header",4,80),new("Word",4,4),new("Byte",1,1),
        new("Record",4,Fields:new[] {new GuestLayoutField("header","Header"),new GuestLayoutField("teams","Word",PerPlayer:true,PlayersPerElement:12),new GuestLayoutField("tail","Byte")}),
        new("FrameHeader",4,8),new("Gap",4,4),new("Rng",4,16),new("Spills",4,16),new("Frame",16,Fields:new[] {
            new GuestLayoutField("header","FrameHeader"),new GuestLayoutField("teams","Word",PerPlayer:true,PlayersPerElement:12),
            new GuestLayoutField("gap","Gap"),new GuestLayoutField("rng","Rng"),new GuestLayoutField("spills","Spills")})};
    private static readonly uint[] CopyWords={0x80010008,0x38610010,0x901f0050};
    private static GuestBitsetLoad Load()=>new(Start,0x80830050,0,"r3","r0","r4","Record.teams.offset","Record.teams.count",24,4,2);
    private static GuestArrayTransfer Copy()=>new(Start,CopyWords,"r1","Frame.teams.offset","r31","Record.teams.offset","r0","Word.size","Record.teams.count");
    private static Translator.Core.Loading.ProgramImage Image(params uint[] words)=>TranslatorCppTestHarness.CreateImage(
        words.Append(0x4e800020u).Select((word,index)=>(Start+(uint)index*4,word)).ToArray());
    private static void Execute(IEnumerable<IrInstruction> nodes,Dictionary<string,uint> registers,Dictionary<uint,uint> memory)
    {
        uint Value(IrValue value)=>value.RegisterName is {} register?registers[register]:unchecked((uint)value.Constant!.Value);
        uint Address(IrAddress address)=>registers[address.Base]+unchecked((uint)address.Offset);
        foreach(var node in nodes)switch(node){
            case IrLoad load:Assert.Equal(4,load.SizeBytes);registers[load.Destination]=memory[Address(load.Address)];break;
            case IrStore store:Assert.Equal(4,store.SizeBytes);memory[Address(store.Address)]=Value(store.Source);break;
            case IrBinary binary:
                var a=Value(binary.Left);var b=Value(binary.Right);
                registers[binary.Destination]=binary.Op switch {"add"=>unchecked(a+b),"sub"=>unchecked(a-b),"mul"=>unchecked(a*b),"divu"=>a/b,"or"=>a|b,"shl"=>a<<(int)b,"ppc_slw"=>(b&63)>=32?0:a<<(int)(b&31),
                    _=>throw new InvalidOperationException(binary.Op)};break;
            case IrAssign assign:registers[assign.Destination]=Value(assign.Value);break;
            case IrComment or IrReturn or IrTracePpc:break;
            default:throw new InvalidOperationException(node.ToString());
        }
    }

    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(48)] [InlineData(100)]
    public void PackedLoadsReadTheContainingWordAndNormalizeOnlyTheBitIndex(int players)
    {
        var image=Image(0x80830050);var plan=new GuestLayoutRelocations(Types,12,players,Array.Empty<GuestLayoutReference>(),bitsetLoads:new[] {Load()});
        var translator=new FunctionTranslator(image,layoutRelocations:plan);
        if(players==12)Assert.Equal(new FunctionTranslator(image).Translate(Start,TranslationOptions.Default).CxxCode,translator.Translate(Start,TranslationOptions.Default).CxxCode);
        var nodes=translator.Discover(Start,TranslationOptions.Default).LinearIr.Blocks.SelectMany(block=>block.Instructions).ToArray();
        var memory=Enumerable.Range(0,(players+11)/12).ToDictionary(i=>0x1050u+(uint)i*4,i=>0xa5000100u+(uint)i);
        var before=new Dictionary<uint,uint>(memory);
        for(var index=0;index<players*2;index++){
            var registers=new Dictionary<string,uint>{{"r3",0x1000},{"r0",(uint)index},{"r4",17},{"r5",0x1234}};
            Execute(nodes,registers,memory);
            Assert.Equal(memory[0x1050+(uint)(index/24)*4],registers["r4"]);Assert.Equal((uint)(index%24),registers["r0"]);
            Assert.Equal(0x1000u,registers["r3"]);Assert.Equal(0x1234u,registers["r5"]);
        }
        Assert.Equal(before.OrderBy(pair=>pair.Key),memory.OrderBy(pair=>pair.Key));
        Assert.NotEmpty(translator.Translate(Start,TranslationOptions.Default).CxxCode);
    }

    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(48)] [InlineData(100)]
    public void StackArrayTransfersCopyAllWordsAndRetainRelocatedAddressSetup(int players)
    {
        var image=Image(CopyWords);var layouts=new GuestLayouts(Types,players);
        var reference=new GuestLayoutReference(Start+4,CopyWords[1],0,"right","Frame.rng.offset");
        var plan=new GuestLayoutRelocations(Types,12,players,new[] {reference},arrayTransfers:new[] {Copy()});
        var translator=new FunctionTranslator(image,layoutRelocations:plan);
        if(players==12)Assert.Equal(new FunctionTranslator(image).Translate(Start,TranslationOptions.Default).CxxCode,translator.Translate(Start,TranslationOptions.Default).CxxCode);
        var count=(players+11)/12;
        var memory=new Dictionary<uint,uint>{{0x1004,0xfedcba98},{0x1008+(uint)count*4,0x87654321},{0x204c,0x12345678},{0x2050+(uint)count*4,0x10203040}};
        for(var i=0;i<count;i++){memory[0x1008+(uint)i*4]=0xa5010000+(uint)i;memory[0x2050+(uint)i*4]=0x55555555;}
        var expected=new Dictionary<uint,uint>(memory);for(var i=0;i<count;i++)expected[0x2050+(uint)i*4]=memory[0x1008+(uint)i*4];
        var registers=new Dictionary<string,uint>{{"r1",0x1000},{"r31",0x2000},{"r0",0},{"r3",0}};
        Execute(translator.Discover(Start,TranslationOptions.Default).LinearIr.Blocks.SelectMany(block=>block.Instructions),registers,memory);
        Assert.Equal(expected.OrderBy(pair=>pair.Key),memory.OrderBy(pair=>pair.Key));
        Assert.Equal(0xa5010000u,registers["r0"]);Assert.Equal((uint)(0x1000+layouts.Symbol("Frame.rng.offset")),registers["r3"]);
        Assert.Equal(0x1000u,registers["r1"]);Assert.Equal(0x2000u,registers["r31"]);
        Assert.NotEmpty(translator.Translate(Start,TranslationOptions.Default).CxxCode);
    }

    [Fact]
    public void PackedStoresCanUseAnIndependentAuditedShiftInput()
    {
        var words=new uint[]{0x80e30050,0x7d406030,0x38a50001,0x7ce00378,0x90030050};
        var site=new GuestBitsetStore(Start,words,"r3","r12","r10","r0","r7","Record.teams.offset","Record.teams.count",24,4,2,"r12");
        var image=Image(words);var plan=new GuestLayoutRelocations(Types,12,24,Array.Empty<GuestLayoutReference>(),bitsetStores:new[] {site});
        var nodes=new FunctionTranslator(image,layoutRelocations:plan).Discover(Start,TranslationOptions.Default).LinearIr.Blocks.SelectMany(block=>block.Instructions).ToArray();
        for(var bit=0;bit<48;bit++){
            var registers=new Dictionary<string,uint>{{"r3",0x1000},{"r12",(uint)bit},{"r10",1},{"r0",0x9999},{"r5",17}};
            var memory=new Dictionary<uint,uint>{{0x1050,0xa5000000},{0x1054,0x12000000}};
            Execute(nodes,registers,memory);
            Assert.Equal(bit<24?0xa5000000u|(1u<<bit):0xa5000000u,memory[0x1050]);
            Assert.Equal(bit>=24?0x12000000u|(1u<<(bit-24)):0x12000000u,memory[0x1054]);
            Assert.Equal(0xa5000000u|(bit<32?1u<<bit:0),registers["r0"]);Assert.Equal(18u,registers["r5"]);
        }
        Assert.Throws<InvalidDataException>(()=>new GuestLayoutRelocations(Types,12,24,Array.Empty<GuestLayoutReference>(),bitsetStores:new[] {site with {ShiftInput="r5"}}));
        var path=Path.GetTempFileName();
        try{foreach(var version in new[] {21,22,23}){
            File.WriteAllText(path,JsonSerializer.Serialize(new {Version=version,OriginalPlayers=12,Types,References=Array.Empty<object>(),
                BitsetStores=new[] {new {Begin=$"0x{Start:X8}",ExpectedWords=words.Select(word=>$"0x{word:X8}"),site.Base,site.Index,site.Unit,
                    site.Shift,site.Loaded,site.Offset,site.Count,site.WordBits,site.WordBytes,site.BitsPerPlayer,site.ShiftInput}}}));
            if(version is 22 or 23 or 24 or 25)Assert.Equal(24,GuestLayoutRelocations.Read(path,24).Players);else Assert.Throws<InvalidDataException>(()=>GuestLayoutRelocations.Read(path,24));
        }}finally{File.Delete(path);}
    }

    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(48)] [InlineData(100)]
    public void PostShiftStoresRetainScalarEffectsAndRecoverTheOriginalBit(int players)
    {
        var words=new uint[]{0x80e30050,0x7d406030,0x38c00000,0x398c0001,0x38a50001,0x7ce00378,0x90030050};
        var site=new GuestBitsetStore(Start,words,"r3","r12","r10","r0","r7","Record.teams.offset","Record.teams.count",24,4,2,"r12",-1,true);
        var image=Image(words);var plan=new GuestLayoutRelocations(Types,12,players,Array.Empty<GuestLayoutReference>(),bitsetStores:new[] {site});
        var translator=new FunctionTranslator(image,layoutRelocations:plan);
        if(players==12)Assert.Equal(new FunctionTranslator(image).Translate(Start,TranslationOptions.Default).CxxCode,translator.Translate(Start,TranslationOptions.Default).CxxCode);
        var nodes=translator.Discover(Start,TranslationOptions.Default).LinearIr.Blocks.SelectMany(block=>block.Instructions).ToArray();
        for(var bit=0;bit<players*2;bit++){
            var registers=new Dictionary<string,uint>{{"r3",0x1000},{"r12",(uint)bit},{"r10",1},{"r0",0x9999},{"r5",17},{"r6",18}};
            var memory=Enumerable.Range(0,(players+11)/12).ToDictionary(i=>0x1050u+(uint)i*4,i=>0xa5000000u+(uint)i*256);
            var expected=new Dictionary<uint,uint>(memory);expected[0x1050+(uint)(bit/24)*4]|=1u<<(bit%24);
            Execute(nodes,registers,memory);
            Assert.Equal(expected.OrderBy(pair=>pair.Key),memory.OrderBy(pair=>pair.Key));
            Assert.Equal((uint)bit+1,registers["r12"]);Assert.Equal(18u,registers["r5"]);Assert.Equal(0u,registers["r6"]);
        }
        foreach(var invalid in new[] {site with {IndexBias=0},site with {IndexBias=-2},site with {AllowScalarAuxiliaries=false}})
            Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(image,layoutRelocations:new GuestLayoutRelocations(Types,12,players,Array.Empty<GuestLayoutReference>(),bitsetStores:new[] {invalid})));
    }

    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(24)] [InlineData(48)] [InlineData(100)]
    public void ObjectArrayTransfersSnapshotOverlappingSourceWords(int players)
    {
        var words=new uint[]{0x80040008,0x901f0050};var site=Copy() with {ExpectedWords=words,SourceBase="r4",AllowObjectSource=true};
        var image=Image(words);var translator=new FunctionTranslator(image,layoutRelocations:new GuestLayoutRelocations(Types,12,players,Array.Empty<GuestLayoutReference>(),arrayTransfers:new[] {site}));
        if(players==12)Assert.Equal(new FunctionTranslator(image).Translate(Start,TranslationOptions.Default).CxxCode,translator.Translate(Start,TranslationOptions.Default).CxxCode);
        var count=(players+11)/12;
        var memory=Enumerable.Range(0,count+3).ToDictionary(i=>0x1004u+(uint)i*4,i=>0xa5000000u+(uint)i);
        var before=new Dictionary<uint,uint>(memory);var expected=new Dictionary<uint,uint>(memory);
        for(var i=0;i<count;i++)expected[0x100c+(uint)i*4]=before[0x1008+(uint)i*4];
        var registers=new Dictionary<string,uint>{{"r4",0x1000},{"r31",0xfbc},{"r0",0}};
        Execute(translator.Discover(Start,TranslationOptions.Default).LinearIr.Blocks.SelectMany(block=>block.Instructions),registers,memory);
        Assert.Equal(expected.OrderBy(pair=>pair.Key),memory.OrderBy(pair=>pair.Key));
        Assert.Equal(before[0x1008],registers["r0"]);Assert.Equal(0x1000u,registers["r4"]);Assert.Equal(0xfbcu,registers["r31"]);
    }

    [Fact]
    public void ExtendedWriterPatternsRequireFormatTwentyThree()
    {
        var words=new uint[]{0x80e30050,0x7d406030,0x38c00000,0x398c0001,0x7ce00378,0x90030050};
        var path=Path.GetTempFileName();
        try{foreach(var version in new[] {22,23,24,25,26})foreach(var store in new[] {false,true}){
            File.WriteAllText(path,JsonSerializer.Serialize(new {Version=version,OriginalPlayers=12,Types,References=Array.Empty<object>(),
                BitsetStores=store?new[] {new {Begin=$"0x{Start:X8}",ExpectedWords=words.Select(word=>$"0x{word:X8}"),Base="r3",Index="r12",Unit="r10",Shift="r0",Loaded="r7",Offset="Record.teams.offset",Count="Record.teams.count",WordBits=24,WordBytes=4,BitsPerPlayer=2,ShiftInput="r12",IndexBias=-1,AllowScalarAuxiliaries=true}}:null,
                ArrayTransfers=store?null:new[] {new {Begin=$"0x{Start:X8}",ExpectedWords=new[] {"0x80040008","0x901f0050"},SourceBase="r4",SourceOffset="Frame.teams.offset",DestinationBase="r31",DestinationOffset="Record.teams.offset",Value="r0",Stride="Word.size",Count="Record.teams.count",AllowObjectSource=true}}}));
            if(version is 23 or 24 or 25)Assert.Equal(24,GuestLayoutRelocations.Read(path,24).Players);else Assert.Throws<InvalidDataException>(()=>GuestLayoutRelocations.Read(path,24));
        }}finally{File.Delete(path);}
    }

    [Fact]
    public void IncorrectShapesEffectsOverlapsAndInteriorEntriesAreRejected()
    {
        GuestLayoutRelocations Loading(GuestBitsetLoad site)=>new(Types,12,24,Array.Empty<GuestLayoutReference>(),bitsetLoads:new[] {site});
        foreach(var site in new[] {Load() with {Index="r4"},Load() with {Index="r3"},Load() with {WordBits=0},Load() with {WordBytes=1},Load() with {BitsPerPlayer=3},Load() with {Node=1}})
            Assert.Throws<InvalidDataException>(()=>Loading(site));
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(0x80830054),layoutRelocations:Loading(Load())));
        Assert.Throws<InvalidDataException>(()=>new GuestLayoutRelocations(Types,12,24,new[] {new GuestLayoutReference(Start,0x80830050,0,"offset","Record.teams.offset")},bitsetLoads:new[] {Load()}));
        GuestLayoutRelocations Copying(GuestArrayTransfer site)=>new(Types,12,24,Array.Empty<GuestLayoutReference>(),arrayTransfers:new[] {site});
        foreach(var site in new[] {Copy() with {SourceBase="r3"},Copy() with {DestinationBase="r1"},Copy() with {Value="r31"},Copy() with {Count="players"},Copy() with {Stride="Byte.size"}})
            Assert.Throws<InvalidDataException>(()=>Copying(site));
        foreach(var middle in new uint[]{0x38010010,0x3be10010,0x80610010}){
            var words=CopyWords.ToArray();words[1]=middle;
            Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(words),layoutRelocations:Copying(Copy() with {ExpectedWords=words})));
        }
        Assert.Throws<InvalidDataException>(()=>new GuestLayoutRelocations(Types,12,24,new[] {new GuestLayoutReference(Start+8,CopyWords[2],0,"offset","Record.teams.offset")},arrayTransfers:new[] {Copy()}));
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(Image(CopyWords),layoutRelocations:Copying(Copy())).Discover(Start+4,TranslationOptions.Default));
        var branch=TranslatorCppTestHarness.CreateImage(new[] {(Start-4,0x48000008u)}.Concat(CopyWords.Append(0x4e800020u).Select((word,index)=>(Start+(uint)index*4,word))).ToArray());
        Assert.Throws<InvalidDataException>(()=>new FunctionTranslator(branch,layoutRelocations:Copying(Copy())).Discover(Start-4,TranslationOptions.Default));
    }

    [Fact]
    public void PackedLoadsAndArrayTransfersRequireFormatTwentyTwo()
    {
        var path=Path.GetTempFileName();
        try{foreach(var version in new[] {21,22,23,24,25,26})foreach(var load in new[] {false,true}){
            File.WriteAllText(path,JsonSerializer.Serialize(new {Version=version,OriginalPlayers=12,Types,References=Array.Empty<object>(),
                BitsetLoads=load?new[] {new {Instruction=$"0x{Start:X8}",ExpectedWord="0x80830050",Node=0,Base="r3",Index="r0",Destination="r4",Offset="Record.teams.offset",Count="Record.teams.count",WordBits=24,WordBytes=4,BitsPerPlayer=2}}:null,
                ArrayTransfers=load?null:new[] {new {Begin=$"0x{Start:X8}",ExpectedWords=CopyWords.Select(word=>$"0x{word:X8}"),SourceBase="r1",SourceOffset="Frame.teams.offset",DestinationBase="r31",DestinationOffset="Record.teams.offset",Value="r0",Stride="Word.size",Count="Record.teams.count",Width=4}}}));
            if(version is 22 or 23 or 24 or 25)Assert.Equal(24,GuestLayoutRelocations.Read(path,24).Players);else Assert.Throws<InvalidDataException>(()=>GuestLayoutRelocations.Read(path,24));
        }}finally{File.Delete(path);}
    }
}
