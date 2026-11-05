using Translator.Core.Loading;
using Translator.Core.Translation;
using Xunit;

namespace Translator.Tests;

public class RacedataRankTableTests
{
    [Theory]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(100)]
    [Trait("Requires", "USA game assets")]
    public void ExpandedRankPolicyPreservesEveryLegacyRowAndStrictlyOrdersEveryExpandedRank(int capacity)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "translator/Translator.sln")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing");
        var image = new ProgramImageBuilder().Build(Path.Combine(root, "Assets/main.dol"),
            Path.Combine(root, "Assets/StaticR.rel"), 0x8050bf60);
        var layout = GuestLayoutRelocations.Read(Path.Combine(root, "projects/mkwii-ntsc-u/layouts/racedata.json"), capacity);
        layout.ValidateImage(image);
        var original = image.GetOffset(0x8088bbc0, 144);
        for (var players = 1; players <= 12; players++)
            for (var rank = 1; rank <= players; rank++)
                Assert.Equal((uint)image.Memory[original+(players-1)*12+rank-1], layout.RankPoints(0, players, rank));
        for (var players = 13; players <= capacity; players++)
        {
            var previous = layout.RankPoints(0, players, 1);
            Assert.Equal((uint)((15*players+6)/12), previous);
            for (var rank = 2; rank <= players; rank++)
            {
                var current = layout.RankPoints(0, players, rank);
                Assert.True(previous > current, $"{players} players: ranks {rank-1}/{rank} tie or reverse ({previous}/{current})");
                previous = current;
            }
            Assert.Equal(0u, previous);
        }
    }
}
