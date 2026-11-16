using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EchoDeck;
using Xunit;

namespace EchoDeck.Tests;

public class SmartQueueTests
{
    private static string[] Series(string opened, params string[] folder) => SmartQueue.SeriesOf(folder, opened).ToArray();

    [Fact]
    public void OpeningOneEpisodeQueuesTheSeasonInOrder()
    {
        var result = Series("Show.S01E03.1080p.WEB-DL.x265.mkv",
            "Show.S01E01.1080p.WEB-DL.x265.mkv", "Show.S01E02.1080p.WEB-DL.x265.mkv", "Show.S01E10.1080p.WEB-DL.x265.mkv",
            "Show.S01E03.1080p.WEB-DL.x265.mkv", "Show.S02E01.1080p.WEB-DL.x265.mkv", "Other Movie (2020).mkv");

        Assert.Equal(new[]
        {
            "Show.S01E01.1080p.WEB-DL.x265.mkv", "Show.S01E02.1080p.WEB-DL.x265.mkv",
            "Show.S01E03.1080p.WEB-DL.x265.mkv", "Show.S01E10.1080p.WEB-DL.x265.mkv"
        }, result);   // E10 after E3 (numbers, not text), and no other season
    }

    [Fact]
    public void EpisodeNumbersSortAsNumbers()
    {
        Assert.Equal(new[] { "Naruto Episode 1.mp4", "Naruto Episode 2.mp4", "Naruto Episode 3.mp4", "Naruto Episode 12.mp4" },
            Series("Naruto Episode 1.mp4", "Naruto Episode 1.mp4", "Naruto Episode 2.mp4", "Naruto Episode 12.mp4", "Naruto Episode 3.mp4"));
    }

    [Fact]
    public void ReleaseGroupsAndQualityTagsAreIgnored()
    {
        var all = new[] { "[Group] Anime - 01 [1080p].mkv", "[Other] Anime - 02 [720p].mkv", "[Group] Anime - 03 [1080p].mkv" };
        Assert.Equal(all, Series(all[0], all));
    }

    [Fact]
    public void UnrelatedFilesAreNotQueued()
    {
        Assert.Equal(new[] { "Movie A (2020).mkv" }, Series("Movie A (2020).mkv", "Movie A (2020).mkv", "Movie B (2021).mkv"));
        Assert.Equal(new[] { "Only Movie.mkv" }, Series("Only Movie.mkv", "Only Movie.mkv"));
    }

    [Fact]
    public void CameraFileNumbersAreNotASeries()
    {
        Assert.Equal(new[] { "IMG_1234.mp4" }, Series("IMG_1234.mp4", "IMG_1234.mp4", "IMG_1235.mp4"));
    }

    [Fact]
    public void TheSameEpisodeInTwoFormatsIsQueuedOnce()
    {
        Assert.Equal(new[] { "Show 01.mkv", "Show 02.mp4" }, Series("Show 01.mkv", "Show 01.mp4", "Show 01.mkv", "Show 02.mp4"));
    }

    [Fact]
    public void OtherSeasonsInOneXFormatAreLeftOut()
    {
        Assert.Equal(new[] { "Show 1x01.avi", "Show 1x02.avi" }, Series("Show 1x02.avi", "Show 1x01.avi", "Show 1x02.avi", "Show 2x01.avi"));
    }

    [Fact]
    public void AlbumTracksAreQueuedInOrder()
    {
        Assert.Equal(new[] { "Track 01.mp3", "Track 02.mp3", "Track 10.mp3" },
            Series("Track 02.mp3", "Track 10.mp3", "Track 01.mp3", "Track 02.mp3"));
    }

    [Fact]
    public void ANameWithoutNumbersIsJustItself()
    {
        Assert.Equal(new[] { "Documentary.mkv" }, Series("Documentary.mkv", "Documentary.mkv", "Another.mkv"));
    }
}

public class LyricsLengthCheckTests
{
    [Fact]
    public void LastStampSeconds_FindsTheLastTimedLine()
    {
        Assert.Equal(185.5, OnlineLyrics.LastStampSeconds("[00:01.00] a\n[01:05.20] b\n[03:05.50] c"), 2);
        Assert.Equal(0, OnlineLyrics.LastStampSeconds("just words"));
    }

    [Fact]
    public void TimedLyricsThatRunPastTheSongAreRejected()
    {
        // a 3:00 song; the upload's timed lines go on until 4:40: another song or another cut
        var wrong = new OnlineLyrics.Hit("Flex", "Polo G", 180, "[00:10.00] a\n[04:40.00] z", "", false);
        var right = new OnlineLyrics.Hit("Flex", "Polo G", 181, "[00:10.00] a\n[02:55.00] z", "", false);

        var picked = OnlineLyrics.Pick(new[] { wrong, right }, "Flex", "Polo G", 180, out _);
        Assert.Same(right, picked);
        Assert.Null(OnlineLyrics.Pick(new[] { wrong }, "Flex", "Polo G", 180, out _));
    }
}
