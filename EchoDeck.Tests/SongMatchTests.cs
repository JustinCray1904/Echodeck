using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EchoDeck;
using Xunit;

namespace EchoDeck.Tests;

public class SongMatchTests
{
    private static string? Best(string artist, string title, double seconds, string raw, params SongCandidate[] entries)
    {
        var q = new SongQuery(artist, title, seconds, raw);
        SongCandidate? best = null;
        double bestScore = double.MinValue;
        foreach (var e in entries)
            if (SongMatcher.TryScore(q, e, out double s) && s > bestScore) { best = e; bestScore = s; }
        return best == null ? null : best.Title + " / " + best.Artist;
    }

    private static SongCandidate C(string title, string artist, double seconds, int rank = 0, string album = "x") =>
        new(title, artist, album, seconds, null, rank);

    [Fact]
    public void FindsTheSong_WhateverTheFileIsCalled()
    {
        Assert.Equal("Flex (feat. Juice WRLD) / Polo G",
            Best("Polo G", "Flex", 160, "yt1z.net - Polo G - Flex (Official Audio) ft. Juice WRLD (320 KBps)",
                C("Flex (feat. Juice WRLD)", "Polo G", 164), C("Flex", "Other Guy", 160, 1), C("Flex (Karaoke Version)", "Karaoke Hits", 160, 2)));
    }

    [Fact]
    public void ALongerTitleIsNotTakenForAShorterOne()
    {
        Assert.Equal("Love Story / Taylor Swift",
            Best("Taylor Swift", "Love Story", 235, "Taylor Swift - Love Story", C("Love", "Taylor Swift", 200), C("Love Story", "Taylor Swift", 235, 1)));
    }

    [Fact]
    public void KeepsAVersionInBrackets_WhenTheFileHasIt()
    {
        Assert.Equal("Love Story (Taylor's Version) / Taylor Swift",
            Best("Taylor Swift", "Love Story (Taylor's Version)", 235, "Taylor Swift - Love Story (Taylor's Version)",
                C("Love Story (Taylor's Version)", "Taylor Swift", 235)));
    }

    [Fact]
    public void AnotherArtistsSongWithTheSameTitleIsRejected()
    {
        Assert.Null(Best("Drake", "Flex", 160, "Drake - Flex", C("Flex", "Polo G", 160)));
        Assert.Null(Best("Drake", "Intro", 120, "Drake - Intro", C("Intro", "The xx", 120)));
    }

    [Fact]
    public void WithoutAnArtist_TheLengthHasToVouchForTheSong()
    {
        Assert.Equal("Last Last / Burna Boy", Best("", "Last Last", 172, "Last Last", C("Last Last", "Burna Boy", 173), C("Last Last", "Cover Band", 300, 1)));
        Assert.Null(Best("", "Flex", 160, "Flex", C("Flex", "Polo G", 250)));
    }

    [Fact]
    public void ArtistFirstOrTitleFirst_BothWork()
    {
        Assert.Equal("Flex / Polo G", Best("Flex", "Polo G", 160, "Flex - Polo G", C("Flex", "Polo G", 160)));
    }

    [Fact]
    public void AccentsTyposCollaborationsAndFeaturesAreForgiven()
    {
        Assert.Equal("Halo / Beyoncé", Best("Beyonce", "Halo", 261, "Beyonce - Halo", C("Halo", "Beyoncé", 261)));
        Assert.Equal("Lucid Dreams / Juice WRLD", Best("Juice WRLD", "Lucid Dreamz", 239, "Juice WRLD - Lucid Dreamz", C("Lucid Dreams", "Juice WRLD", 239)));
        Assert.Equal("Jumpman / Drake & Future", Best("Drake", "Jumpman", 205, "Drake - Jumpman", C("Jumpman", "Drake & Future", 205)));
        Assert.Equal("Essence (feat. Tems) / Wizkid", Best("Wizkid", "Essence", 248, "Wizkid - Essence ft. Tems", C("Essence (feat. Tems)", "Wizkid", 248)));
    }

    [Fact]
    public void KaraokeAndTributeVersionsAreRejected_UnlessTheFileIsOne()
    {
        Assert.Null(Best("Polo G", "Flex", 160, "Polo G - Flex", C("Flex (Karaoke Version)", "Polo G", 160)));
        Assert.Equal("Flex (Remix) / Polo G", Best("Polo G", "Flex (Remix)", 160, "Polo G - Flex (Remix)", C("Flex (Remix)", "Polo G", 160)));
    }
}

public class SongTextTests
{
    private static TagInfo Tags(string title = "", string artist = "", string album = "", long seconds = 0) => new(title, artist, album, "", seconds);

    [Fact]
    public void Local_TakesArtistAndTitleFromTheTidiedFileName()
    {
        var (artist, title) = SongText.Local(@"C:\music\yt1z.net - Polo G - Flex (Official Audio) ft. Juice WRLD (320 KBps).mp3", Tags());
        Assert.Equal("Polo G", artist);
        Assert.Equal("Flex", title);
    }

    [Fact]
    public void Local_DropsTheArtistRepeatedInsideTheTitleTag()
    {
        var (artist, title) = SongText.Local(@"C:\music\x.mp3", Tags("Polo G - Flex Official Music Video", "Polo G"));
        Assert.Equal("Polo G", artist);
        Assert.Equal("Flex", title);
    }

    [Fact]
    public void Local_IgnoresTrackNumbersAndTopicChannels()
    {
        var (artist, title) = SongText.Local(@"C:\music\03 - Flex.mp3", Tags("", "Polo G - Topic"));
        Assert.Equal("Polo G", artist);
        Assert.Equal("Flex", title);
    }

    [Theory]
    [InlineData("Unknown Artist")]
    [InlineData("<unknown>")]
    [InlineData("Various Artists")]
    public void CleanArtist_DropsPlaceholders(string artist)
    {
        Assert.Equal("", SongText.CleanArtist(artist));
    }

    [Fact]
    public void Tokens_IgnoreCaseAccentsApostrophesAndFillerWords()
    {
        Assert.Equal(new[] { "beyonce", "halo" }, SongText.Tokens("The Beyoncé - Halo").ToArray());
        Assert.Equal(new[] { "taylors", "version" }, SongText.Tokens("Taylor's Version").ToArray());
    }

    [Fact]
    public void AFileThatNamesItsArtistIsNeverSearchedForByTitleAlone()
    {
        // "Girlfriend" alone matches every song of that name of about the same length: a compilation's cover for a Juice WRLD song
        var tags = new TagInfo("", "", "", "", 204);
        var (artist, title) = SongText.Local(@"C:\Music\Juice WRLD - Girlfriend.mp3", tags);
        var variants = SongResolver.Variants(@"C:\Music\Juice WRLD - Girlfriend.mp3", tags, artist, title);

        Assert.Equal("Juice WRLD", artist);
        Assert.NotEmpty(variants);
        Assert.All(variants, v => Assert.Equal("Juice WRLD", v.Artist));
    }

    [Fact]
    public void AFileWithOnlyATagArtistStillTriesTheTitleAlone()
    {
        var tags = new TagInfo("Flex", "Some Uploader", "", "", 160);
        var variants = SongResolver.Variants(@"C:\Music\Flex.mp3", tags, "Some Uploader", "Flex");

        Assert.Contains(variants, v => v.Artist.Length == 0);
    }
}
