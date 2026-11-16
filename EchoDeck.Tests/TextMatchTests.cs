using EchoDeck;
using Xunit;

namespace EchoDeck.Tests;

public class TextMatchTests
{
    [Theory]
    [InlineData("Flex", "Flex (Official Audio)")]
    [InlineData("Flex", "flex")]
    [InlineData("Flex ft. Juice WRLD", "Flex")]
    [InlineData("Don't Stop", "Dont Stop")]
    [InlineData("Lose Myself", "Lose Myself [prod. Bisenas]")]
    public void SameTitle_IgnoresDecorations(string a, string b)
    {
        Assert.True(TextMatch.SameTitle(a, b));
    }

    [Theory]
    [InlineData("Flex", "Flexible")]               // a longer, different title that merely contains it
    [InlineData("Stay", "Stay With Me")]
    [InlineData("Ashes", "Burning Memories")]
    [InlineData("", "Flex")]
    public void SameTitle_RejectsDifferentSongs(string a, string b)
    {
        Assert.False(TextMatch.SameTitle(a, b));
    }

    [Fact]
    public void SameTitle_ForgivesATypo_OnlyInLongerTitles()
    {
        Assert.True(TextMatch.SameTitle("Lucid Dreams", "Lucid Dreamz"));
        Assert.False(TextMatch.SameTitle("Hide", "Hive"));
    }

    [Theory]
    [InlineData("Juice WRLD", "Juice WRLD & Young Thug")]
    [InlineData("Polo G", "Polo G ft. Juice WRLD")]
    [InlineData("juicewrld", "Juice WRLD")]
    [InlineData("", "Anyone")]                      // unknown wanted artist matches anything
    public void SameArtist_AcceptsCreditVariations(string wanted, string found)
    {
        Assert.True(TextMatch.SameArtist(wanted, found));
    }

    [Fact]
    public void SameArtist_RejectsAnotherArtist()
    {
        Assert.False(TextMatch.SameArtist("Juice WRLD", "Trippie Redd"));
        Assert.False(TextMatch.SameArtist("Juice WRLD", ""));
    }

    [Theory]
    [InlineData("Flex ft. Juice WRLD", "Flex")]
    [InlineData("Flex (feat. Juice WRLD)", "Flex")]
    [InlineData("Flex featuring Juice WRLD", "Flex")]
    [InlineData("Soft Landing", "Soft Landing")]    // "ft" inside a word is not a featuring credit
    public void StripFeature_RemovesTheFeaturedArtist(string title, string expected)
    {
        Assert.Equal(expected, TextMatch.StripFeature(title));
    }

    [Fact]
    public void Similarity_IsOneForEqualTexts_AndLowForDifferentOnes()
    {
        Assert.Equal(1.0, TextMatch.Similarity("abc", "abc"));
        Assert.True(TextMatch.Similarity("abcdef", "uvwxyz") < 0.2);
    }
}
