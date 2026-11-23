using System;
using System.IO;
using System.Linq;
using EchoDeck;
using Xunit;

namespace EchoDeck.Tests;

/// <summary>The vault file names (new .deex and the older .svv, which must keep working) and the sound-settings filter chain.</summary>
public sealed class VaultNamingAndWaveformCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tempo-cache-tests-" + Guid.NewGuid().ToString("N"));

    public VaultNamingAndWaveformCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp folder */ }
    }

    [Theory]
    [InlineData("a.deex", true)]
    [InlineData("A.DEEX", true)]
    [InlineData("old.svv", true)]
    [InlineData("song.mp3", false)]
    [InlineData("a.deex.part", false)]
    [InlineData("a.deet", false)]
    public void Recognises_vault_files_of_this_and_earlier_builds(string name, bool expected) =>
        Assert.Equal(expected, VaultFormat.IsVaultFile(Path.Combine(_dir, name)));

    [Fact]
    public void New_files_use_the_new_extension() => Assert.Equal(".deex", VaultFormat.Extension);

    [Theory]
    [InlineData(false, 0, "")]
    [InlineData(true, 0, "normvol")]
    [InlineData(false, 1, "stereo_widen")]
    [InlineData(false, 2, "spatializer")]
    [InlineData(false, 3, "stereo_widen:spatializer")]
    [InlineData(true, 3, "normvol:stereo_widen:spatializer")]
    [InlineData(false, 9, "")]
    public void Sound_settings_make_the_audio_filter_chain(bool normalize, int space, string expected) =>
        Assert.Equal(expected, SoundFilters.Chain(normalize, space));
}
