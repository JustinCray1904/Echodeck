using System.Linq;
using EchoDeck;
using Xunit;

namespace EchoDeck.Tests;

public class LyricsParserTests
{
    [Theory]
    [InlineData("[00:12.34]Hello", 12340)]     // hundredths
    [InlineData("[01:02.345]Hello", 62345)]    // thousandths
    [InlineData("[00:01.5]Hello", 1500)]       // tenths
    [InlineData("[02:00]Hello", 120000)]       // no fraction
    [InlineData("[00:05:25]Hello", 5250)]      // a colon before the hundredths
    [InlineData("[100:00.00]Hello", 6000000)]  // three-digit minutes
    public void Timestamps_ParseToMilliseconds(string line, long expectedMs)
    {
        var doc = LyricsParser.Parse(line);

        Assert.True(doc.Synced);
        var single = Assert.Single(doc.Lines);
        Assert.Equal(expectedMs, single.TimeMs);
        Assert.Equal("Hello", single.Text);
    }

    [Fact]
    public void SeveralTagsOnOneLine_GiveOneLinePerTag_InTimeOrder()
    {
        var doc = LyricsParser.Parse("[00:20.00][00:10.00]Chorus");

        Assert.Equal(new long[] { 10000, 20000 }, doc.Lines.Select(l => l.TimeMs));
        Assert.All(doc.Lines, l => Assert.Equal("Chorus", l.Text));
    }

    [Fact]
    public void Lines_AreSortedByTime_AndStayInOrderWhenTimesAreEqual()
    {
        var doc = LyricsParser.Parse("[00:30.00]third\n[00:10.00]first\n[00:10.00]second");

        Assert.Equal(new[] { "first", "second", "third" }, doc.Lines.Select(l => l.Text));
    }

    [Fact]
    public void OffsetTag_MovesEveryLine_AndNeverGoesBelowZero()
    {
        var shifted = LyricsParser.Parse("[offset:500]\n[00:10.00]Line");
        Assert.Equal(9500, Assert.Single(shifted.Lines).TimeMs);

        var clamped = LyricsParser.Parse("[offset:5000]\n[00:01.00]Line");
        Assert.Equal(0, Assert.Single(clamped.Lines).TimeMs);
    }

    [Fact]
    public void MetadataTags_AreNotLyrics()
    {
        var doc = LyricsParser.Parse("[ar:Artist]\n[ti:Title]\n[al:Album]\n[00:01.00]Hi");

        Assert.Equal("Hi", Assert.Single(doc.Lines).Text);
    }

    [Fact]
    public void WordTimestamps_AreRemovedFromTheText()
    {
        var doc = LyricsParser.Parse("[00:01.00]<00:01.00>Hello <00:01.50>world");

        Assert.Equal("Hello world", Assert.Single(doc.Lines).Text);
    }

    [Fact]
    public void WindowsLineEndings_AreHandled()
    {
        var doc = LyricsParser.Parse("[00:01.00]a\r\n[00:02.00]b\r\n");

        Assert.Equal(2, doc.Lines.Count);
    }

    [Fact]
    public void PlainText_IsNotSynced_AndBlankEdgesAreTrimmed()
    {
        var doc = LyricsParser.Parse("\n\nfirst\n\nsecond\n\n");

        Assert.False(doc.Synced);
        Assert.Equal(new[] { "first", "", "second" }, doc.Lines.Select(l => l.Text));
        Assert.All(doc.Lines, l => Assert.True(l.IsPlain));
        Assert.All(doc.Lines, l => Assert.Equal(-1, l.TimeMs));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n \n")]
    public void EmptyText_GivesAnEmptyDocument(string text)
    {
        Assert.True(LyricsParser.Parse(text).IsEmpty);
    }

    [Fact]
    public void HasTimestamps_RecognisesTimedText()
    {
        Assert.True(LyricsParser.HasTimestamps("[00:01.00]x"));
        Assert.False(LyricsParser.HasTimestamps("just words"));
        Assert.False(LyricsParser.HasTimestamps("[ar:Someone]"));
    }

    [Theory]
    [InlineData(62345, "[01:02.34]")]
    [InlineData(0, "[00:00.00]")]
    [InlineData(-5, "[00:00.00]")]
    [InlineData(3599990, "[59:59.99]")]
    public void FormatTime_UsesMinutesSecondsAndHundredths(long ms, string expected)
    {
        Assert.Equal(expected, LyricsParser.FormatTime(ms));
    }

    [Fact]
    public void Shift_MovesTimestamps_AndStopsAtZero()
    {
        Assert.Equal("[00:11.50]a", LyricsParser.Shift("[00:10.00]a", 1500));
        Assert.Equal("[00:00.00]a", LyricsParser.Shift("[00:01.00]a", -5000));
    }

    [Fact]
    public void StripTimes_LeavesPlainLyrics()
    {
        Assert.Equal("one\ntwo", LyricsParser.StripTimes("[00:01.00]one\n[00:02.00]two"));
    }

    [Fact]
    public void StampLine_ReplacesAnExistingTimestamp()
    {
        Assert.Equal("[00:05.00]words", LyricsParser.StampLine("[00:01.00]words", 5000));
        Assert.Equal("[00:05.00]words", LyricsParser.StampLine("words", 5000));
    }

    [Fact]
    public void FormatThenParse_RoundTrips()
    {
        var text = LyricsParser.FormatTime(83450) + "line";

        Assert.Equal(83450, Assert.Single(LyricsParser.Parse(text).Lines).TimeMs);
    }
}
