using System;
using System.IO;
using System.Security.Cryptography;
using EchoDeck;
using Xunit;

namespace EchoDeck.Tests;

/// <summary>Tempo.log must not stay silent when the vault's playlist file fails, and must never write down what a failure says (names, paths).</summary>
[CollectionDefinition("AppLog", DisableParallelization = true)]
public sealed class AppLogCollection { }

[Collection("AppLog")]
public sealed class VaultLoggingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tempo-log-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _vault;
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public VaultLoggingTests()
    {
        _vault = Path.Combine(_root, "vault");
        Directory.CreateDirectory(_vault);
        AppLog.DirectoryOverride = Path.Combine(_root, "logs");
    }

    public void Dispose()
    {
        AppLog.DirectoryOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch { /* temp folder */ }
    }

    private static string Log() => File.Exists(AppLog.FilePath) ? File.ReadAllText(AppLog.FilePath) : "";

    [Fact]
    public void A_damaged_playlist_file_is_logged_by_type_only_and_kept_as_bad()
    {
        string db = Path.Combine(_vault, "vault.db");
        var junk = new byte[80];
        RandomNumberGenerator.Fill(junk);
        junk[0] = (byte)'S'; junk[1] = (byte)'V'; junk[2] = (byte)'D'; junk[3] = 1;   // the right magic, then nonsense
        File.WriteAllBytes(db, junk);

        var vault = new VaultService(_vault, _key);
        vault.ReloadPlaylists();

        Assert.Empty(vault.Playlists);               // starts with no playlists
        Assert.True(File.Exists(db + ".bad"));       // the damaged file is kept
        string log = Log();
        Assert.Contains("[Vault playlists]", log);
        Assert.Contains("Exception", log);           // what kind of failure
        Assert.DoesNotContain("authentication tag", log, StringComparison.OrdinalIgnoreCase);   // but not what it said
    }

    [Fact]
    public void A_playlist_file_that_cannot_be_written_is_logged()
    {
        Directory.CreateDirectory(Path.Combine(_vault, "vault.db.tmp"));   // a folder where the temporary file must go

        var vault = new VaultService(_vault, _key);
        vault.SavePlaylists();

        Assert.Contains("[Vault playlists]", Log());
        Assert.Contains("could not be saved", Log());
    }

    [Fact]
    public void Playlists_survive_a_save_and_a_reload_without_logging_anything()
    {
        var first = new VaultService(_vault, _key);
        first.Playlists.Add(new Playlist { Name = "Gym", Items = new System.Collections.Generic.List<string> { Path.Combine(_vault, "a.deex") } });
        first.SavePlaylists();

        var second = new VaultService(_vault, _key);
        second.ReloadPlaylists();

        Assert.Single(second.Playlists);
        Assert.Equal("Gym", second.Playlists[0].Name);
        Assert.Equal("", Log());
    }

    [Fact]
    public void Failure_writes_the_kind_of_exception_but_never_its_message()
    {
        AppLog.Failure("Vault test", new InvalidOperationException("private-name.mp4 could not be opened"));
        AppLog.Write("Plain test", new InvalidOperationException("visible message"));

        string log = Log();
        Assert.Contains("[Vault test] InvalidOperationException", log);
        Assert.DoesNotContain("private-name", log);
        Assert.Contains("visible message", log);
    }
}
