using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NAudio.Wave;

namespace EchoDeck;

/// <summary>A problem with the AI setup that the user can understand and fix. The message is meant to be shown as is.</summary>
internal sealed class WhisperException : Exception
{
    public WhisperException(string message, bool setupProblem = false, Exception? inner = null) : base(message, inner)
    {
        SetupProblem = setupProblem;
    }

    /// <summary>True when the program or model is missing or unusable (Settings > Lyrics can fix it).</summary>
    public bool SetupProblem { get; }
}

/// <summary>
/// Optional local speech-to-text for lyrics: runs the whisper.cpp command line program on this PC (nothing is uploaded).
/// The song is first converted to the 16 kHz mono WAV that whisper.cpp expects, in a temporary folder that is removed afterwards.
/// </summary>
internal static class WhisperService
{
    private static readonly Regex ProgressLine = new(@"progress\s*=\s*(\d{1,3})\s*%", RegexOptions.Compiled);

    public static string DefaultFolder => Path.Combine(AppPaths.DataDir, "whisper");

    /// <summary>Uses the configured paths, or looks in %LOCALAPPDATA%\Tempo\whisper for whisper-cli.exe / main.exe and a ggml-*.bin model.</summary>
    public static (string? Exe, string? Model) Resolve(AppSettings cfg)
    {
        string? exe = File.Exists(cfg.WhisperExePath) ? cfg.WhisperExePath : null;

        // Recent whisper.cpp releases ship main.exe only as a stub that prints "deprecated" and exits: use whisper-cli.exe beside it
        if (exe != null && IsDeprecatedStub(exe))
        {
            var sibling = Path.Combine(Path.GetDirectoryName(exe) ?? "", "whisper-cli.exe");
            if (File.Exists(sibling)) exe = sibling;
        }
        string? model = File.Exists(cfg.WhisperModelPath) ? cfg.WhisperModelPath : null;

        try
        {
            var dir = DefaultFolder;
            if (exe == null && Directory.Exists(dir))
            {
                foreach (var name in new[] { "whisper-cli.exe", "main.exe" })
                {
                    var hit = Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
                    if (hit != null) { exe = hit; break; }
                }
            }
            if (model == null && Directory.Exists(dir))
                model = Directory.EnumerateFiles(dir, "ggml-*.bin", SearchOption.AllDirectories).OrderBy(f => new FileInfo(f).Length).FirstOrDefault();
        }
        catch { /* unreadable folder */ }

        return (exe, model);
    }

    public static bool IsReady(AppSettings cfg)
    {
        var (exe, model) = Resolve(cfg);
        return exe != null && !IsDeprecatedStub(exe) && model != null;
    }

    /// <summary>True for the old "main.exe" that newer whisper.cpp releases keep only as a do-nothing deprecation notice.</summary>
    public static bool IsDeprecatedStub(string? exePath) =>
        !string.IsNullOrEmpty(exePath) && string.Equals(Path.GetFileName(exePath), "main.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A warning when the transcript clearly stops well before the song does (whisper often gives up on musical passages),
    /// otherwise null. <paramref name="durationSeconds"/> 0 means unknown.
    /// </summary>
    public static string? CoverageWarning(string lrcText, long durationSeconds)
    {
        if (durationSeconds < 30) return null;
        var doc = LyricsParser.Parse(lrcText);
        var last = doc.Lines.LastOrDefault(l => l.Text.Length > 0 && l.TimeMs >= 0);
        if (last == null) return null;

        double covered = last.TimeMs / 1000.0 / durationSeconds;
        if (covered >= 0.72) return null;
        return $"Only the first {LibraryScanner.FormatDuration((long)(last.TimeMs / 1000))} of {LibraryScanner.FormatDuration(durationSeconds)} was transcribed. " +
               "A larger model, or the cloud option, usually does better with music.";
    }

    private const long MinModelBytes = 1_000_000;

    /// <summary>
    /// Null when the model file looks usable, otherwise a short reason. Cheap (it reads four bytes): it catches a deleted,
    /// truncated or wrong file before whisper.cpp is started on it.
    /// </summary>
    public static string? CheckModel(string? model)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(model) || !File.Exists(model)) return "Model missing";
            if (new FileInfo(model).Length < MinModelBytes) return "Model file is incomplete";

            var magic = new byte[4];
            using (var fs = new FileStream(model, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                if (fs.Read(magic, 0, 4) < 4) return "Model file is incomplete";

            // whisper.cpp models start with the "ggml" tag (stored little-endian); newer builds may write "GGUF"
            bool ggml = magic[0] == 0x6C && magic[1] == 0x6D && magic[2] == 0x67 && magic[3] == 0x67;
            bool gguf = magic[0] == 'G' && magic[1] == 'G' && magic[2] == 'U' && magic[3] == 'F';
            return ggml || gguf ? null : "Not a ggml model file";
        }
        catch (Exception ex)
        {
            AppLog.Write("Whisper model check", ex);
            return "Model can't be read";
        }
    }

    /// <summary>
    /// Transcribes a song and returns the result as .lrc text. Never throws anything but <see cref="WhisperException"/>
    /// (with a message fit for the user) or <see cref="OperationCanceledException"/>. whisper.cpp runs in its own process, so a
    /// bad model can make that process fail but cannot take Tempo down.
    /// </summary>
    public static async Task<string> TranscribeAsync(string audioPath, AppSettings cfg, IProgress<(double Fraction, string Text)>? progress, CancellationToken ct)
    {
        try
        {
            return await TranscribeCoreAsync(audioPath, cfg, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (WhisperException ex)
        {
            AppLog.Write("Whisper", ex);
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Write("Whisper", ex, "unexpected");
            throw new WhisperException("Generating lyrics failed unexpectedly: " + ex.Message, setupProblem: false, inner: ex);
        }
    }

    private static async Task<string> TranscribeCoreAsync(string audioPath, AppSettings cfg, IProgress<(double Fraction, string Text)>? progress, CancellationToken ct)
    {
        var (exe, model) = Resolve(cfg);
        if (exe == null) throw new WhisperException("The whisper.cpp program was not found. Choose it in Settings > Lyrics.", setupProblem: true);

        // checked again right now: the model may have been deleted or replaced since the settings were saved
        var modelProblem = CheckModel(model);
        if (modelProblem != null)
            throw new WhisperException("Model missing or invalid (" + modelProblem + "). Choose a working ggml model file in Settings > Lyrics.", setupProblem: true);
        if (!File.Exists(audioPath)) throw new WhisperException("The song is no longer there.");

        var work = Path.Combine(Path.GetTempPath(), "Tempo-whisper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            // 1) 16 kHz mono 16-bit WAV
            progress?.Report((0, "Preparing the audio…"));
            var wav = Path.Combine(work, "input.wav");
            try
            {
                await Task.Run(() => ConvertTo16kMono(audioPath, wav), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new WhisperException("Tempo couldn't read this song's audio to transcribe it (" + ex.Message + ").", inner: ex);
            }
            ct.ThrowIfCancellationRequested();

            // 2) whisper.cpp
            var outBase = Path.Combine(work, "result");
            string lang = string.IsNullOrWhiteSpace(cfg.WhisperLanguage) ? "auto" : cfg.WhisperLanguage.Trim();
            // English-only models (for example ggml-base.en.bin) cannot detect a language
            if (Path.GetFileName(model).Contains(".en", StringComparison.OrdinalIgnoreCase)) lang = "en";

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? work
            };
            foreach (var a in new[]
            {
                "-m", model, "-f", wav, "-olrc", "-of", outBase, "-pp",
                // short lines (about one sung line each) give far better highlight timing; no carried-over context stops the
                // "gets stuck and skips the rest of the song" repetition loops whisper is prone to on music
                "-ml", "45", "-sow", "-mc", "0",
                "-l", lang, "-t", Math.Clamp(Environment.ProcessorCount - 2, 2, 8).ToString(CultureInfo.InvariantCulture)   // leave two cores for playback and the UI
            })
                psi.ArgumentList.Add(a);

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var tail = new Queue<string>();

            // what the user sees: the current stage, how long it has been running, and whisper's latest message
            string stage = "Starting Whisper…";
            double fraction = 0;
            string lastMessage = "";
            var clock = Stopwatch.StartNew();
            void Publish()
            {
                string text, hint;
                double f;
                lock (tail)
                {
                    text = stage;
                    f = fraction;
                    hint = lastMessage;
                }
                var elapsed = clock.Elapsed;
                string time = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
                if (f <= 0 && elapsed.TotalSeconds > 45) text += "  (big models take a while to load on a CPU)";
                if (f <= 0 && hint.Length > 0) text += "\n" + (hint.Length > 80 ? hint.Substring(0, 80) + "…" : hint);
                progress?.Report((f, text + "  ·  " + time));
            }

            void OnLine(string? line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                line = line.Trim();
                lock (tail)
                {
                    tail.Enqueue(line);
                    while (tail.Count > 6) tail.Dequeue();

                    var m = ProgressLine.Match(line);
                    if (m.Success && int.TryParse(m.Groups[1].Value, out int pct))
                    {
                        fraction = Math.Clamp(pct, 0, 100) / 100.0;
                        stage = "Transcribing…  " + pct + "%";
                    }
                    else
                    {
                        lastMessage = line;
                        if (line.Contains("loading model", StringComparison.OrdinalIgnoreCase) || line.Contains("whisper_init", StringComparison.OrdinalIgnoreCase))
                            stage = "Loading the model…";
                        else if (line.Contains("processing", StringComparison.OrdinalIgnoreCase))
                            stage = "Transcribing…";
                    }
                }
                Publish();
            }
            process.OutputDataReceived += (_, e) => OnLine(e.Data);
            process.ErrorDataReceived += (_, e) => OnLine(e.Data);

            AppLog.Write("Whisper", null, "running: " + exe + " " + string.Join(" ", psi.ArgumentList.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a)));
            progress?.Report((0, "Starting Whisper…"));
            try
            {
                if (!process.Start()) throw new WhisperException("The whisper.cpp program could not be started.", setupProblem: true);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // long jobs must not make the music or video stutter
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* already exited / not allowed */ }
            }
            catch (WhisperException) { throw; }
            catch (Exception ex)
            {
                // not a Windows program, blocked by antivirus, missing DLLs next to it ...
                throw new WhisperException("The whisper.cpp program could not be started (" + ex.Message + "). Check the program chosen in Settings > Lyrics.",
                    setupProblem: true, inner: ex);
            }

            // a once-a-second tick so the status never looks frozen while whisper.cpp is silent (model loading, long passes)
            using var heartbeat = new Timer(_ => Publish(), null, 1000, 1000);

            using (ct.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ } }))
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            process.WaitForExit();   // lets the redirected output finish, so the last lines are in the error message
            ct.ThrowIfCancellationRequested();

            var lrc = outBase + ".lrc";
            if (process.ExitCode != 0 || !File.Exists(lrc))
            {
                string detail;
                lock (tail) detail = string.Join(" ", tail);

                // the model went missing while it ran, or whisper.cpp refused to load it
                bool modelGone = CheckModel(model) != null;
                bool loadFailure = Regex.IsMatch(detail, "failed to (load|initialize)|invalid model|bad magic|not a ggml|unknown model|failed to read", RegexOptions.IgnoreCase);
                if (modelGone || loadFailure)
                    throw new WhisperException("Model missing or invalid. Choose a working ggml model file in Settings > Lyrics.", setupProblem: true);

                if (IsDeprecatedStub(exe))
                    throw new WhisperException("main.exe is an old stub that does nothing in recent whisper.cpp releases. Choose whisper-cli.exe instead.", setupProblem: true);

                throw new WhisperException("Whisper didn't produce lyrics" + (detail.Length > 0 ? ": " + detail : "."));
            }

            var text = await File.ReadAllTextAsync(lrc, Encoding.UTF8, ct).ConfigureAwait(false);
            if (!LyricsParser.Parse(text).Lines.Any(l => l.Text.Length > 0))
                throw new WhisperException("Whisper didn't hear any words in this song.");
            return text;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* temp folder; the OS cleans it eventually */ }
        }
    }

    internal static void ConvertTo16kMono(string source, string wavPath)
    {
        using var reader = new MediaFoundationReader(source);
        using var resampler = new MediaFoundationResampler(reader, new WaveFormat(16000, 16, 1)) { ResamplerQuality = 60 };
        WaveFileWriter.CreateWaveFile(wavPath, resampler);
    }
}
