using System.Security.Cryptography;
using System.Text;
using NAudio.Wave;

namespace EchoDeck;

/// <summary>
/// Makes the modded copy of a song: reads it, runs <see cref="AudioModDsp"/>, and writes a 16-bit WAV file into
/// %LOCALAPPDATA%\Tempo\AudioMod, which Tempo then plays instead of the original (the original is never changed). A copy that was made
/// before comes straight from that folder. Normal files only: Anonymous Mode never gets here, because the copy would be a decrypted file.
/// </summary>
internal static class AudioModRenderer
{
    /// <summary>Longer songs are not modded (the whole song is held in memory while it is processed).</summary>
    public const double MaxMinutes = 10;

    private const int KeepFiles = 8;

    public static string CacheDir => Path.Combine(AppPaths.DataDir, "AudioMod");

    /// <summary>Where the modded copy of this song with these settings is (or will be), or null when the song can't be found.</summary>
    public static string? FileFor(string source, AudioModSettings settings)
    {
        try
        {
            var info = new FileInfo(source);
            if (!info.Exists) return null;
            string id = Path.GetFullPath(source).ToLowerInvariant() + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks + "|" + settings.Key();
            var hash = SHA1.HashData(Encoding.UTF8.GetBytes(id));
            return Path.Combine(CacheDir, Convert.ToHexString(hash, 0, 10).ToLowerInvariant() + ".wav");
        }
        catch { return null; }
    }

    /// <summary>Makes (or finds) the modded copy. Throws <see cref="InvalidOperationException"/> with a sentence for the user when it can't be made.</summary>
    public static Task<string> RenderAsync(string source, AudioModSettings settings, IProgress<double>? progress, CancellationToken ct) =>
        Task.Run(() => Render(source, settings, progress, ct), ct);

    private static string Render(string source, AudioModSettings settings, IProgress<double>? progress, CancellationToken ct)
    {
        string target = FileFor(source, settings) ?? throw new InvalidOperationException("The song can't be found.");
        if (File.Exists(target) && new FileInfo(target).Length > 44)
        {
            progress?.Report(1);
            return target;   // made before
        }

        Directory.CreateDirectory(CacheDir);

        // 1) read the song into two channels of samples
        float[] left, right;
        int rate;
        try
        {
            using var reader = new MediaFoundationReader(source);
            if (reader.TotalTime.TotalMinutes > MaxMinutes)
                throw new InvalidOperationException($"Audio Mod works on songs up to {MaxMinutes:0} minutes. This one is longer.");

            var samples = reader.ToSampleProvider();
            int channels = Math.Max(1, samples.WaveFormat.Channels);
            rate = samples.WaveFormat.SampleRate;
            int estimate = (int)Math.Min(int.MaxValue / 4, reader.TotalTime.TotalSeconds * rate + rate);
            left = new float[Math.Max(estimate, 1024)];
            right = new float[left.Length];

            var buffer = new float[channels * 8192];
            int frames = 0, read;
            while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                int got = read / channels;
                if (frames + got > left.Length)
                {
                    int bigger = Math.Max(frames + got, left.Length + left.Length / 2);
                    Array.Resize(ref left, bigger);
                    Array.Resize(ref right, bigger);
                }
                for (int i = 0; i < got; i++)
                {
                    float a = buffer[i * channels];
                    left[frames + i] = a;
                    right[frames + i] = channels > 1 ? buffer[i * channels + 1] : a;
                }
                frames += got;
                if (frames % 262144 < got) progress?.Report(Math.Min(0.1, 0.1 * frames / Math.Max(1.0, reader.TotalTime.TotalSeconds * rate)));
            }
            if (frames < rate / 4) throw new InvalidOperationException("The song is too short.");
            Array.Resize(ref left, frames);
            Array.Resize(ref right, frames);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException) { throw; }
        catch (Exception)
        {
            throw new InvalidOperationException("Tempo can't read this song's audio, so it can't be modded.");
        }
        progress?.Report(0.1);

        // 2) the effects
        var (l, r) = AudioModDsp.Process(left, right, rate, settings, p => progress?.Report(0.1 + 0.8 * p), ct);
        left = right = Array.Empty<float>();

        // 3) the file (written under another name first, so a cut-off one is never played)
        PurgeOld(target);
        string part = target + ".part";
        try
        {
            using (var stream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                WriteWav(stream, l, r, rate, ct);
            File.Move(part, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(part); } catch { /* nothing to clean up */ }
            throw;
        }
        progress?.Report(1);
        return target;
    }

    /// <summary>A 16-bit stereo PCM WAV file: the 44-byte header, then the samples (clipped to -1..1).</summary>
    internal static void WriteWav(Stream output, float[] left, float[] right, int sampleRate, CancellationToken ct = default)
    {
        int frames = left.Length;
        long dataBytes = (long)frames * 4;
        using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write((uint)(36 + dataBytes));
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16u);
        writer.Write((ushort)1);            // PCM
        writer.Write((ushort)2);            // channels
        writer.Write((uint)sampleRate);
        writer.Write((uint)(sampleRate * 4));
        writer.Write((ushort)4);            // bytes per frame
        writer.Write((ushort)16);           // bits per sample
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write((uint)dataBytes);

        var chunk = new byte[1 << 16];
        int used = 0;
        for (int i = 0; i < frames; i++)
        {
            short a = ToShort(left[i]), b = ToShort(right[i]);
            chunk[used++] = (byte)(a & 0xFF);
            chunk[used++] = (byte)((a >> 8) & 0xFF);
            chunk[used++] = (byte)(b & 0xFF);
            chunk[used++] = (byte)((b >> 8) & 0xFF);
            if (used == chunk.Length)
            {
                writer.Write(chunk, 0, used);
                used = 0;
                ct.ThrowIfCancellationRequested();
            }
        }
        if (used > 0) writer.Write(chunk, 0, used);
    }

    private static short ToShort(float v) => (short)Math.Clamp((int)Math.Round(v * 32767f), -32768, 32767);

    /// <summary>Keeps the folder small: old copies go, the newest few stay (never the one about to be written).</summary>
    private static void PurgeOld(string keep)
    {
        try
        {
            var files = new DirectoryInfo(CacheDir).GetFiles("*.wav")
                .Where(f => !string.Equals(f.FullName, keep, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            foreach (var old in files.Skip(KeepFiles - 1))
            {
                try { old.Delete(); } catch { /* playing right now: next time */ }
            }
            foreach (var leftover in new DirectoryInfo(CacheDir).GetFiles("*.part"))
            {
                try { leftover.Delete(); } catch { /* in use */ }
            }
        }
        catch { /* best effort */ }
    }
}
