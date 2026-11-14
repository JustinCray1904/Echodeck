using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace EchoDeck;

// Generating lyrics for several songs in the background: the player stays fully usable, a card in the side bar
// shows "n of N" and the progress, and every result is saved next to its song as a .lrc file.
public partial class MainWindow
{
    private CancellationTokenSource? _batchCts;
    private int _batchTotal, _batchDone, _batchFailed, _batchSkipped;
    private string _batchCurrent = "";
    private double _batchFraction;
    private bool _cloudConsent;   // the user agreed to upload audio during this run of the app

    private bool BatchRunning => _batchCts != null;

    // ------------------------------------------------------------------ entry points

    private void SelMore_Click(object sender, RoutedEventArgs e)
    {
        var songs = CheckedFiles().Where(x => x.Kind == EntryKind.Audio).Select(x => x.FullPath).ToList();

        var menu = new ContextMenu { PlacementTarget = SelMoreButton, Placement = PlacementMode.Top };
        var label = songs.Count == 1 ? "Generate lyrics for this song" : $"Generate lyrics for {songs.Count} songs";
        var generate = MenuEntry(label, "IcoMusic", (_, _) => StartLyricsBatch(songs));
        generate.IsEnabled = songs.Count > 0 && !_anon;
        menu.Items.Add(generate);

        if (_anon)
            menu.Items.Add(new MenuItem { Header = "Not available in Anonymous Mode", IsEnabled = false });
        else if (songs.Count == 0)
            menu.Items.Add(new MenuItem { Header = "Tick some songs first", IsEnabled = false });

        menu.IsOpen = true;
    }

    /// <summary>Queues songs for lyrics generation. Songs that already have a .lrc next to them are skipped.</summary>
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
                if (File.Exists(Path.ChangeExtension(path, ".lrc"))) _batchSkipped++;
                else todo.Add(path);
            }

            if (todo.Count == 0)
            {
                Notify(_batchSkipped > 0 ? "Those songs already have lyrics next to them." : "Nothing to generate.");
                return;
            }

            _batchCts = new CancellationTokenSource();
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
                File.WriteAllText(Path.ChangeExtension(path, ".lrc"), text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                _sessionLyrics[path] = text;
                _batchDone++;

                var warn = WhisperService.CoverageWarning(text, LibraryScanner.ReadTags(path).DurationSeconds);
                if (warn != null) warnings.Add("“" + _batchCurrent + "”: " + warn);

                // if this is the song on screen right now, show its new lyrics at once
                if (string.Equals(_lyricsMediaPath, path, StringComparison.OrdinalIgnoreCase)) ShowLyrics(LyricsParser.Parse(text, Path.ChangeExtension(path, ".lrc")));
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

        BatchCountText.Text = $"Song {shown} of {_batchTotal}  ·  {_batchDone} done" + (_batchFailed > 0 ? $", {_batchFailed} failed" : "");
        BatchFileText.Text = _batchCurrent;
        BatchBadge.Text = $"{shown}/{_batchTotal}";
        BatchBadge.ToolTip = "Generating lyrics: " + _batchCurrent;

        double overall = _batchTotal == 0 ? 0 : (_batchDone + _batchFailed + Math.Clamp(_batchFraction, 0, 1)) / _batchTotal;
        BatchFill.Width = Math.Max(0, BatchTrack.ActualWidth * Math.Clamp(overall, 0, 1));
    }

    private void BatchCancel_Click(object sender, RoutedEventArgs e) => _batchCts?.Cancel();
}
