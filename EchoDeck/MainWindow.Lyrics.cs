using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace EchoDeck;

// Lyrics: loaded from Tempo's hidden lyrics folder (or an .lrc / .txt next to the song), editor + paste, online lookup, local Whisper generation, and the synced display.
public partial class MainWindow
{
    private const double LyricsPanelMargin = 28;

    /// <summary>
    /// The cover and the visualiser keep their size while there is room; the lyrics take what is left beside them (at most 480 wide).
    /// In a narrower music view (the queue and side bar are open, or the window is small) the cover scales down and the lyrics get
    /// about half; when even that is too tight, the lyrics take the whole width and the cover steps aside until they are closed.
    /// </summary>
    private double ComputeLyricsColumn()
    {
        double avail = MusicPanel.ActualWidth;
        if (avail <= 0) return 480 + LyricsPanelMargin;
        if (avail < 560) return avail;

        const double coverNeeds = 500;
        double lyrics = avail >= 900
            ? Math.Clamp(avail - coverNeeds - LyricsPanelMargin, 320, 480)
            : Math.Clamp(avail * 0.46, 250, 480);
        return lyrics + LyricsPanelMargin;
    }

    private readonly ObservableCollection<LyricLine> _lyricLines = new();
    private readonly Dictionary<string, string> _sessionLyrics = new(StringComparer.OrdinalIgnoreCase);   // generated / edited this session
    private readonly Dictionary<string, string> _anonLyrics = new(StringComparer.OrdinalIgnoreCase);      // Anonymous Mode: memory only
    private LyricsDocument? _lyrics;
    private string? _lyricsMediaPath;          // song the shown lyrics belong to
    private string? _lyricsBusyPath;           // song Whisper is working on
    private CancellationTokenSource? _lyricsCts;
    private LyricsEditorWindow? _lyricsEditor;
    private Action<double>? _lyricScroll;
    private ScrollViewer? _lyricsViewer;
    private int _lyricIndex = -1;
    private DateTime _lyricManualUntil = DateTime.MinValue;
    private bool _lyricsOpen;

    private Dictionary<string, string> LyricStore => _anon ? _anonLyrics : _sessionLyrics;

    private void InitLyrics()
    {
        LyricsList.ItemsSource = _lyricLines;
        _lyricScroll = SmoothScroll.Attach(LyricsList, 0.14);

        // after the wheel is used, stop following the song for a few seconds so the reader can look around
        LyricsList.AddHandler(UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler((_, _) => _lyricManualUntil = DateTime.UtcNow.AddSeconds(3.5)), handledEventsToo: true);

        LyricsList.SizeChanged += (_, _) => { if (_lyricsOpen) ScrollToLyric(_lyricIndex, immediate: false); };
    }

    // ------------------------------------------------------------------ opening / closing the panel

    /// <summary>Music view shown or hidden: the Lyrics button only exists for songs.</summary>
    private void OnMusicViewChanged(bool musicShown)
    {
        LyricsToggle.Visibility = musicShown ? Visibility.Visible : Visibility.Collapsed;
        if (!musicShown) SetLyricsOpen(false, animate: false, persist: false);
        else if (_cfg.LyricsOpen && !_lyricsOpen) SetLyricsOpen(true, animate: false, persist: false);
    }

    private void SetLyricsOpen(bool open, bool animate = true, bool persist = true)
    {
        if (open == _lyricsOpen && LyricsPanel.Visibility == (open ? Visibility.Visible : Visibility.Collapsed) && !animate) return;
        _lyricsOpen = open;

        // the Lyrics button sits on the cover-coloured background: white when the lyrics are open, softer white when not
        LyricsToggle.Foreground = open ? Brushes.White : LyricsToggleIdle;
        if (open) EnsureLyricTimer().Start(); else _lyricTimer?.Stop();
        if (persist)
        {
            _cfg.LyricsOpen = open;
            if (!_anon) _cfg.Save(notify: false);
        }

        double from = LyricsCol.Width.Value;
        double full = ComputeLyricsColumn();
        double to = open ? full : 0;
        // the panel keeps its full width while the column grows / shrinks (the host clips it): the text slides in and out instead
        // of being squeezed into a narrowing box, which made it cramped on closing
        if (open) LyricsPanel.Width = full - LyricsPanelMargin;
        if (open) LyricsPanel.Visibility = Visibility.Visible;

        void Apply(double w)
        {
            LyricsCol.Width = new GridLength(w);
            LyricsPanel.Opacity = Math.Clamp(w / full * 1.3, 0, 1);
        }

        UiAnim.Tween(LyricsCol, from, to, animate ? 260 : 0, Apply, () =>
        {
            if (!open) LyricsPanel.Visibility = Visibility.Collapsed;
            else SyncLyricsNow();
            UpdateTaskbarClip();
        });
    }

    private void LyricsToggle_Click(object sender, RoutedEventArgs e) => SetLyricsOpen(!_lyricsOpen);
    private void LyricsClose_Click(object sender, RoutedEventArgs e) => SetLyricsOpen(false);

    // ------------------------------------------------------------------ following the song

    private static readonly Brush LyricsToggleIdle = MakeBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
    private static readonly Brush WordLit = Brushes.White;
    private static readonly Brush WordDim = MakeBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));

    private static Brush MakeBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private DispatcherTimer? _lyricTimer;

    // ---- the lyrics clock
    // LibVLC reports the song's time only a few times a second, and each report is already a little old when it is read. Using the
    // reports directly made the highlight jump ahead and fall behind. Instead the lyrics run on their own clock (the system's
    // stopwatch) that is kept in step with the reports: a report is never ahead of the truth, only behind it, so the clock follows the
    // highest value it has been given (the freshest report) and lets go of it very slowly. A jump of more than 0.6 s (a seek, or
    // playing again after a pause) puts the clock straight on the report.
    private double _lyricClockOffset;
    private bool _lyricClockValid;

    private static double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    private long IngestLyricTime(long reported, bool allowSnap)
    {
        double now = NowMs();
        double target = reported - now;
        double estimate = now + _lyricClockOffset;

        if (!_lyricClockValid || (allowSnap && Math.Abs(reported - estimate) > 600))
        {
            _lyricClockOffset = target;
            _lyricClockValid = true;
        }
        else if (target > _lyricClockOffset) _lyricClockOffset = target;
        else _lyricClockOffset -= 0.25;

        return Math.Max(0, (long)(now + _lyricClockOffset));
    }

    /// <summary>A seek happened (the bar, the arrow keys, a double-clicked line): the lyrics jump there and follow again.</summary>
    private void OnSeeked(long ms)
    {
        _lyricManualUntil = DateTime.MinValue;
        _lyricClockOffset = ms - NowMs();
        _lyricClockValid = true;
        if (!_lyricsOpen || _lyrics is not { Synced: true }) return;

        UpdateLyricsCore(ms);
        if (_lyricIndex >= 0) ScrollToLyric(_lyricIndex, immediate: false);
    }

    private DispatcherTimer EnsureLyricTimer()
    {
        if (_lyricTimer == null)
        {
            _lyricTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _lyricTimer.Tick += (_, _) =>
            {
                if (_player is not { IsPlaying: true }) { _lyricClockValid = false; return; }
                UpdateLyricsCore(IngestLyricTime(_player.Time, allowSnap: true));
            };
        }
        return _lyricTimer;
    }

    /// <summary>The player's own time report (it can be a little old by the time it gets here).</summary>
    private void UpdateLyrics(long timeMs) => UpdateLyricsCore(IngestLyricTime(timeMs, allowSnap: false));

    // ---- per-song timing nudge (positive = the lyrics show later)

    private int _lyricOffsetMs;

    private void NudgeLyricOffset(int deltaMs)
    {
        if (!_lyricsOpen || _lyricsMediaPath == null) return;
        _lyricOffsetMs = Math.Clamp(_lyricOffsetMs + deltaMs, -5000, 5000);
        LyricsOffsets.Set(_lyricsMediaPath, _lyricOffsetMs, save: !_anon);   // Anonymous Mode keeps it in memory only
        SyncLyricsNow();
        ShowOsd("Lyrics timing for this song: " + (_lyricOffsetMs > 0 ? "+" : "") + (_lyricOffsetMs / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s");
    }

    private void UpdateLyricsCore(long timeMs)
    {
        if (!_lyricsOpen || _lyrics is not { Synced: true } || _lyricLines.Count == 0) return;
        timeMs -= _lyricOffsetMs + _cfg.LyricsOffsetMs;   // a line shows later when the offset is positive (the setting for all songs + this song's own nudge)

        int idx = FindLyricIndex(timeMs);   // exactly on the timestamp: the earlier +120 ms lead made every line appear before it was sung
        if (idx != _lyricIndex)
        {
            if (_lyricIndex >= 0 && _lyricIndex < _lyricLines.Count) _lyricLines[_lyricIndex].IsActive = false;
            _lyricIndex = idx;
            if (idx >= 0) _lyricLines[idx].IsActive = true;

            ApplyLyricFocus(idx);
            if (idx >= 0 && DateTime.UtcNow >= _lyricManualUntil) ScrollToLyric(idx, immediate: false);
        }

        UpdateLyricWords(timeMs);
    }

    // ---- Apple-Music-style focus: the line being sung is bright, the others fade and blur with their distance from it

    private TextBlock? _wordBlock;                                  // the text of the active line while its words are lit one by one
    private readonly List<(Run Run, double Start)> _words = new();
    private int _wordsLit = -1;

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private TextBlock? LyricTextAt(int index) =>
        LyricsList.ItemContainerGenerator.ContainerFromIndex(index) is DependencyObject item ? FindChild<TextBlock>(item) : null;

    private int _bounceShown = -1;   // the line that was last made the active one (for the springy hand-over to the next)

    private static (ScaleTransform Scale, TranslateTransform Shift) LyricTransforms(TextBlock text)
    {
        if (text.RenderTransform is TransformGroup { Children.Count: 2 } group &&
            group.Children[0] is ScaleTransform scale && group.Children[1] is TranslateTransform shift)
            return (scale, shift);

        var newScale = new ScaleTransform(1, 1);
        var newShift = new TranslateTransform();
        text.RenderTransformOrigin = new Point(0, 0.5);   // lines grow from their left edge, like Apple Music
        var created = new TransformGroup();
        created.Children.Add(newScale);
        created.Children.Add(newShift);
        text.RenderTransform = created;
        return (newScale, newShift);
    }

    /// <summary>
    /// Apple Music style motion: the line being sung springs up to full size (an elastic overshoot), the others rest a little smaller, and
    /// when the song moves on to the next line the lines around it settle into place one after another - the further from the active line,
    /// the later and the longer - which makes the list feel stretchy. Only render transforms: nothing is re-laid-out, so it stays smooth.
    /// </summary>
    private void ApplyLyricBounce(TextBlock text, int distance, bool cascade)
    {
        if (!_cfg.LyricsBounce)
        {
            if (text.RenderTransform is TransformGroup) text.RenderTransform = Transform.Identity;
            return;
        }

        var (scale, shift) = LyricTransforms(text);
        double target = distance == 0 ? 1.0 : 0.92;

        if (distance > 6)
        {
            // far away: no animation, just the resting size
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = scale.ScaleY = target;
            shift.BeginAnimation(TranslateTransform.YProperty, null);
            shift.Y = 0;
            return;
        }

        IEasingFunction ease = distance == 0
            ? new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 5 }
            : new CubicEase { EasingMode = EasingMode.EaseOut };
        var span = TimeSpan.FromMilliseconds(distance == 0 ? 650 : 380);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(target, span) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(target, span) { EasingFunction = ease });

        if (cascade)
        {
            // the lines lag behind the scroll by a little more the further they are, then spring into place
            double lag = Math.Min(30, 5 + 4 * distance);
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(lag, 0, TimeSpan.FromMilliseconds(520 + distance * 80))
            { EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 6 } });
        }
        else if (shift.Y != 0 && !cascade)
        {
            shift.BeginAnimation(TranslateTransform.YProperty, null);
            shift.Y = 0;
        }
    }

    private void ApplyLyricFocus(int active)
    {
        bool cascade = active >= 0 && active == _bounceShown + 1;   // only when the song moves on by one line (a seek does not wobble)
        // the previous line's lit words go back to plain text (it fades with the others now)
        if (_wordBlock != null)
        {
            _wordBlock.Inlines.Clear();
            _wordBlock.Text = _wordBlock.Tag as string ?? "";
            _wordBlock = null;
            _words.Clear();
            _wordsLit = -1;
        }

        for (int i = 0; i < _lyricLines.Count; i++)
        {
            var text = LyricTextAt(i);
            if (text == null) continue;

            // lines further from the one being sung are dimmer (a blur effect looked better still, but it made the whole view lag)
            int d = active < 0 ? 3 : Math.Abs(i - active);
            double opacity = d == 0 ? 1.0 : Math.Max(0.16, 0.55 - 0.09 * (d - 1));

            if (d <= 5)
            {
                // the lines near the active one change gently
                text.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(300))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
            else if (Math.Abs(text.Opacity - opacity) > 0.01)
            {
                text.BeginAnimation(UIElement.OpacityProperty, null);
                text.Opacity = opacity;   // the far ones simply jump, and only when they really change
            }

            ApplyLyricBounce(text, d, cascade);
        }
        _bounceShown = active;

        if (active >= 0 && LyricTextAt(active) is { } activeText) PrepareWords(_lyricLines[active], activeText);
    }

    /// <summary>The active line is split into words, so they can light up as they are sung.</summary>
    private void PrepareWords(LyricLine line, TextBlock text)
    {
        // by default the whole line being sung is simply bright; the words only light up one by one if the setting is on
        if (!_cfg.LyricsWordByWord) return;

        _wordBlock = text;
        _wordsLit = -1;
        _words.Clear();
        text.Tag = line.Text;

        var parts = line.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        double total = parts.Sum(p => p.Length + 1);
        double acc = 0;
        text.Inlines.Clear();
        for (int i = 0; i < parts.Length; i++)
        {
            var run = new Run(parts[i] + (i < parts.Length - 1 ? " " : "")) { Foreground = WordDim };
            text.Inlines.Add(run);
            _words.Add((run, acc / total));
            acc += parts[i].Length + 1;
        }
    }

    private void UpdateLyricWords(long timeMs)
    {
        if (_wordBlock == null || _words.Count == 0 || _lyricIndex < 0 || _lyricIndex >= _lyricLines.Count) return;

        var line = _lyricLines[_lyricIndex];
        long next = _lyricIndex + 1 < _lyricLines.Count ? _lyricLines[_lyricIndex + 1].TimeMs : line.TimeMs + 4000;
        // the words are spread over most of the time until the next line (a singer rarely finishes a line early), but a long
        // instrumental pause after the line does not stretch it: about 85 ms per character at the most (a fast verse, not a ballad)
        double gap = Math.Max(1, next - line.TimeMs);
        double span = Math.Clamp(Math.Min(gap * 0.97, line.Text.Length * 85 + 500), 600, 12000);
        double progress = Math.Clamp((timeMs - line.TimeMs) / span, 0, 1);

        int lit = 0;
        while (lit < _words.Count && _words[lit].Start <= progress + 0.015) lit++;
        if (lit == _wordsLit) return;

        for (int i = 0; i < _words.Count; i++) _words[i].Run.Foreground = i < lit ? WordLit : WordDim;
        _wordsLit = lit;
    }

    private void SyncLyricsNow()
    {
        if (_player == null) return;
        _lyricClockValid = false;
        UpdateLyricsCore(IngestLyricTime(_player.Time, allowSnap: true));
    }

    private int FindLyricIndex(long timeMs)
    {
        int lo = 0, hi = _lyricLines.Count - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_lyricLines[mid].TimeMs <= timeMs) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        // a line only counts as "being sung" while it has text; gaps (empty lines) clear the highlight
        return best >= 0 && _lyricLines[best].Text.Length == 0 ? -1 : best;
    }

    private bool _scrollRetryQueued;

    private void ScrollToLyric(int index, bool immediate)
    {
        if (index < 0 || index >= _lyricLines.Count) return;

        _lyricsViewer ??= FindScrollViewer(LyricsList);
        var viewer = _lyricsViewer;
        if (viewer == null || viewer.ViewportHeight <= 0) return;
        if (LyricsList.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement item)
        {
            // the line's container is not made yet (the list was only just filled): try again once the layout has run
            if (!_scrollRetryQueued)
            {
                _scrollRetryQueued = true;
                Dispatcher.InvokeAsync(() => { _scrollRetryQueued = false; if (_lyricsOpen) ScrollToLyric(_lyricIndex >= 0 ? _lyricIndex : index, immediate); }, DispatcherPriority.Loaded);
            }
            return;
        }

        // where the line's middle is inside the viewport right now, then where the viewport has to move to centre it
        var centre = item.TranslatePoint(new Point(0, item.ActualHeight / 2), viewer);
        double target = viewer.VerticalOffset + centre.Y - viewer.ViewportHeight * 0.42;

        if (immediate) viewer.ScrollToVerticalOffset(Math.Max(0, target));
        else _lyricScroll?.Invoke(target);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    private void LyricsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: LyricLine line } && line.TimeMs >= 0 && _player != null && _hasMedia)
        {
            _player.Time = line.TimeMs;
            OnSeeked(line.TimeMs);
        }
    }
}
