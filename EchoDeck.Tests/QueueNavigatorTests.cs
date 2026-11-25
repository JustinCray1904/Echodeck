using System;
using EchoDeck;
using Xunit;

namespace EchoDeck.Tests;

/// <summary>The queue's rules (Next, Previous, and what follows a finished song) without a window.</summary>
public class QueueNavigatorTests
{
    private static readonly Random Rng = new(1234);

    [Fact]
    public void Next_and_previous_move_to_the_neighbour_and_stop_at_the_ends()
    {
        Assert.Equal(2, QueueNavigator.Adjacent(5, 1, +1, shuffle: false, Rng));
        Assert.Equal(0, QueueNavigator.Adjacent(5, 1, -1, shuffle: false, Rng));
        Assert.Equal(-1, QueueNavigator.Adjacent(5, 0, -1, shuffle: false, Rng));   // nothing before the first
        Assert.Equal(-1, QueueNavigator.Adjacent(5, 4, +1, shuffle: false, Rng));   // nothing after the last
        Assert.Equal(-1, QueueNavigator.Adjacent(0, -1, +1, shuffle: false, Rng));  // empty queue
    }

    [Fact]
    public void Shuffle_never_picks_the_song_that_is_playing()
    {
        for (int i = 0; i < 300; i++)
        {
            int pick = QueueNavigator.Adjacent(4, 2, +1, shuffle: true, Rng);
            Assert.InRange(pick, 0, 3);
            Assert.NotEqual(2, pick);
        }
    }

    [Fact]
    public void Shuffle_with_one_song_is_just_the_neighbour_rule()
    {
        Assert.Equal(-1, QueueNavigator.Adjacent(1, 0, +1, shuffle: true, Rng));
    }

    [Fact]
    public void A_finished_song_is_followed_by_the_next_one()
    {
        Assert.Equal(3, QueueNavigator.AfterEnd(5, 2, LoopMode.Off, shuffle: false, autoAdvance: true, Rng));
    }

    [Fact]
    public void The_last_song_stops_the_player_unless_the_queue_repeats()
    {
        Assert.Equal(-1, QueueNavigator.AfterEnd(3, 2, LoopMode.Off, shuffle: false, autoAdvance: true, Rng));
        Assert.Equal(0, QueueNavigator.AfterEnd(3, 2, LoopMode.All, shuffle: false, autoAdvance: true, Rng));
    }

    [Fact]
    public void Repeat_one_plays_the_same_song_again()
    {
        Assert.Equal(1, QueueNavigator.AfterEnd(3, 1, LoopMode.One, shuffle: false, autoAdvance: false, Rng));
        Assert.Equal(1, QueueNavigator.AfterEnd(3, 1, LoopMode.One, shuffle: true, autoAdvance: true, Rng));   // repeat wins over shuffle
    }

    [Fact]
    public void Without_auto_advance_only_repeat_all_carries_on()
    {
        Assert.Equal(-1, QueueNavigator.AfterEnd(3, 0, LoopMode.Off, shuffle: false, autoAdvance: false, Rng));
        Assert.Equal(1, QueueNavigator.AfterEnd(3, 0, LoopMode.All, shuffle: false, autoAdvance: false, Rng));
    }

    [Fact]
    public void Shuffle_picks_another_song_when_one_ends()
    {
        for (int i = 0; i < 200; i++)
            Assert.NotEqual(1, QueueNavigator.AfterEnd(5, 1, LoopMode.Off, shuffle: true, autoAdvance: true, Rng));
    }

    [Fact]
    public void Nothing_is_playing_from_the_queue_and_nothing_repeats_means_nothing_next()
    {
        Assert.Equal(-1, QueueNavigator.AfterEnd(3, -1, LoopMode.Off, shuffle: false, autoAdvance: true, Rng));
        Assert.Equal(0, QueueNavigator.AfterEnd(3, -1, LoopMode.All, shuffle: false, autoAdvance: true, Rng));
        Assert.Equal(-1, QueueNavigator.AfterEnd(0, -1, LoopMode.All, shuffle: false, autoAdvance: true, Rng));
    }
}

public class TimeTextTests
{
    [Theory]
    [InlineData(0L, "00:00:00")]
    [InlineData(-5000L, "00:00:00")]
    [InlineData(3_725_000L, "01:02:05")]
    [InlineData(59_999L, "00:00:59")]
    [InlineData(360_000_000L, "100:00:00")]
    public void Clock_shows_hours_minutes_and_seconds(long ms, string expected) => Assert.Equal(expected, TimeText.Clock(ms));
}

public class StereoModesTests
{
    [Theory]
    [InlineData(0, 0)]    // normal: LibVLC's "unset"
    [InlineData(1, 7)]    // mono
    [InlineData(2, 2)]    // reverse stereo
    [InlineData(3, 3)]    // left only
    [InlineData(4, 4)]    // right only
    [InlineData(5, 5)]    // Dolby surround
    [InlineData(-3, 0)]   // an unknown saved value is the normal mode
    [InlineData(99, 5)]
    public void Settings_map_to_libvlc_stereo_modes(int setting, int code) => Assert.Equal(code, StereoModes.CodeFor(setting));

    [Fact]
    public void Every_mode_has_a_name()
    {
        Assert.Equal(6, StereoModes.Names.Length);
        Assert.All(StereoModes.Names, name => Assert.False(string.IsNullOrWhiteSpace(name)));
    }
}
