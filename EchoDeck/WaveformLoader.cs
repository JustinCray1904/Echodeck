using NAudio.Wave;

namespace EchoDeck;

/// <summary>
/// Builds the waveform for the music-mode seek bar. Instead of decoding the whole song it jumps to ~600 evenly spaced
/// positions and measures a short slice at each, so even long mixes finish in a few seconds. If a format can't be
/// read (Media Foundation has no decoder for it) the caller keeps the flat placeholder bars.
/// </summary>
internal static class WaveformLoader
{
    public const int Buckets = 300;

    // Up to this length the song is read once from start to finish (decoding runs at hundreds of times real time, and
    // seeking inside VBR mp3 files is slower than just reading). Longer mixes use evenly spaced seeks instead.
    private static readonly TimeSpan SequentialLimit = TimeSpan.FromMinutes(25);

    public static Task<float[]?> FromFileAsync(string path, CancellationToken ct) =>
        Task.Run<float[]?>(() =>
        {
            try
            {
                using var reader = new MediaFoundationReader(path);
                return Analyze(reader, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }, ct);

    /// <summary>For Anonymous Mode: reads from its own decrypting stream, so nothing touches the disk.</summary>
    public static Task<float[]?> FromStreamAsync(Stream stream, CancellationToken ct) =>
        Task.Run<float[]?>(() =>
        {
            try
            {
                using (stream)
                using (var reader = new StreamMediaFoundationReader(stream))
                    return Analyze(reader, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }, ct);

    private static float[]? Analyze(WaveStream reader, CancellationToken ct)
    {
        var total = reader.TotalTime;
        if (total.TotalSeconds < 1) return null;

        var samples = reader.ToSampleProvider();
        var peaks = new float[Buckets];
        var buffer = new float[16384];

        if (total <= SequentialLimit)
        {
            long perBucket = Math.Max(1, (long)(total.TotalSeconds * reader.WaveFormat.SampleRate) * reader.WaveFormat.Channels / Buckets);
            long index = 0;
            int read;
            while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                for (int k = 0; k < read; k++)
                {
                    float v = Math.Abs(buffer[k]);
                    int b = (int)Math.Min(Buckets - 1, index / perBucket);
                    if (v > peaks[b]) peaks[b] = v;
                    index++;
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
        return peaks;
    }
}
