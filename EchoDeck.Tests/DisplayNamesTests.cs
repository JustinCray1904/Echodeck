using EchoDeck;
using Xunit;

namespace EchoDeck.Tests;

public class DisplayNamesTests
{
    [Theory]
    [InlineData("yt1z.net - Polo G - Flex (Official Audio) ft. Juice WRLD (320 KBps)", "Polo G - Flex ft. Juice WRLD")]
    [InlineData("y2mate.com - Ouse  Her Little Dance w Svar", "Ouse Her Little Dance w Svar")]
    [InlineData("Nemzzz - 4AM | Lyrics [CwfqtPzTzsA]", "Nemzzz - 4AM")]
    [InlineData("AnDee - Come &amp; Go (Lyrics)", "AnDee - Come & Go")]
    [InlineData("Juice WRLD - Lose Myself [prod. Bisenas]", "Juice WRLD - Lose Myself")]
    [InlineData("Artist - Title 320kbps", "Artist - Title")]
    public void Tidy_RemovesSiteNamesAndQualityTags(string raw, string expected)
    {
        Assert.Equal(expected, DisplayNames.Tidy(raw));
    }

    [Theory]
    [InlineData("Zenitsu Uk Drill Kaigaku Diss (Demon Slayer Rap)")]   // brackets that are part of the title stay
    [InlineData("Dr. Dre - Still DRE")]
    [InlineData("Plain Title")]
    public void Tidy_LeavesNormalTitlesAlone(string name)
    {
        Assert.Equal(name, DisplayNames.Tidy(name));
    }

    [Fact]
    public void Tidy_NeverReturnsNothing()
    {
        Assert.Equal("(Official Audio)", DisplayNames.Tidy("(Official Audio)"));
        Assert.Equal("", DisplayNames.Tidy(""));
    }

    [Fact]
    public void CleanFileName_KeepsTheExtension()
    {
        // Clean follows the "Tidy song names" setting (on by default); Tidy is the always-on part
        Assert.EndsWith(".mp3", DisplayNames.CleanFileName("yt1z.net - Polo G - Flex (320 KBps).mp3"));
    }
}
