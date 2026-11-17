using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EchoDeck;
using Xunit;

namespace EchoDeck.Tests;

/// <summary>A player that only remembers what was done to it.</summary>
internal sealed class FakePlayer
{
    public string Track = "";
    public bool Playing = true;
    public bool Stopped;
    public bool Disposed;
}

/// <summary>
/// Drives <see cref="PlaybackCoordinator"/> the way MainWindow does (StartPlayback, WatchSongEnd / StartIncoming, FadeStep,
/// FinishCrossfadeNow, the Stop button), with fake players, so races can be replayed without a window or LibVLC. If MainWindow's use of the
/// coordinator changes, this mirror has to change with it.
/// </summary>
internal sealed class PlaybackHarness
{
    public readonly PlaybackCoordinator Coord = new();
    public readonly List<FakePlayer> All = new();
    public readonly List<string> Log = new();
    public FakePlayer Current;
    public FakePlayer? Outgoing;
    public DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public PlaybackHarness(string firstTrack)
    {
        Current = NewPlayer(firstTrack);
    }

    private FakePlayer NewPlayer(string track)
    {
        var p = new FakePlayer { Track = track };
        All.Add(p);
        return p;
    }

    /// <summary>StartPlayback, normal path.</summary>
    public void Play(string track)
    {
        if (Coord.Park(track, 0, false)) { Log.Add("parked " + track); return; }
        FinishOverlap();
        Current.Track = track;
        Current.Playing = true;
        Current.Stopped = false;
    }

    /// <summary>WatchSongEnd deciding on an overlap, and StartIncoming.</summary>
    public void StartOverlap(string next, bool fade, double seconds)
    {
        Coord.BeginOverlap(fade, seconds, Now);
        Outgoing = Current;
        Current = NewPlayer(next);
    }

    /// <summary>The incoming player reports "playing".</summary>
    public void IncomingPlaying(double outgoingLeftSeconds = 30) => Coord.NoteIncomingPlaying(Now, outgoingLeftSeconds);

    /// <summary>FadeStep, once.</summary>
    public void FadeStep()
    {
        if (Outgoing == null) return;
        if (Coord.Tick(Now, Outgoing.Playing) == OverlapAction.Finish) FinishOverlap();
    }

    public void Advance(double seconds, double stepSeconds = 0.05)
    {
        for (double t = 0; t < seconds; t += stepSeconds)
        {
            Now = Now.AddSeconds(stepSeconds);
            FadeStep();
        }
    }

    /// <summary>FinishCrossfadeNow / CancelCrossfade.</summary>
    public void FinishOverlap()
    {
        Coord.EndOverlap();
        var old = Outgoing;
        Outgoing = null;
        if (old == null) return;
        old.Stopped = true;
        old.Playing = false;
        old.Disposed = true;
    }

    /// <summary>The Stop button: begins the stop; <see cref="EndStop"/> finishes it (the part after the await).</summary>
    public bool BeginStop()
    {
        if (!Coord.BeginStop()) return false;
        FinishOverlap();   // both sides of a crossfade
        return true;
    }

    public void EndStop()
    {
        Current.Stopped = true;
        Current.Playing = false;
        var pending = Coord.EndStop();
        if (pending is { } p) Play(p.Path);   // the request that waited
    }
}

public class PlaybackCoordinatorTests
{
    // ---- stop / start

    [Fact]
    public void AStartDuringAStopWaitsAndThenPlays()
    {
        var h = new PlaybackHarness("a");
        Assert.True(h.BeginStop());

        h.Play("b");                                  // picked while the old player is still being stopped
        Assert.Equal("a", h.Current.Track);           // not started yet: the old Stop cannot kill it
        Assert.Contains("parked b", h.Log);

        h.EndStop();
        Assert.Equal("b", h.Current.Track);           // plays now...
        Assert.True(h.Current.Playing);               // ...and was not stopped by the earlier stop
        Assert.False(h.Current.Stopped);
    }

    [Fact]
    public void OnlyTheLatestRequestDuringAStopIsKept()
    {
        var h = new PlaybackHarness("a");
        h.BeginStop();
        h.Play("b");
        h.Play("c");
        h.Play("d");
        h.EndStop();
        Assert.Equal("d", h.Current.Track);
    }

    [Fact]
    public void ASecondStopWhileStoppingDoesNothing()
    {
        var h = new PlaybackHarness("a");
        Assert.True(h.BeginStop());
        Assert.False(h.BeginStop());
        h.EndStop();
        Assert.True(h.BeginStop());                   // and a later stop works again
    }

    [Fact]
    public void WithNoStopRunningARequestIsNotParked()
    {
        var c = new PlaybackCoordinator();
        Assert.False(c.Park("x", 0, false));
        Assert.Null(c.Pending);
    }

    // ---- crossfade

    [Fact]
    public void TheCrossfadeClockStartsWhenTheNewSongPlays_NotWhenItWasAskedFor()
    {
        var h = new PlaybackHarness("a");
        h.StartOverlap("b", fade: true, seconds: 6);

        h.Advance(8);                                 // the new song is slow to open: longer than the whole crossfade
        Assert.NotNull(h.Outgoing);                   // the old song is still there and not stopped
        Assert.False(h.All[0].Stopped);
        Assert.Equal(0, h.Coord.Progress(h.Now));     // and still at full volume

        h.IncomingPlaying();
        h.Advance(3);
        Assert.NotNull(h.Outgoing);                   // 3 of 6 seconds
        h.Advance(3.5);
        Assert.Null(h.Outgoing);                      // finished after the real overlap
        Assert.True(h.All[0].Stopped && h.All[0].Disposed);
        Assert.Equal(OverlapPhase.None, h.Coord.Phase);
    }

    [Fact]
    public void ASongThatNeverStartsIsGivenUpAfterTenSeconds()          // decoder error / unreadable file
    {
        var h = new PlaybackHarness("a");
        h.StartOverlap("broken", fade: true, seconds: 6);

        h.Advance(9);
        Assert.NotNull(h.Outgoing);
        h.Advance(2);
        Assert.Null(h.Outgoing);                      // the overlap was given up, the old player released
        Assert.Equal(OverlapPhase.None, h.Coord.Phase);

        h.Play("c");                                  // and playback carries on normally
        Assert.Equal("c", h.Current.Track);
        Assert.True(h.Current.Playing);
    }

    [Fact]
    public void TheOverlapNeverLastsLongerThanTheOldSongHasLeft()
    {
        var c = new PlaybackCoordinator();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        c.BeginOverlap(fade: true, seconds: 8, t);
        c.NoteIncomingPlaying(t, outgoingLeftSeconds: 2);
        Assert.Equal(2, c.Seconds);

        c.BeginOverlap(fade: true, seconds: 8, t);
        c.NoteIncomingPlaying(t, outgoingLeftSeconds: 0.01);
        Assert.Equal(0.3, c.Seconds);                 // never shorter than 0.3 s
    }

    [Fact]
    public void GaplessKeepsTheOldSongUntilItHasReallyEnded()
    {
        var h = new PlaybackHarness("a");
        h.StartOverlap("b", fade: false, seconds: 0.8);
        h.IncomingPlaying();

        h.Advance(1.2);                               // the planned overlap is over but the old song is still playing
        Assert.NotNull(h.Outgoing);

        h.Outgoing!.Playing = false;                  // it reaches its natural end
        h.Advance(0.1);
        Assert.Null(h.Outgoing);
    }

    [Fact]
    public void GaplessDoesNotWaitForEver()
    {
        var h = new PlaybackHarness("a");
        h.StartOverlap("b", fade: false, seconds: 0.8);
        h.IncomingPlaying();
        h.Advance(0.8 + PlaybackCoordinator.GaplessGraceSeconds + 0.2);   // the old song never reports an end
        Assert.Null(h.Outgoing);
    }

    // ---- races between the user and an overlap

    [Fact]
    public void SwitchingTracksDuringACrossfadeStopsTheOldSong()
    {
        var h = new PlaybackHarness("a");
        h.StartOverlap("b", fade: true, seconds: 6);
        h.IncomingPlaying();
        h.Advance(2);
        var outgoing = h.Outgoing!;

        h.Play("c");                                  // the user picks another song in the middle of the blend

        Assert.True(outgoing.Stopped && outgoing.Disposed);   // nothing is left sounding under it
        Assert.Null(h.Outgoing);
        Assert.Equal("c", h.Current.Track);
        Assert.Equal(OverlapPhase.None, h.Coord.Phase);
    }

    [Fact]
    public void StoppingDuringACrossfadeStopsBothSongs()
    {
        var h = new PlaybackHarness("a");
        h.StartOverlap("b", fade: true, seconds: 6);
        h.IncomingPlaying();
        h.Advance(1);
        var outgoing = h.Outgoing!;
        var incoming = h.Current;

        Assert.True(h.BeginStop());
        h.EndStop();

        Assert.True(outgoing.Stopped);                // the outgoing player used to keep sounding
        Assert.True(incoming.Stopped);
        Assert.Equal(OverlapPhase.None, h.Coord.Phase);
    }

    [Fact]
    public void StartingASongWhileTheStopOfACrossfadeIsFinishingPlaysIt()
    {
        var h = new PlaybackHarness("a");
        h.StartOverlap("b", fade: true, seconds: 6);
        h.IncomingPlaying();

        h.BeginStop();
        h.Play("c");
        h.EndStop();

        Assert.Equal("c", h.Current.Track);
        Assert.True(h.Current.Playing);
        Assert.True(h.All[0].Stopped);                // the old song is gone
    }

    [Fact]
    public void RapidSwitchingLeavesOnlyTheLastSongPlaying()
    {
        var h = new PlaybackHarness("s0");
        for (int i = 1; i <= 50; i++)
        {
            h.Play("s" + i);
            if (i % 7 == 0)
            {
                h.StartOverlap("x" + i, fade: i % 2 == 0, seconds: 4);
                if (i % 14 == 0) h.IncomingPlaying();
            }
        }
        h.Play("last");

        Assert.Equal("last", h.Current.Track);
        Assert.Null(h.Outgoing);
        Assert.Equal(OverlapPhase.None, h.Coord.Phase);
        Assert.Single(h.All, p => p.Playing && !p.Stopped);   // exactly one thing is playing
    }

    [Fact]
    public void RapidStopAndPlayClicksEndWithTheLastRequest()
    {
        var h = new PlaybackHarness("a");
        for (int i = 0; i < 20; i++)
        {
            h.BeginStop();
            h.Play("t" + i);
            h.EndStop();
        }
        Assert.Equal("t19", h.Current.Track);
        Assert.True(h.Current.Playing);
        Assert.False(h.Coord.IsStopping);
    }
}

public class OperationScopeTests
{
    [Fact]
    public void ALateResultOfAnOlderJobIsDropped()
    {
        var scope = new OperationScope();
        var songA = scope.Renew();                    // song A: cover lookup starts
        var songB = scope.Renew();                    // song B starts meanwhile

        var published = new List<string>();
        Assert.True(scope.RunIfCurrent(songB, () => published.Add("B")));
        Assert.False(scope.RunIfCurrent(songA, () => published.Add("A")));   // A's cover finally arrives: dropped
        Assert.Equal(new[] { "B" }, published);
    }

    [Fact]
    public void CancellingDropsTheJobThatIsRunning()      // entering Anonymous Mode / Discord switched off
    {
        var scope = new OperationScope();
        var job = scope.Renew();
        scope.Cancel();

        bool published = false;
        Assert.False(scope.RunIfCurrent(job, () => published = true));
        Assert.False(published);
        Assert.True(job.IsCancellationRequested);
    }

    [Fact]
    public void ADisconnectAndReconnectWorks()
    {
        var scope = new OperationScope();
        var first = scope.Renew();
        scope.Cancel();                               // Discord closes

        var second = scope.Renew();                   // and comes back: a new status
        var published = new List<string>();
        Assert.False(scope.RunIfCurrent(first, () => published.Add("old")));
        Assert.True(scope.RunIfCurrent(second, () => published.Add("new")));
        Assert.Equal(new[] { "new" }, published);
    }

    [Fact]
    public async Task ACancelWaitsForAPublishThatIsInProgress()
    {
        var scope = new OperationScope();
        var job = scope.Renew();
        var order = new List<string>();
        var inside = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();

        var publisher = Task.Run(() => scope.RunIfCurrent(job, () =>
        {
            inside.Set();
            release.Wait();
            order.Add("publish");
        }));

        inside.Wait();
        var canceller = Task.Run(() => { scope.Cancel(); order.Add("cancel"); });
        Thread.Sleep(50);
        Assert.DoesNotContain("cancel", order);       // the cancel (and the "clear" after it) cannot overtake the publish
        release.Set();

        await Task.WhenAll(publisher, canceller);
        Assert.Equal(new[] { "publish", "cancel" }, order);
    }

    [Fact]
    public void ManyRenewalsOnlyTheLastTokenIsAlive()
    {
        var scope = new OperationScope();
        var tokens = Enumerable.Range(0, 100).Select(_ => scope.Renew()).ToList();
        Assert.All(tokens.Take(99), t => Assert.True(t.IsCancellationRequested));
        Assert.False(tokens[99].IsCancellationRequested);
    }
}
