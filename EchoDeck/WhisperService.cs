using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NAudio.Wave;

namespace EchoDeck;

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
        return exe != null && model != null;
    }

    /// <summary>Transcribes a song and returns the result as .lrc text. Throws with a readable message on failure.</summary>
    public static async Task<string> TranscribeAsync(string audioPath, AppSettings cfg, IProgress<(double Fraction, string Text)>? progress, CancellationToken ct)
    {
        var (exe, model) = Resolve(cfg);
        if (exe == null) throw new InvalidOperationException("The whisper.cpp program was not found. Choose it in Settings > Lyrics.");
        if (model == null) throw new InvalidOperationException("No Whisper model was found. Choose a ggml model file in Settings > Lyrics.");
        if (!File.Exists(audioPath)) throw new FileNotFoundException("The song is no longer there.");

        var work = Path.Combine(Path.GetTempPath(), "Tempo-whisper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            // 1) 16 kHz mono 16-bit WAV
            progress?.Report((0, "Preparing the audio…"));
            var wav = Path.Combine(work, "input.wav");
            await Task.Run(() => ConvertTo16kMono(audioPath, wav), ct).ConfigureAwait(false);
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
                "-l", lang, "-t", Math.Clamp(Environment.ProcessorCount / 2, 2, 8).ToString(CultureInfo.InvariantCulture)
            })
                psi.ArgumentList.Add(a);

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var tail = new Queue<string>();
            void OnLine(string? line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                lock (tail) { tail.Enqueue(line.Trim()); while (tail.Count > 6) tail.Dequeue(); }
                var m = ProgressLine.Match(line);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int pct))
                    progress?.Report((Math.Clamp(pct, 0, 100) / 100.0, "Transcribing…  " + pct + "%"));
            }
            process.OutputDataReceived += (_, e) => OnLine(e.Data);
            process.ErrorDataReceived += (_, e) => OnLine(e.Data);

            progress?.Report((0, "Starting Whisper…"));
            if (!process.Start()) throw new InvalidOperationException("The whisper.cpp program could not be started.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using (ct.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ } }))
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            var lrc = outBase + ".lrc";
            if (process.ExitCode != 0 || !File.Exists(lrc))
            {
                string detail;
                lock (tail) detail = string.Join(" ", tail);
                throw new InvalidOperationException("Whisper didn't produce lyrics" + (detail.Length > 0 ? ": " + detail : "."));
            }

            var text = await File.ReadAllTextAsync(lrc, Encoding.UTF8, ct).ConfigureAwait(false);
            if (!LyricsParser.Parse(text).Lines.Any(l => l.Text.Length > 0))
                throw new InvalidOperationException("Whisper didn't hear any words in this song.");
            return text;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* temp folder; the OS cleans it eventually */ }
        }
    }

    private static void ConvertTo16kMono(string source, string wavPath)
    {
        using var reader = new MediaFoundationReader(source);
        using var resampler = new MediaFoundationResampler(reader, new WaveFormat(16000, 16, 1)) { ResamplerQuality = 60 };
        WaveFileWriter.CreateWaveFile(wavPath, resampler);
    }
}
