using NAudio.Wave;

namespace EchoDeck;

/// <summary>
/// Builds the waveform for the music-mode seek bar. Instead of decoding the whole song it jumps to ~600 evenly spaced
/// positions and measures a short slice at each, so even long mixes finish in a few seconds. If a format can't be
/// read (Media Foundation has no decoder for it) the caller keeps the flat placeholder bars.
/// </summary>
internal sealed record AudioAnalysis(float[] Peaks, SpectrumData? Spectrum);

internal static class WaveformLoader
{
    public const int Buckets = 300;

    // Up to this length the song is read once from start to finish (decoding runs at hundreds of times real time, and
    // seeking inside VBR mp3 files is slower than just reading). Longer mixes use evenly spaced seeks instead.
    private static readonly TimeSpan SequentialLimit = TimeSpan.FromMinutes(25);

    public static Task<AudioAnalysis?> FromFileAsync(string path, CancellationToken ct, Action<float[]>? partial = null) =>
        Task.Run<AudioAnalysis?>(() =>
        {
            try
            {
                // worked out before: it opens at once
                var stored = WaveformCache.TryRead(path);
                if (stored != null)
                {
                    partial?.Invoke(stored.Peaks);
                    return stored;
                }

                using var reader = new MediaFoundationReader(path);
                var analysis = Analyze(reader, ct, partial);
                if (analysis != null) WaveformCache.Write(path, analysis);   // ...and next time too
                return analysis;
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }, ct);

    /// <summary>For Anonymous Mode: reads from its own decrypting stream, so nothing touches the disk.</summary>
    public static Task<AudioAnalysis?> FromStreamAsync(Stream stream, CancellationToken ct, Action<float[]>? partial = null) =>
        Task.Run<AudioAnalysis?>(() =>
        {
            try
            {
                using (stream)
                using (var reader = new StreamMediaFoundationReader(stream))
                    return Analyze(reader, ct, partial);
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }, ct);

    /// <param name="partial">Called now and then while the song is read, with the part of the waveform that is ready (the rest is flat),
    /// so the bar fills in as it loads instead of staying empty until the very end.</param>
    private static AudioAnalysis? Analyze(WaveStream reader, CancellationToken ct, Action<float[]>? partial = null)
    {
        // Reading a whole song takes a full CPU core for a moment. At normal priority that competed with the audio of the song that
        // is playing (crackling, most audible when the next song was starting): the analysis now only uses what is left over.
        var thread = Thread.CurrentThread;
        var before = thread.Priority;
        try
        {
            thread.Priority = ThreadPriority.Lowest;
            return AnalyzeCore(reader, ct, partial);
        }
        finally
        {
            try { thread.Priority = before; } catch { /* the pool thread is going away */ }
        }
    }

    private static AudioAnalysis? AnalyzeCore(WaveStream reader, CancellationToken ct, Action<float[]>? partial)
    {
        var total = reader.TotalTime;
        if (total.TotalSeconds < 1) return null;

        var samples = reader.ToSampleProvider();
        var peaks = new float[Buckets];
        var buffer = new float[16384];
        SpectrumBuilder? spectrum = null;

        if (total <= SequentialLimit)
        {
            // the same single read also feeds the spectrum (the music visualiser): mono mix of every sample frame
            int channels = Math.Max(1, reader.WaveFormat.Channels);
            spectrum = new SpectrumBuilder(reader.WaveFormat.SampleRate);
            float mix = 0;
            int mixCount = 0;

            long perBucket = Math.Max(1, (long)(total.TotalSeconds * reader.WaveFormat.SampleRate) * reader.WaveFormat.Channels / Buckets);
            long index = 0;
            long nextBoundary = perBucket;   // the first sample of the next bucket (no division per sample)
            int b = 0;
            int reported = 0;
            float runningMax = 0;
            int read;
            while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                for (int k = 0; k < read; k++)
                {
                    float v = Math.Abs(buffer[k]);
                    while (index >= nextBoundary && b < Buckets - 1) { b++; nextBoundary += perBucket; }
                    if (v > peaks[b]) peaks[b] = v;
                    index++;

                    mix += buffer[k];
                    if (++mixCount == channels)
                    {
                        spectrum.Push(mix / channels);
                        mix = 0;
                        mixCount = 0;
                    }
                }

                // show what is ready so far (about 15 times while loading)
                if (partial != null && b - reported >= Buckets / 15)
                {
                    reported = b;
                    runningMax = 0;
                    for (int i = 0; i < b; i++) if (peaks[i] > runningMax) runningMax = peaks[i];
                    if (runningMax > 0.0001f)
                    {
                        var shown = new float[Buckets];
                        for (int i = 0; i < b; i++) shown[i] = (float)Math.Pow(peaks[i] / runningMax, 0.8);
                        partial(shown);
                    }
                }
            }
        }
        else
        {
            int window = Math.Max(512, reader.WaveFormat.SampleRate * reader.WaveFormat.Channels / 30);
            for (int i = 0; i < Buckets; i++)
            {
                ct.ThrowIfCancellationRequested();
                reader.CurrentTime = TimeSpan.FromTicks(total.Ticks / Buckets * i);
                int read = samples.Read(buffer, 0, Math.Min(window, buffer.Length));
                float max = 0;
                for (int k = 0; k < read; k++)
                {
                    float v = Math.Abs(buffer[k]);
                    if (v > max) max = v;
                }
                peaks[i] = max;
            }
        }

        float overall = 0;
        foreach (var p in peaks) if (p > overall) overall = p;
        if (overall < 0.0001f) return null;

        // normalise, then ease the values a little so quiet parts stay visible
        for (int i = 0; i < peaks.Length; i++)
            peaks[i] = (float)Math.Pow(peaks[i] / overall, 0.8);
        return new AudioAnalysis(peaks, spectrum?.Build());
    }
}
