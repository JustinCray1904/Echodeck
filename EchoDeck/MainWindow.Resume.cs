using System.Windows;
using System.Windows.Threading;

namespace EchoDeck;

// "Continue where you left off": videos remember their position; playing the same file again offers to jump back.
public partial class MainWindow
{
    private string? _resumePath;          // the normal-mode video being tracked (null for music / Anonymous Mode)
    private long _resumeTimeMs;
    private long _resumeOfferMs;
    private DateTime _resumeLastWrite = DateTime.MinValue;
    private DispatcherTimer? _resumeTimer;

    /// <summary>A new item starts: save the old one's spot, then track the new one if it is a video.</summary>
    private void BeginResumeTracking(string? path, bool isAudio)
    {
        _resumePath = path != null && !isAudio && !_anon && _cfg.RememberPosition ? path : null;
        _resumeTimeMs = 0;
        _resumeLastWrite = DateTime.UtcNow;
    }

    /// <summary>Writes the current spot to disk (called when switching, stopping, closing, and every 15 s while playing).</summary>
    private void CommitResume()
    {
        if (_anon || _resumePath == null) return;
        ResumeStore.Commit(_resumePath, _resumeTimeMs, _lengthMs);
        _resumeLastWrite = DateTime.UtcNow;
    }

    private void StopResumeTracking()
    {
        CommitResume();
        _resumePath = null;
        HideResumePrompt(animate: false);
    }

    private void TrackResume(long timeMs)
    {
        if (_resumePath == null) return;
        _resumeTimeMs = timeMs;
        if ((DateTime.UtcNow - _resumeLastWrite).TotalSeconds >= 15) CommitResume();
    }

    // ------------------------------------------------------------------ the small prompt above the control bar

    private void MaybeOfferResume(string path, bool isAudio, bool inMini)
    {
        HideResumePrompt(animate: false);
        if (isAudio || inMini || _anon || !_cfg.RememberPosition) return;

        var entry = ResumeStore.Get(path);
        if (entry == null) return;

        _resumeOfferMs = entry.Pos;
        ResumeText.Text = "Continue from " + Fmt(entry.Pos) + "?";
        // above the control bar: a bit higher when the floating bar is up in fullscreen
        ResumeCard.Margin = new Thickness(0, 0, 0, _isFullscreen ? 120 : 26);
        UiAnim.FadeIn(ResumeCard, 200);

        _resumeTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        _resumeTimer.Tick -= ResumeTimer_Tick;
        _resumeTimer.Tick += ResumeTimer_Tick;
        _resumeTimer.Stop();
        _resumeTimer.Start();
    }

    private void ResumeTimer_Tick(object? sender, EventArgs e) => HideResumePrompt(animate: true);

    private void HideResumePrompt(bool animate)
    {
        _resumeTimer?.Stop();
        if (ResumeCard.Visibility != Visibility.Visible) return;
        if (animate) UiAnim.FadeOut(ResumeCard, 220);
        else { UiAnim.Cancel(ResumeCard); ResumeCard.Visibility = Visibility.Collapsed; ResumeCard.Opacity = 1; }
    }

    private void ResumeContinue_Click(object sender, RoutedEventArgs e)
    {
        long pos = _resumeOfferMs;
        HideResumePrompt(animate: true);
        if (_player is null || !_hasMedia) return;

        // while LibVLC is still opening the file the jump is applied as soon as it starts playing
        if (_player.IsPlaying) _player.Time = pos; else _pendingSeekMs = pos;
        ShowOsd("Continuing from " + Fmt(pos));
    }

    private void ResumeDismiss_Click(object sender, RoutedEventArgs e) => HideResumePrompt(animate: true);
}
