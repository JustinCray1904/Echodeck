using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NAudio.Wave;

namespace EchoDeck;

internal static partial class WhisperService
{
    // ------------------------------------------------------------------ pieces

    private static int ReadFully(Stream stream, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = stream.Read(buffer, total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>
    /// Cuts the 16 kHz mono WAV into pieces of 18 to 27 s, each cut at the quietest tenth of a second around the 23 s mark,
    /// so a word is hardly ever cut in two. Each piece is written as its own WAV in <paramref name="work"/>.
    /// </summary>
    private static List<Chunk> SplitAtQuietMoments(string wavPath, string work)
    {
        var chunks = new List<Chunk>();
        using var reader = new WaveFileReader(wavPath);
        int rate = reader.WaveFormat.SampleRate;
        int bytesPerSample = Math.Max(1, reader.WaveFormat.BitsPerSample / 8) * Math.Max(1, reader.WaveFormat.Channels);
        long totalSamples = reader.Length / bytesPerSample;
        double total = (double)totalSamples / rate;
        if (total < 0.5) return chunks;

        // loudness of every tenth of a second (16-bit mono)
        int frameSamples = Math.Max(1, rate / 10);
        var energy = new List<float>((int)(total * 10) + 2);
        var buffer = new byte[frameSamples * bytesPerSample];
        reader.Position = 0;
        while (true)
        {
            int read = ReadFully(reader, buffer, buffer.Length);
            if (read < bytesPerSample) break;
            int n = read / bytesPerSample;
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                short sample = BitConverter.ToInt16(buffer, i * bytesPerSample);
                sum += (double)sample * sample;
            }
            energy.Add((float)(Math.Sqrt(sum / n) / 32768.0));
            if (read < buffer.Length) break;
        }

        float Smooth(int frame)
        {
            int a = Math.Max(0, frame - 1), b = Math.Min(energy.Count - 1, frame + 1);
            float sum = 0;
            for (int i = a; i <= b; i++) sum += energy[i];
            return sum / (b - a + 1);
        }

        // where to cut
        var cuts = new List<double> { 0 };
        double pos = 0;
        while (total - pos > 30)
        {
            double want = pos + 23;
            int from = Math.Max(0, (int)((want - 5) * 10)), to = Math.Min(energy.Count - 1, (int)((want + 4) * 10));
            int best = (int)(want * 10);
            float bestEnergy = float.MaxValue;
            for (int f = from; f <= to; f++)
            {
                float e = Smooth(f);
                if (e <= bestEnergy) { bestEnergy = e; best = f; }   // on a tie the later one wins: longer pieces, fewer runs
            }
            double cut = best / 10.0;
            if (cut < pos + 12) cut = want;
            cuts.Add(cut);
            pos = cut;
        }
        cuts.Add(total);

        // write the pieces
        for (int i = 0; i + 1 < cuts.Count; i++)
        {
            double start = cuts[i], end = cuts[i + 1];
            long first = (long)(start * rate), last = Math.Min(totalSamples, (long)(end * rate));
            if (last <= first) continue;

            var path = Path.Combine(work, "part" + i + ".wav");
            reader.Position = first * bytesPerSample;
            long bytesLeft = (last - first) * bytesPerSample;
            var copy = new byte[65536];
            using (var writer = new WaveFileWriter(path, reader.WaveFormat))
            {
                while (bytesLeft > 0)
                {
                    int want = (int)Math.Min(copy.Length, bytesLeft);
                    int read = ReadFully(reader, copy, want);
                    if (read <= 0) break;
                    writer.Write(copy, 0, read);
                    bytesLeft -= read;
                }
            }

            // average loudness of the piece, from the tenth-of-a-second values
            int f0 = Math.Max(0, (int)(start * 10)), f1 = Math.Min(energy.Count - 1, (int)(end * 10));
            double sumSq = 0;
            int count = 0;
            for (int f = f0; f <= f1; f++) { sumSq += (double)energy[f] * energy[f]; count++; }
            double rms = count > 0 ? Math.Sqrt(sumSq / count) : 0;

            chunks.Add(new Chunk(chunks.Count, start, end - start, path, Path.Combine(work, "part" + i), rms));
        }
        return chunks;
    }

    /// <summary>A copy of a 16-bit mono WAV brought up to <paramref name="targetRms"/> (at most 8 times louder): vocals under a loud beat.</summary>
    private static void NormaliseCopy(string source, string target, double targetRms)
    {
        byte[] bytes;
        WaveFormat format;
        using (var reader = new WaveFileReader(source))
        {
            format = reader.WaveFormat;
            bytes = new byte[reader.Length];
            ReadFully(reader, bytes, bytes.Length);
        }

        int count = bytes.Length / 2;
        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            short s = BitConverter.ToInt16(bytes, i * 2);
            sum += (double)s * s;
        }
        double rms = count > 0 ? Math.Sqrt(sum / count) / 32768.0 : 0;
        double gain = rms > 1e-4 ? Math.Clamp(targetRms / rms, 1.0, 8.0) : 1.0;

        using var writer = new WaveFileWriter(target, format);
        var outBytes = new byte[bytes.Length];
        for (int i = 0; i < count; i++)
        {
            double v = BitConverter.ToInt16(bytes, i * 2) * gain;
            short clipped = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
            outBytes[i * 2] = (byte)(clipped & 0xFF);
            outBytes[i * 2 + 1] = (byte)((clipped >> 8) & 0xFF);
        }
        writer.Write(outBytes, 0, outBytes.Length);
    }

    private static readonly Regex JunkText = new(
        @"^\s*(?:[\[\(\{<*♪♫#\-–—\s].*[\]\)\}>*♪♫#\-–—\s]|[♪♫\s.,!?…-]*|(?:thanks? for watching\.?|thank you\.?|subtitles? by .*|you)\s*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// With "never skip" whisper sometimes fills instrumental parts with markers like [Music], (music), ♪ or a stray
    /// "Thank you.". Those lines are dropped (their timing carries no lyrics); real sung lines are untouched.
    /// </summary>
    internal static string RemoveJunkLines(string lrc)
    {
        var kept = new List<string>();
        foreach (var raw in lrc.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            int end = 0;
            while (end < line.Length && line[end] == '[')
            {
                int close = line.IndexOf(']', end);
                if (close < 0) break;
                end = close + 1;
            }
            var body = line.Substring(end).Trim();
            if (end > 0 && JunkText.IsMatch(body)) continue;
            kept.Add(line);
        }
        return string.Join("\n", kept) + "\n";
    }

    internal static void ConvertTo16kMono(string source, string wavPath)
    {
        using var reader = new MediaFoundationReader(source);
        using var resampler = new MediaFoundationResampler(reader, new WaveFormat(16000, 16, 1)) { ResamplerQuality = 60 };
        WaveFileWriter.CreateWaveFile(wavPath, resampler);
    }
}
