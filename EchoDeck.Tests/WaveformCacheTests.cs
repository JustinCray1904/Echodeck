using System;
using System.IO;
using System.Linq;
using EchoDeck;
using Xunit;

namespace EchoDeck.Tests;

/// <summary>The saved-waveform cache (WaveformCache): a song is analysed once and then loaded from its small cache file.</summary>
public sealed class WaveformCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tempo-cache-tests-" + Guid.NewGuid().ToString("N"));

    public WaveformCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp folder */ }
    }

    [Fact]
    public void Waveform_round_trips_through_the_cache_file()
    {
        var peaks = Enumerable.Range(0, WaveformLoader.Buckets).Select(i => i / (float)WaveformLoader.Buckets).ToArray();
        var values = Enumerable.Range(0, SpectrumData.Bands * 40).Select(i => (byte)(i % 251)).ToArray();
        var original = new AudioAnalysis(peaks, new SpectrumData(values, 40));
        string file = Path.Combine(_dir, "a.wf");

        WaveformCache.Save(file, original);
        var loaded = WaveformCache.TryLoad(file);

        Assert.NotNull(loaded);
        Assert.Equal(peaks, loaded!.Peaks);
        Assert.NotNull(loaded.Spectrum);
        Assert.Equal(40, loaded.Spectrum!.Frames);
        Assert.Equal(values, loaded.Spectrum.Values);
    }

    [Fact]
    public void Waveform_without_spectrum_round_trips()
    {
        var peaks = new float[WaveformLoader.Buckets];
        string file = Path.Combine(_dir, "b.wf");

        WaveformCache.Save(file, new AudioAnalysis(peaks, null));
        var loaded = WaveformCache.TryLoad(file);

        Assert.NotNull(loaded);
        Assert.Null(loaded!.Spectrum);
    }

    [Fact]
    public void Damaged_cache_file_is_ignored_and_removed()
    {
        string file = Path.Combine(_dir, "c.wf");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4, 5 });

        Assert.Null(WaveformCache.TryLoad(file));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Missing_cache_file_is_just_a_miss()
    {
        Assert.Null(WaveformCache.TryLoad(Path.Combine(_dir, "nothing.wf")));
        Assert.Null(WaveformCache.TryLoad(null));
    }

    [Fact]
    public void Cache_file_name_follows_the_song_as_it_is_on_disk()
    {
        string song = Path.Combine(_dir, "song.mp3");
        File.WriteAllBytes(song, new byte[] { 1, 2, 3 });

        string? first = WaveformCache.FileFor(song);
        Assert.NotNull(first);
        Assert.EndsWith(".wf", first);
        Assert.Equal(first, WaveformCache.FileFor(song));   // same song, same file

        File.WriteAllBytes(song, new byte[] { 1, 2, 3, 4 });   // changed on disk: analysed afresh
        Assert.NotEqual(first, WaveformCache.FileFor(song));

        Assert.Null(WaveformCache.FileFor(Path.Combine(_dir, "missing.mp3")));
    }
}
