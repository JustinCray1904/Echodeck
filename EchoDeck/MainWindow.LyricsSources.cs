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

public partial class MainWindow
{
    // ------------------------------------------------------------------ loading lyrics for a song

    /// <summary>Called when a song starts: finds its lyrics in the hidden lyrics folder, or an .lrc / .txt next to it (or lyrics made earlier this session).</summary>
    private async void LoadLyricsFor(string path, bool isAudio)
    {
        _lyricsMediaPath = isAudio ? path : null;
        _lyricClockValid = false;   // a new song: the lyrics clock starts again from the player's time
        _lyricOffsetMs = LyricsOffsets.Get(isAudio ? path : null);
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

        if (_lyrics != null && TempoDialog.Show(this, "Replace the lyrics shown for this song with AI-generated ones?", "Generate lyrics",
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
                TempoDialog.Show(this, coverage, "Lyrics may be incomplete", MessageBoxButton.OK, MessageBoxImage.Information);
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
            try { TempoDialog.Show(this, "Couldn't generate lyrics.\n\n" + ex.Message, "Generate lyrics", MessageBoxButton.OK, MessageBoxImage.Warning); }
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

        if (_lyrics != null && TempoDialog.Show(this, "Replace the lyrics shown for this song with the ones found online?", "Find lyrics online",
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
            else if (found.Approximate)
            {
                Notify("Found by title only: no upload has this song's length, so the timing may be off. Use the − and + buttons to adjust it.");
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
            if (TempoDialog.Show(this, message, "Generate lyrics", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
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
