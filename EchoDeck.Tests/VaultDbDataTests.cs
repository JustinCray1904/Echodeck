using System.Linq;
using System.Text;
using System.Text.Json;
using EchoDeck;
using Xunit;

namespace EchoDeck.Tests;

// The vault's playlists are stored as JSON (then encrypted). These tests keep that stored shape stable: a change that would stop
// existing vaults from loading their playlists fails here first.
public class VaultDbDataTests
{
    private static VaultDbData Sample() => new()
    {
        Playlists =
        {
            new VaultDbPlaylist { Id = "a1", Name = "Gym", Items = { "x1.bin", "x2.bin", "x3.bin" } },
            new VaultDbPlaylist { Id = "b2", Name = "Zażółć gęślą – 音楽 🎵", Items = { "y1.bin" } },
        },
    };

    [Fact]
    public void NewData_HasNoPlaylists()
    {
        var data = new VaultDbData();

        Assert.NotNull(data.Playlists);
        Assert.Empty(data.Playlists);
    }

    [Fact]
    public void RoundTrip_KeepsEverything()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Sample());
        var back = JsonSerializer.Deserialize<VaultDbData>(bytes);

        Assert.NotNull(back);
        Assert.Equal(2, back!.Playlists.Count);
        Assert.Equal("a1", back.Playlists[0].Id);
        Assert.Equal("Gym", back.Playlists[0].Name);
        Assert.Equal(new[] { "x1.bin", "x2.bin", "x3.bin" }, back.Playlists[0].Items);
        Assert.Equal("Zażółć gęślą – 音楽 🎵", back.Playlists[1].Name);
    }

    [Fact]
    public void ItemOrder_IsKept()
    {
        var back = JsonSerializer.Deserialize<VaultDbData>(JsonSerializer.SerializeToUtf8Bytes(Sample()))!;

        Assert.Equal(new[] { "x1.bin", "x2.bin", "x3.bin" }, back.Playlists[0].Items.ToArray());
    }

    [Fact]
    public void StoredShape_UsesTheDocumentedPropertyNames()
    {
        var json = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(Sample()));
        using var doc = JsonDocument.Parse(json);

        var playlist = doc.RootElement.GetProperty("Playlists")[0];
        Assert.Equal("a1", playlist.GetProperty("Id").GetString());
        Assert.Equal("Gym", playlist.GetProperty("Name").GetString());
        Assert.Equal(3, playlist.GetProperty("Items").GetArrayLength());
    }

    [Fact]
    public void EmptyObject_LoadsAsNoPlaylists()
    {
        var back = JsonSerializer.Deserialize<VaultDbData>("{}");

        Assert.NotNull(back);
        Assert.Empty(back!.Playlists);
    }

    [Fact]
    public void UnknownProperties_FromANewerVersion_AreIgnored()
    {
        const string json = "{\"Playlists\":[{\"Id\":\"p\",\"Name\":\"N\",\"Items\":[\"f\"],\"Colour\":\"red\"}],\"Future\":42}";

        var back = JsonSerializer.Deserialize<VaultDbData>(json)!;

        var playlist = Assert.Single(back.Playlists);
        Assert.Equal("N", playlist.Name);
        Assert.Equal("f", Assert.Single(playlist.Items));
    }

    [Fact]
    public void APlaylistWithoutItems_LoadsWithAnEmptyList()
    {
        var back = JsonSerializer.Deserialize<VaultDbData>("{\"Playlists\":[{\"Id\":\"p\",\"Name\":\"Empty\"}]}")!;

        Assert.Empty(Assert.Single(back.Playlists).Items);
    }
}
