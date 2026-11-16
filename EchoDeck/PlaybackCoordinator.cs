namespace EchoDeck;

/// <summary>What a crossfade / gapless overlap should do next (see <see cref="PlaybackCoordinator.Tick"/>).</summary>
internal enum OverlapAction { Wait, Finish }

/// <summary>Where a crossfade / gapless overlap is.</summary>
internal enum OverlapPhase
{
    /// <summary>No overlap.</summary>
    None,
    /// <summary>The next song was asked to play but has not reported "playing" yet: the old song carries on at full volume.</summary>
    Opening,
    /// <summary>Both songs are playing and the overlap clock runs.</summary>
    Running,
}

/// <summary>
/// The rules of starting, stopping and overlapping songs, kept apart from the window and from LibVLC so they can be tested without
/// either (the window asks it what to do; PlaybackCoordinatorTests drives it through every race). It never touches a player: it only
/// holds the state and answers questions, and every time is passed in.
///
///  - Stop and start never run together: while a Stop is in progress, a request to play something is parked and handed back when the
///    Stop is done, so the old Stop can neither stop the new song nor clear its screen.
///  - An overlap starts its clock when the new song really plays (not when it was asked to), lasts at most what is left of the old
///    song, is given up after 10 s if the new song never starts, and in gapless mode keeps the old song until it has really ended.
/// </summary>
internal sealed class PlaybackCoordinator
{
    /// <summary>How long a new song may take to report "playing" before the overlap is given up.</summary>
    public const double OpenTimeoutSeconds = 10;

    /// <summary>In gapless mode the old song is kept this long after the planned overlap, so it is never cut before its natural end.</summary>
    public const double GaplessGraceSeconds = 1.5;

    // ---- stop / start serialization

    public bool IsStopping { get; private set; }
    public (string Path, long StartMs, bool IsMrl)? Pending { get; private set; }

    /// <summary>A Stop begins. False when one is already running (the second click does nothing).</summary>
    public bool BeginStop()
    {
        if (IsStopping) return false;
        IsStopping = true;
        return true;
    }

    /// <summary>A request to play something. True: a Stop is running, the request is parked (only the latest one is kept).</summary>
    public bool Park(string path, long startMs, bool isMrl)
    {
        if (!IsStopping) return false;
        Pending = (path, startMs, isMrl);
        return true;
    }

    /// <summary>The Stop is over. Returns the request that was parked meanwhile (to be started now), if any.</summary>
    public (string Path, long StartMs, bool IsMrl)? EndStop()
    {
        IsStopping = false;
        var pending = Pending;
        Pending = null;
        return pending;
    }

    // ---- overlap

    private DateTime _asked;
    private DateTime _started;

    public OverlapPhase Phase { get; private set; }
    public bool Fade { get; private set; }
    public double Seconds { get; private set; }

    /// <summary>An overlap was decided on: the next song is about to be asked to play.</summary>
    public void BeginOverlap(bool fade, double seconds, DateTime now)
    {
        Phase = OverlapPhase.Opening;
        Fade = fade;
        Seconds = Math.Max(0.3, seconds);
        _asked = now;
    }

    /// <summary>
    /// The new song reports "playing": the clock starts, over at most what is left of the old song (never under 0.3 s).
    /// False when there is no overlap waiting for that.
    /// </summary>
    public bool NoteIncomingPlaying(DateTime now, double outgoingLeftSeconds)
    {
        if (Phase != OverlapPhase.Opening) return false;
        Phase = OverlapPhase.Running;
        _started = now;
        if (outgoingLeftSeconds > 0) Seconds = Math.Max(0.3, Math.Min(Seconds, outgoingLeftSeconds));
        return true;
    }

    /// <summary>0 while the new song is opening, then 0 to 1 over the overlap.</summary>
    public double Progress(DateTime now) =>
        Phase == OverlapPhase.Running ? Math.Clamp((now - _started).TotalSeconds / Math.Max(0.1, Seconds), 0, 1) : 0;

    /// <summary>Asked every moment of an overlap: wait, or finish it (stop and release the old song).</summary>
    public OverlapAction Tick(DateTime now, bool outgoingStillPlaying)
    {
        switch (Phase)
        {
            case OverlapPhase.None:
                return OverlapAction.Wait;

            case OverlapPhase.Opening:
                return (now - _asked).TotalSeconds > OpenTimeoutSeconds ? OverlapAction.Finish : OverlapAction.Wait;

            default:
                double elapsed = (now - _started).TotalSeconds;
                if (elapsed < Seconds) return OverlapAction.Wait;
                if (Fade) return OverlapAction.Finish;
                return !outgoingStillPlaying || elapsed >= Seconds + GaplessGraceSeconds ? OverlapAction.Finish : OverlapAction.Wait;
        }
    }

    /// <summary>The overlap is over (finished, cancelled by a seek / skip / stop, or never started).</summary>
    public void EndOverlap() => Phase = OverlapPhase.None;
}

/// <summary>
/// One place that decides whether the result of a background job (a Discord cover lookup, ...) may still be used. Every new job gets a
/// fresh token and cancels the one before it; <see cref="Cancel"/> (a new song, Anonymous Mode, Discord switched off) cancels the
/// current one. <see cref="RunIfCurrent"/> does the final "is this still wanted?" check and the publishing as one step, so a result can
/// never slip out between the check and the use, and a clear that follows <see cref="Cancel"/> always lands after anything in flight.
/// </summary>
internal sealed class OperationScope
{
    private readonly object _gate = new();
    private CancellationTokenSource _current = new();

    /// <summary>Starts a new job: the previous one is cancelled.</summary>
    public CancellationToken Renew()
    {
        lock (_gate)
        {
            _current.Cancel();
            _current = new CancellationTokenSource();   // the old source is not disposed: jobs may still be looking at its token
            return _current.Token;
        }
    }

    /// <summary>Cancels the job that is running (waits for a publish that is in progress).</summary>
    public void Cancel()
    {
        lock (_gate) _current.Cancel();
    }

    /// <summary>Runs <paramref name="action"/> only if <paramref name="token"/> is still the current, uncancelled job. False when it was not.</summary>
    public bool RunIfCurrent(CancellationToken token, Action action)
    {
        lock (_gate)
        {
            if (token.IsCancellationRequested || token != _current.Token) return false;
            action();
            return true;
        }
    }
}
