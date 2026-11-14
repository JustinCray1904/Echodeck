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

    // ------------------------------------------------------------------ loading lyrics for a song

    /// <summary>Called when a song starts: finds its lyrics in the hidden lyrics folder, or an .lrc / .txt next to it (or lyrics made earlier this session).</summary>
    private async void LoadLyricsFor(string path, bool isAudio)
    {
        _lyricsMediaPath = isAudio ? path : null;
        _lyricClockValid = false;   // a new song: the lyrics clock starts again from the player's time
        UpdateLyricsBusyVisibility();

        if (!isAudio)
        {
            ShowLyrics(null);
            return;
        }

        // lyrics typed or generated earlier in this run
        if (LyricStore.TryGetValue(path, out var remembered))
        {
            ShowLyrics(LyricsParser.Parse(remembered));
            return;
        }

        ShowLyrics(null);   // clear the previous song's lyrics straight away
        if (_anon || !_cfg.AutoLoadLyrics) return;

        LyricsDocument? doc = await Task.Run(() =>
        {
            var file = LyricsFiles.Find(path);
            return file == null ? null : LyricsFiles.Load(file);
        });

        if (!string.Equals(_lyricsMediaPath, path, StringComparison.OrdinalIgnoreCase)) return;   // another song started meanwhile
        if (doc != null) ShowLyrics(doc);
    }

    private void ShowLyrics(LyricsDocument? doc)
    {
        _lyrics = doc != null && !doc.IsEmpty ? doc : null;
        _wordBlock = null;   // its container is about to go away
        _words.Clear();
        _wordsLit = -1;
        _lyricLines.Clear();
        _lyricIndex = -1;

        if (_lyrics != null)
            foreach (var line in _lyrics.Lines) _lyricLines.Add(line);

        bool any = _lyricLines.Count > 0;
        LyricsEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        LyricsEmptyText.Text = !_cfg.AutoLoadLyrics && !_anon
            ? "Automatic lyric loading is off (Settings > Lyrics). You can still paste lyrics or generate them."
            : _anon
                ? "No lyrics for this song. Anything you add in Anonymous Mode is kept in memory only."
                : "No lyrics for this song yet. Find them online, paste them, or generate them with AI.";

        if (any && _lyricsOpen)
            Dispatcher.InvokeAsync(() => { SyncLyricsNow(); ScrollToLyric(Math.Max(0, _lyricIndex), immediate: true); }, DispatcherPriority.Loaded);
    }

    private void ClearLyrics()
    {
        _lyricsMediaPath = null;
        ShowLyrics(null);
    }

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

    private void UpdateLyricsCore(long timeMs)
    {
        if (!_lyricsOpen || _lyrics is not { Synced: true } || _lyricLines.Count == 0) return;

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

    private void ApplyLyricFocus(int active)
    {
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
        }

        if (active >= 0 && LyricTextAt(active) is { } activeText) PrepareWords(_lyricLines[active], activeText);
    }

    /// <summary>The active line is split into words, so they can light up as they are sung.</summary>
    private void PrepareWords(LyricLine line, TextBlock text)
    {
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

    // ------------------------------------------------------------------ editor

    private void LyricsEdit_Click(object sender, RoutedEventArgs e)
    {
        var path = _lyricsMediaPath;
        if (path == null || !_hasMedia)
        {
            Notify("Play a song first, then edit its lyrics.");
            return;
        }

        if (_lyricsEditor != null)
        {
            _lyricsEditor.Activate();
            return;
        }

        string name = _anon ? MusicTitle.Text : Path.GetFileNameWithoutExtension(path);
        string hint = _anon
            ? "Anonymous Mode: lyrics are kept in memory only and forgotten when you leave."
            : "Saved in Tempo's hidden lyrics folder, not next to the song (.lrc when timed, .txt for plain lyrics).";

        _lyricsEditor = new LyricsEditorWindow(name, _lyrics?.RawText ?? "", hint,
            () => _player?.Time ?? 0,
            () => TogglePlayPause(),
            delta => { if (_player != null && _hasMedia) _player.Time = Math.Max(0, _player.Time + delta); },
            text => SaveLyrics(path, text))
        { Owner = this };
        _lyricsEditor.Closed += (_, _) => _lyricsEditor = null;
        _lyricsEditor.Show();
    }

    /// <summary>Returns null on success, otherwise a message for the editor to show.</summary>
    private string? SaveLyrics(string mediaPath, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "The lyrics are empty. Paste or type something first.";
        text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");

        string? sourcePath = null;
        if (!_anon)
        {
            try
            {
                sourcePath = LyricsFiles.Save(mediaPath, text);   // the hidden Tempo folder: it also drops the other kind (.lrc / .txt) kept there
            }
            catch (Exception ex)
            {
                return "Couldn't save the lyrics: " + ex.Message;
            }
        }

        LyricStore[mediaPath] = text;
        if (string.Equals(_lyricsMediaPath, mediaPath, StringComparison.OrdinalIgnoreCase))
        {
            var doc = LyricsParser.Parse(text, sourcePath);
            ShowLyrics(doc);
            SetLyricsOpen(true);
        }
        Notify(_anon ? "Lyrics kept for this session" : "Lyrics saved");
        return null;
    }

    // ------------------------------------------------------------------ Whisper (local, optional)

    private async void LyricsAi_Click(object sender, RoutedEventArgs e)
    {
        if (_anon)
        {
            Notify("AI lyrics aren't available in Anonymous Mode: they would need an unencrypted copy of the song.");
            return;
        }

        var path = _lyricsMediaPath;
        if (path == null || !_hasMedia || !MediaTypes.IsAudio(path))
        {
            Notify("Play a song first, then generate its lyrics.");
            return;
        }

        if (_lyricsCts != null || BatchRunning)
        {
            Notify("Lyrics are already being generated.");
            return;
        }

        var notReady = LyricsGenerator.NotReadyReason(_cfg);
        if (notReady != null)
        {
            OfferWhisperSettings(notReady + "\n\nOpen the Lyrics settings to set it up?");
            return;
        }
        if (!ConfirmCloudUpload(1)) return;

        if (_lyrics != null && MessageBox.Show(this, "Replace the lyrics shown for this song with AI-generated ones?", "Generate lyrics",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        var cts = _lyricsCts = new CancellationTokenSource();
        _lyricsBusyPath = path;
        UpdateLyricsBusyVisibility();
        SetLyricsBusy("Starting…", 0);

        var progress = new Progress<(double Fraction, string Text)>(p =>
        {
            if (string.Equals(_lyricsBusyPath, _lyricsMediaPath, StringComparison.OrdinalIgnoreCase)) SetLyricsBusy(p.Text, p.Fraction);
        });

        try
        {
            var text = await LyricsGenerator.GenerateAsync(path, _cfg, progress, cts.Token);
            text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
            _sessionLyrics[path] = text;

            string note = "";
            if (_cfg.SaveGeneratedLyrics)
            {
                try
                {
                    LyricsFiles.Save(path, text);
                    note = " and saved";
                }
                catch (Exception ex)
                {
                    note = " (couldn't save the file: " + ex.Message + ")";
                }
            }

            var coverage = WhisperService.CoverageWarning(text, LibraryScanner.ReadTags(path).DurationSeconds);

            if (string.Equals(_lyricsMediaPath, path, StringComparison.OrdinalIgnoreCase))
            {
                ShowLyrics(LyricsParser.Parse(text, null));
                SetLyricsOpen(true);
                Notify("Lyrics generated" + note);
            }
            else
            {
                Notify("Lyrics for “" + Path.GetFileNameWithoutExtension(path) + "” are ready" + note);
            }

            if (coverage != null)
                MessageBox.Show(this, coverage, "Lyrics may be incomplete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            Notify("Lyrics generation cancelled");
        }
        catch (WhisperException ex) when (ex.SetupProblem)
        {
            OfferWhisperSettings(ex.Message + "\n\nOpen the Lyrics settings?");
        }
        catch (Exception ex)
        {
            // a failed transcription (or anything unexpected) is reported, never allowed to reach the crash handler
            AppLog.Write("Generate lyrics", ex);
            try { MessageBox.Show(this, "Couldn't generate lyrics.\n\n" + ex.Message, "Generate lyrics", MessageBoxButton.OK, MessageBoxImage.Warning); }
            catch { /* window closing */ }
        }
        finally
        {
            _lyricsCts = null;
            _lyricsBusyPath = null;
            UpdateLyricsBusyVisibility();
        }
    }

    // ------------------------------------------------------------------ online (lrclib.net)

    private bool _lyricsOnlineBusy;

    private async void LyricsOnline_Click(object sender, RoutedEventArgs e)
    {
        if (_anon)
        {
            Notify("Online lookup isn't available in Anonymous Mode: it would send the song's name over the internet.");
            return;
        }

        var path = _lyricsMediaPath;
        if (path == null || !_hasMedia || !MediaTypes.IsAudio(path))
        {
            Notify("Play a song first, then look up its lyrics.");
            return;
        }
        if (_lyricsOnlineBusy) return;

        if (_lyrics != null && MessageBox.Show(this, "Replace the lyrics shown for this song with the ones found online?", "Find lyrics online",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _lyricsOnlineBusy = true;
        LyricsOnlineButton.IsEnabled = false;
        Notify("Searching lrclib.net…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var tags = await Task.Run(() => LibraryScanner.ReadTags(path), cts.Token);
            var found = await OnlineLyrics.FindAsync(path, tags, cts.Token);
            if (found == null)
            {
                Notify("No lyrics found online for this song.");
                return;
            }

            // same path as pasted lyrics: saved in the hidden lyrics folder (.lrc when timed, .txt when plain) and shown straight away
            string? error = SaveLyrics(path, found.Text);
            if (error != null)
            {
                LyricStore[path] = found.Text;
                if (string.Equals(_lyricsMediaPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    ShowLyrics(LyricsParser.Parse(found.Text, null));
                    SetLyricsOpen(true);
                }
                Notify("Lyrics found, but they couldn't be saved");
            }
            else if (!found.Synced)
            {
                Notify("Found lyrics without timing: they can't follow the song");
            }
        }
        catch (OperationCanceledException)
        {
            Notify("The lyrics search took too long");
        }
        catch (Exception ex)
        {
            AppLog.Write("Online lyrics", ex);
            Notify("Couldn't reach the lyrics service (" + ex.Message + ")");
        }
        finally
        {
            _lyricsOnlineBusy = false;
            LyricsOnlineButton.IsEnabled = true;
        }
    }

    private void OfferWhisperSettings(string message)
    {
        try
        {
            if (MessageBox.Show(this, message, "Generate lyrics", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                ShowSettings("lyrics");
        }
        catch (Exception ex) { AppLog.Write("Whisper prompt", ex); }
    }

    private void LyricsCancel_Click(object sender, RoutedEventArgs e) => _lyricsCts?.Cancel();

    private void UpdateLyricsBusyVisibility()
    {
        bool show = _lyricsBusyPath != null && string.Equals(_lyricsBusyPath, _lyricsMediaPath, StringComparison.OrdinalIgnoreCase);
        LyricsBusy.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        LyricsAiButton.IsEnabled = _lyricsBusyPath == null;
    }

    private void SetLyricsBusy(string text, double fraction)
    {
        LyricsBusyText.Text = text;
        LyricsBusyFill.Width = Math.Max(0, LyricsBusyTrack.ActualWidth * Math.Clamp(fraction, 0, 1));
    }
}
