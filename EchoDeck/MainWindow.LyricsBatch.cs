using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace EchoDeck;

// Finding (lrclib.net) or generating (Whisper) lyrics for several songs in the background: the player stays fully usable,
// a card in the side bar shows "n of N" and the progress, and every result is saved in Tempo's hidden lyrics folder.
public partial class MainWindow
{
    private CancellationTokenSource? _batchCts;
    private int _batchTotal, _batchDone, _batchFailed, _batchSkipped;
    private string _batchCurrent = "";
    private string _batchTitle = "Generating lyrics";
    private int _batchSynced, _batchPlain;
    private double _batchFraction;
    private bool _cloudConsent;   // the user agreed to upload audio during this run of the app

    private bool BatchRunning => _batchCts != null;

    // ------------------------------------------------------------------ entry points

    private void SelMore_Click(object sender, RoutedEventArgs e)
    {
        var songs = CheckedFiles().Where(x => x.Kind == EntryKind.Audio).Select(x => x.FullPath).ToList();

        var menu = new ContextMenu { PlacementTarget = SelMoreButton, Placement = PlacementMode.Top };
        var label = songs.Count == 1 ? "Generate lyrics for this song" : $"Generate lyrics for {songs.Count} songs";
        var findOnline = MenuEntry(songs.Count == 1 ? "Find lyrics online" : $"Find lyrics online for {songs.Count} songs", "IcoSearch",
            (_, _) => StartOnlineLyricsBatch(songs));
        findOnline.IsEnabled = songs.Count > 0 && !_anon;
        menu.Items.Add(findOnline);

        var generate = MenuEntry(label, "IcoMusic", (_, _) => StartLyricsBatch(songs));
        generate.IsEnabled = songs.Count > 0 && !_anon;
        menu.Items.Add(generate);

        if (_anon)
            menu.Items.Add(new MenuItem { Header = "Not available in Anonymous Mode", IsEnabled = false });
        else if (songs.Count == 0)
            menu.Items.Add(new MenuItem { Header = "Tick some songs first", IsEnabled = false });

        menu.IsOpen = true;
    }

    /// <summary>Queues songs for lyrics generation. Songs that already have lyrics are skipped.</summary>
    private async void StartLyricsBatch(IReadOnlyList<string> songs)
    {
        try
        {
            if (_anon)
            {
                Notify("Lyrics generation isn't available in Anonymous Mode: it would need unencrypted copies of the songs.");
                return;
            }
            if (BatchRunning || _lyricsCts != null)
            {
                Notify("Lyrics are already being generated. Wait for it to finish, or cancel it first.");
                return;
            }

            var notReady = LyricsGenerator.NotReadyReason(_cfg);
            if (notReady != null)
            {
                OfferWhisperSettings(notReady + "\n\nOpen the Lyrics settings?");
                return;
            }
            if (!ConfirmCloudUpload(songs.Count)) return;

            var todo = new List<string>();
            _batchSkipped = 0;
            foreach (var path in songs.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (LyricsFiles.Find(path) != null) _batchSkipped++;
                else todo.Add(path);
            }

            if (todo.Count == 0)
            {
                Notify(_batchSkipped > 0 ? "Those songs already have lyrics." : "Nothing to generate.");
                return;
            }

            _batchCts = new CancellationTokenSource();
            _batchTitle = "Generating lyrics";
            _batchTotal = todo.Count;
            _batchDone = 0;
            _batchFailed = 0;
            _batchFraction = 0;
            UpdateBatchUi();
            Notify($"Generating lyrics for {todo.Count} song{(todo.Count == 1 ? "" : "s")} in the background" +
                   (_batchSkipped > 0 ? $" ({_batchSkipped} already had lyrics)" : ""));

            await RunBatchAsync(todo, _batchCts.Token);
        }
        catch (Exception ex)
        {
            AppLog.Write("Lyrics batch", ex);
            Notify("Generating lyrics stopped: " + ex.Message);
        }
        finally
        {
            _batchCts?.Dispose();
            _batchCts = null;
            UpdateBatchUi();
        }
    }

    private async Task RunBatchAsync(List<string> songs, CancellationToken ct)
    {
        string? lastProblem = null;
        var warnings = new List<string>();

        foreach (var path in songs)
        {
            if (ct.IsCancellationRequested) break;

            _batchCurrent = Path.GetFileNameWithoutExtension(path);
            _batchFraction = 0;
            UpdateBatchUi();

            var progress = new Progress<(double Fraction, string Text)>(p =>
            {
                _batchFraction = p.Fraction;
                UpdateBatchUi();
            });

            try
            {
                var text = (await LyricsGenerator.GenerateAsync(path, _cfg, progress, ct)).Replace("\r\n", "\n").Replace("\n", "\r\n");
                var savedTo = LyricsFiles.Save(path, text);
                _sessionLyrics[path] = text;
                _batchDone++;

                var warn = WhisperService.CoverageWarning(text, LibraryScanner.ReadTags(path).DurationSeconds);
                if (warn != null) warnings.Add("“" + _batchCurrent + "”: " + warn);

                // if this is the song on screen right now, show its new lyrics at once
                if (string.Equals(_lyricsMediaPath, path, StringComparison.OrdinalIgnoreCase)) ShowLyrics(LyricsParser.Parse(text, savedTo));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (WhisperException ex) when (ex.SetupProblem)
            {
                // the same problem would hit every remaining song: stop and say so
                lastProblem = ex.Message;
                _batchFailed++;
                break;
            }
            catch (Exception ex)
            {
                AppLog.Write("Lyrics batch", ex, path);
                _batchFailed++;
                lastProblem = ex.Message;
            }

            _batchFraction = 1;
            UpdateBatchUi();
        }

        string summary = ct.IsCancellationRequested
            ? $"Lyrics generation cancelled: {_batchDone} of {_batchTotal} done"
            : $"Lyrics ready for {_batchDone} of {_batchTotal} song{(_batchTotal == 1 ? "" : "s")}";
        if (_batchFailed > 0) summary += $", {_batchFailed} failed" + (lastProblem != null ? " (" + Truncate(lastProblem, 90) + ")" : "");
        Notify(summary);

        if (warnings.Count > 0 && !ct.IsCancellationRequested)
        {
            try
            {
                MessageBox.Show(this, string.Join("\n\n", warnings.Take(5)) + (warnings.Count > 5 ? $"\n\n…and {warnings.Count - 5} more." : ""),
                    "Lyrics may be incomplete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch { /* window closing */ }
        }
    }

    // ------------------------------------------------------------------ online batch (lrclib.net)

    /// <summary>
    /// Looks every ticked song up at lrclib.net, one after the other (a short pause between requests keeps the free service happy).
    /// Only the title, artist, album and length of each song are sent. Songs that already have lyrics are skipped.
    /// </summary>
    private async void StartOnlineLyricsBatch(IReadOnlyList<string> songs)
    {
        try
        {
            if (_anon)
            {
                Notify("Online lookup isn't available in Anonymous Mode: it would send song names over the internet.");
                return;
            }
            if (BatchRunning || _lyricsCts != null)
            {
                Notify("Lyrics are already being generated. Wait for it to finish, or cancel it first.");
                return;
            }

            var todo = new List<string>();
            _batchSkipped = 0;
            foreach (var path in songs.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (LyricsFiles.Find(path) != null) _batchSkipped++;
                else todo.Add(path);
            }

            if (todo.Count == 0)
            {
                Notify(_batchSkipped > 0 ? "Those songs already have lyrics." : "Nothing to look up.");
                return;
            }

            _batchCts = new CancellationTokenSource();
            _batchTitle = "Finding lyrics online";
            _batchTotal = todo.Count;
            _batchDone = 0;
            _batchFailed = 0;
            _batchSynced = 0;
            _batchPlain = 0;
            _batchFraction = 0;
            UpdateBatchUi();
            Notify($"Looking up lyrics for {todo.Count} song{(todo.Count == 1 ? "" : "s")} at lrclib.net" +
                   (_batchSkipped > 0 ? $" ({_batchSkipped} already had lyrics)" : ""));

            await RunOnlineBatchAsync(todo, _batchCts.Token);
        }
        catch (Exception ex)
        {
            AppLog.Write("Online lyrics batch", ex);
            Notify("Looking up lyrics stopped: " + ex.Message);
        }
        finally
        {
            _batchCts?.Dispose();
            _batchCts = null;
            UpdateBatchUi();
        }
    }

    private async Task RunOnlineBatchAsync(List<string> songs, CancellationToken ct)
    {
        int notFound = 0;          // looked up fine, but lrclib has nothing for the song
        int networkFailures = 0;   // in a row: the same problem would hit every remaining song
        string? lastProblem = null;

        foreach (var path in songs)
        {
            if (ct.IsCancellationRequested) break;

            _batchCurrent = Path.GetFileNameWithoutExtension(path);
            _batchFraction = 0.1;
            UpdateBatchUi();

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(25));

                var tags = await Task.Run(() => LibraryScanner.ReadTags(path), timeout.Token);
                _batchFraction = 0.4;
                UpdateBatchUi();

                var found = await OnlineLyrics.FindAsync(path, tags, timeout.Token);
                networkFailures = 0;

                if (found == null)
                {
                    notFound++;
                }
                else
                {
                    var text = found.Text.Replace("\r\n", "\n").Replace("\n", "\r\n");
                    var savedTo = LyricsFiles.Save(path, text);
                    _sessionLyrics[path] = text;
                    _batchDone++;
                    if (found.Synced) _batchSynced++; else _batchPlain++;

                    // if this is the song on screen right now, show its new lyrics at once
                    if (string.Equals(_lyricsMediaPath, path, StringComparison.OrdinalIgnoreCase))
                        ShowLyrics(LyricsParser.Parse(text, savedTo));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // this one song took too long: count it and go on
                _batchFailed++;
                networkFailures++;
                lastProblem = "the service took too long";
            }
            catch (Exception ex)
            {
                AppLog.Write("Online lyrics batch", ex, path);
                _batchFailed++;
                networkFailures++;
                lastProblem = ex.Message;
            }

            _batchFraction = 1;
            UpdateBatchUi();

            if (networkFailures >= 3)
            {
                lastProblem = "can't reach lrclib.net" + (lastProblem != null ? " (" + Truncate(lastProblem, 60) + ")" : "");
                break;
            }

            try { await Task.Delay(300, ct); }   // be polite to the free service
            catch (OperationCanceledException) { break; }
        }

        string summary = ct.IsCancellationRequested
            ? $"Cancelled: lyrics found for {_batchDone} of {_batchTotal}"
            : $"Lyrics found for {_batchDone} of {_batchTotal} song{(_batchTotal == 1 ? "" : "s")}";
        if (_batchPlain > 0) summary += $" ({_batchSynced} timed, {_batchPlain} without timing)";
        if (notFound > 0) summary += $", {notFound} not found";
        if (_batchFailed > 0) summary += $", {_batchFailed} failed";
        if (networkFailures >= 3 && lastProblem != null) summary += ". Stopped: " + lastProblem;
        Notify(summary);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

    /// <summary>Cloud engine: say clearly, once per run, that the audio leaves the PC.</summary>
    private bool ConfirmCloudUpload(int songCount)
    {
        if (!LyricsGenerator.IsCloud(_cfg) || _cloudConsent) return true;

        string host;
        try { host = new Uri(_cfg.CloudEndpoint).Host; } catch { host = _cfg.CloudEndpoint; }

        var answer = MessageBox.Show(this,
            $"Cloud lyrics upload the audio of {(songCount == 1 ? "this song" : "these songs")} to {host}.\n\nContinue?",
            "Generate lyrics", MessageBoxButton.YesNo, MessageBoxImage.Question);
        _cloudConsent = answer == MessageBoxResult.Yes;
        return _cloudConsent;
    }

    // ------------------------------------------------------------------ side bar card

    private void UpdateBatchUi()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(UpdateBatchUi); return; }

        bool running = BatchRunning;
        int shown = Math.Min(_batchTotal, _batchDone + _batchFailed + 1);

        // the wide card on the pages, a tiny "3/10" next to the narrow side bar beside the player
        BatchCard.Visibility = running && _libraryVisible ? Visibility.Visible : Visibility.Collapsed;
        BatchBadge.Visibility = running && !_libraryVisible ? Visibility.Visible : Visibility.Collapsed;
        if (!running) return;

        BatchTitleText.Text = _batchTitle;
        BatchCountText.Text = $"Song {shown} of {_batchTotal}  ·  {_batchDone} done" + (_batchFailed > 0 ? $", {_batchFailed} failed" : "");
        BatchFileText.Text = _batchCurrent;
        BatchBadge.Text = $"{shown}/{_batchTotal}";
        BatchBadge.ToolTip = _batchTitle + ": " + _batchCurrent;

        double overall = _batchTotal == 0 ? 0 : (_batchDone + _batchFailed + Math.Clamp(_batchFraction, 0, 1)) / _batchTotal;
        BatchFill.Width = Math.Max(0, BatchTrack.ActualWidth * Math.Clamp(overall, 0, 1));
    }

    private void BatchCancel_Click(object sender, RoutedEventArgs e) => _batchCts?.Cancel();
}
