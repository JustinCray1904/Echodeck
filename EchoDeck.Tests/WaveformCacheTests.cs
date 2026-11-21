using System;
using System.IO;
using System.Linq;
using EchoDeck;
using Xunit;

namespace EchoDeck.Tests;

public sealed class WaveformCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tempo-wf-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _previous = WaveformCache.Dir;

    public WaveformCacheTests()
    {
        Directory.CreateDirectory(_dir);
        WaveformCache.Dir = Path.Combine(_dir, "cache");
    }

    public void Dispose()
    {
        WaveformCache.Dir = _previous;
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp folder */ }
    }

    private string Song(string name = "song.mp3", int size = 1000)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    private static AudioAnalysis Analysis(bool withSpectrum)
    {
        var peaks = Enumerable.Range(0, 300).Select(i => (i % 17) / 17f).ToArray();
        var values = Enumerable.Range(0, SpectrumData.Bands * 90).Select(i => (byte)(i % 251)).ToArray();
        return new AudioAnalysis(peaks, withSpectrum ? new SpectrumData(values, 90) : null);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhatWasStoredComesBackExactly(bool withSpectrum)
    {
        string song = Song();
        var original = Analysis(withSpectrum);

        WaveformCache.Write(song, original);
        var back = WaveformCache.TryRead(song);

        Assert.NotNull(back);
        Assert.Equal(original.Peaks, back!.Peaks);
        if (withSpectrum)
        {
            Assert.Equal(original.Spectrum!.Frames, back.Spectrum!.Frames);
            Assert.Equal(original.Spectrum.Values, back.Spectrum.Values);
        }
        else Assert.Null(back.Spectrum);
    }

    [Fact]
    public void NothingIsFoundForASongThatWasNeverStored()
    {
        Assert.Null(WaveformCache.TryRead(Song()));
    }

    [Fact]
    public void AChangedSongIsNotGivenTheOldWaveform()
    {
        string song = Song();
        WaveformCache.Write(song, Analysis(true));

        File.WriteAllBytes(song, new byte[2000]);   // edited / replaced: a different size

        Assert.Null(WaveformCache.TryRead(song));
    }

    [Fact]
    public void ADamagedEntryIsIgnoredAndRemoved()
    {
        string song = Song();
        WaveformCache.Write(song, Analysis(true));
        string file = Directory.GetFiles(WaveformCache.Dir, "*.twf").Single();
        File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4, 5 });

        Assert.Null(WaveformCache.TryRead(song));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void TheFolderIsKeptSmallAndTheLeastRecentlyUsedGoFirst()
    {
        string oldSong = Song("old.mp3", 10);
        string newSong = Song("new.mp3", 11);
        WaveformCache.Write(oldSong, Analysis(true));
        WaveformCache.Write(newSong, Analysis(true));

        var files = Directory.GetFiles(WaveformCache.Dir, "*.twf").Select(f => new FileInfo(f)).ToList();
        var oldFile = files.First(f => f.Name.StartsWith(WaveformCache.KeyFor(oldSong)!));
        oldFile.LastWriteTimeUtc = DateTime.UtcNow.AddDays(-30);

        WaveformCache.Prune(maxBytes: 1, pruneTo: oldFile.Length + 10);   // room for one of the two

        Assert.Null(WaveformCache.TryRead(oldSong));
        Assert.NotNull(WaveformCache.TryRead(newSong));
    }
}
