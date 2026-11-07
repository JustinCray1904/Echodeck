using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace EchoDeck;

// Lyrics: auto-loaded .lrc / .txt next to the song, editor + paste, local Whisper generation, and the synced display.
public partial class MainWindow
{
    private const double LyricsPanelMargin = 28;

    /// <summary>
    /// The cover and the visualiser keep their size; the lyrics take what is left beside them (at most 480 wide, at least 320).
    /// Only when even that does not fit does the cover scale down.
    /// </summary>
    private double ComputeLyricsColumn()
    {
        double avail = MusicPanel.ActualWidth;
        if (avail <= 0) return 480 + LyricsPanelMargin;
        const double coverNeeds = 500;
        return Math.Clamp(avail - coverNeeds - LyricsPanelMargin, 320, 480) + LyricsPanelMargin;
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

        LyricsToggle.SetResourceReference(Control.ForegroundProperty, open ? "AccentBrush" : "TextBrush");
        if (persist)
        {
            _cfg.LyricsOpen = open;
            if (!_anon) _cfg.Save(notify: false);
        }

        double from = LyricsCol.Width.Value;
        double full = ComputeLyricsColumn();
        double to = open ? full : 0;
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
        });
    }

    private void LyricsToggle_Click(object sender, RoutedEventArgs e) => SetLyricsOpen(!_lyricsOpen);
    private void LyricsClose_Click(object sender, RoutedEventArgs e) => SetLyricsOpen(false);

    // ------------------------------------------------------------------ loading lyrics for a song

    /// <summary>Called when a song starts: finds the .lrc / .txt with the same name next to it (or lyrics made earlier this session).</summary>
    private async void LoadLyricsFor(string path, bool isAudio)
    {
        _lyricsMediaPath = isAudio ? path : null;
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
                : "No .lrc or .txt file with this song's name was found next to it. Paste the lyrics, or generate them with AI.";

        if (any && _lyricsOpen)
            Dispatcher.InvokeAsync(() => { SyncLyricsNow(); ScrollToLyric(Math.Max(0, _lyricIndex), immediate: true); }, DispatcherPriority.Loaded);
    }

    private void ClearLyrics()
    {
        _lyricsMediaPath = null;
        ShowLyrics(null);
    }

    // ------------------------------------------------------------------ following the song

    /// <summary>Highlights the line being sung and glides the list to it. Called from the player's time updates.</summary>
    private void UpdateLyrics(long timeMs)
    {
        if (!_lyricsOpen || _lyrics is not { Synced: true } || _lyricLines.Count == 0) return;

        int idx = FindLyricIndex(timeMs + 120);   // a hair early reads better than a hair late
        if (idx == _lyricIndex) return;

        if (_lyricIndex >= 0 && _lyricIndex < _lyricLines.Count) _lyricLines[_lyricIndex].IsActive = false;
        _lyricIndex = idx;
        if (idx >= 0) _lyricLines[idx].IsActive = true;

        if (idx >= 0 && DateTime.UtcNow >= _lyricManualUntil) ScrollToLyric(idx, immediate: false);
    }

    private void SyncLyricsNow()
    {
        if (_player != null) UpdateLyrics(_player.Time);
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

    private void ScrollToLyric(int index, bool immediate)
    {
        if (index < 0 || index >= _lyricLines.Count) return;

        _lyricsViewer ??= FindScrollViewer(LyricsList);
        var viewer = _lyricsViewer;
        if (viewer == null || viewer.ViewportHeight <= 0) return;
        if (LyricsList.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement item) return;

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
            _lyricManualUntil = DateTime.MinValue;
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
            : "Saved next to the song with the same name (.lrc when timed, .txt for plain lyrics).";

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
                var target = LyricsFiles.SavePathFor(mediaPath, text);
                File.WriteAllText(target, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                sourcePath = target;

                // an older file of the other kind would still win when the song is loaded again: remove the one that was showing
                var old = _lyrics?.SourcePath;
                if (old != null && !string.Equals(old, target, StringComparison.OrdinalIgnoreCase) && File.Exists(old)) File.Delete(old);
            }
            catch (Exception ex)
            {
                return "Couldn't save next to the song: " + ex.Message;
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
                    File.WriteAllText(Path.ChangeExtension(path, ".lrc"), text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                    note = " and saved next to the song";
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
