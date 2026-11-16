using EchoDeck;
using Xunit;

namespace EchoDeck.Tests;

public class MetadataIdentityTests
{
    [Theory]
    [InlineData("yt1z.net - Polo G - Flex (Official Audio) [320 KBps]", "Polo G - Flex")]
    [InlineData("Juice WRLD - Lucid Dreams Official Music Video", "Juice WRLD - Lucid Dreams")]
    [InlineData("Artist - Song Visualizer", "Artist - Song")]
    [InlineData("Artist - Song | Official Audio", "Artist - Song")]
    public void Tidy_RemovesDownloaderAndVideoDecorations(string input, string expected)
    {
        Assert.Equal(expected, DisplayNames.Tidy(input));
    }

    [Fact]
    public void Identity_UsesFilenameArtistWhenTagsOnlyContainMatchingTitle()
    {
        var tags = new TagInfo("Flex", "", "", "", 190);
        var identity = OnlineLyrics.Identity(@"C:\Music\Polo G - Flex (Official Audio).mp3", tags);
        Assert.Equal("Polo G", identity.Artist);
        Assert.Equal("Flex", identity.Title);
    }

    [Fact]
    public void Identity_PrefersCompleteTaggedArtistAndTitle()
    {
        var tags = new TagInfo("Lucid Dreams (Official Audio)", "Juice WRLD", "Goodbye & Good Riddance", "", 240);
        var identity = OnlineLyrics.Identity(@"C:\Music\Random Filename.mp3", tags);
        Assert.Equal("Juice WRLD", identity.Artist);
        Assert.Equal("Lucid Dreams", identity.Title);
    }
    [Fact]
    public void LyricsPick_RejectsDifferentArtistEvenWhenDurationIsExact()
    {
        var hits = new[]
        {
            new OnlineLyrics.Hit("Stay", "Wrong Artist", 180, "[00:01]wrong", "", false),
            new OnlineLyrics.Hit("Stay", "Right Artist", 184, "[00:01]right", "", false)
        };
        var picked = OnlineLyrics.Pick(hits, "Stay", "Right Artist", 180, out double gap);
        Assert.NotNull(picked);
        Assert.Equal("Right Artist", picked!.Artist);
        Assert.Equal(4, gap);
    }

    [Fact]
    public void LyricsPick_PrioritizesClosestDurationOverSyncedPreference()
    {
        var hits = new[]
        {
            new OnlineLyrics.Hit("Stay", "Artist", 184, "[00:01]synced", "", false),
            new OnlineLyrics.Hit("Stay", "Artist", 181, "", "plain", false)
        };
        var picked = OnlineLyrics.Pick(hits, "Stay", "Artist", 180, out double gap);
        Assert.NotNull(picked);
        Assert.Equal(181, picked!.Duration);
        Assert.Equal(1, gap);
    }

    [Fact]
    public void LyricsPick_RejectsSimilarButDifferentTitleEvenWhenDurationMatches()
    {
        var hits = new[]
        {
            new OnlineLyrics.Hit("Stay With Me", "Artist", 180, "[00:01]wrong", "", false)
        };
        var picked = OnlineLyrics.Pick(hits, "Stay", "Artist", 180, out _);
        Assert.Null(picked);
    }

}
