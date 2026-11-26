using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace EchoDeck;

/// <summary>
/// Keeps each song's waveform (and the spectrum the visualiser draws from) in %LOCALAPPDATA%\Tempo\Waveforms, so a song is read
/// and analysed only once: the next time it plays, the bars and the visualiser come straight from this small file.
///
/// A file is found by the song's path, size and last-modified time, so a song that is edited, replaced or re-tagged on disk is
/// analysed afresh. Nothing is cached in Anonymous Mode (it reads through its own decrypting stream and never comes here).
/// Files are written to a temporary name and renamed, and anything unreadable is simply ignored and rebuilt, so the cache can
/// never break playback. Format: "TWF1" | peak count | peaks (float32) | spectrum frames | spectrum length | spectrum bytes,
/// all deflate-compressed.
/// </summary>
internal static class WaveformCache
{
    private const uint Magic = 0x31465754;   // "TWF1" (little endian)
    private const int MaxFiles = 3000;       // the oldest are dropped beyond this (about 100-300 KB each at most)
    private const int MaxSpectrumBytes = 400_000_000;

    private static bool _pruned;

    private static string Dir => Path.Combine(AppPaths.DataDir, "Waveforms");

    /// <summary>The cache file of a song as it is on disk right now, or null if it can't be told (missing file, odd path).</summary>
    public static string? FileFor(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            string id = Path.GetFullPath(path).ToLowerInvariant() + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            var hash = SHA1.HashData(Encoding.UTF8.GetBytes(id));
            return Path.Combine(Dir, Convert.ToHexString(hash, 0, 10).ToLowerInvariant() + ".wf");
        }
        catch { return null; }
    }

    /// <summary>The saved analysis, or null when there is none (or it can't be read, in which case it is removed).</summary>
    public static AudioAnalysis? TryLoad(string? file)
    {
        if (file == null) return null;
        try
        {
            if (!File.Exists(file)) return null;

            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            using var deflate = new DeflateStream(fs, CompressionMode.Decompress);
            using var reader = new BinaryReader(deflate);

            if (reader.ReadUInt32() != Magic) return Discard(file);

            int peakCount = reader.ReadInt32();
            if (peakCount != WaveformLoader.Buckets) return Discard(file);   // made with other settings: analyse again
            var peakBytes = reader.ReadBytes(peakCount * sizeof(float));
            if (peakBytes.Length != peakCount * sizeof(float)) return Discard(file);
            var peaks = new float[peakCount];
            Buffer.BlockCopy(peakBytes, 0, peaks, 0, peakBytes.Length);

            SpectrumData? spectrum = null;
            int frames = reader.ReadInt32();
            if (frames > 0)
            {
                int length = reader.ReadInt32();
                if (length <= 0 || length > MaxSpectrumBytes || length % SpectrumData.Bands != 0) return Discard(file);
                var values = reader.ReadBytes(length);
                if (values.Length != length) return Discard(file);
                spectrum = new SpectrumData(values, frames);
            }

            try { File.SetLastAccessTimeUtc(file, DateTime.UtcNow); } catch { /* not important */ }
            return new AudioAnalysis(peaks, spectrum);
        }
        catch
        {
            return Discard(file);   // damaged or cut short
        }
    }

    /// <summary>Saves an analysis under the file name worked out before the song was read (best effort).</summary>
    public static void Save(string? file, AudioAnalysis analysis)
    {
        if (file == null || analysis.Peaks.Length != WaveformLoader.Buckets) return;
        try
        {
            Directory.CreateDirectory(Dir);
            string temp = file + ".tmp";
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var deflate = new DeflateStream(fs, CompressionLevel.Fastest))
            using (var writer = new BinaryWriter(deflate))
            {
                writer.Write(Magic);
                writer.Write(analysis.Peaks.Length);
                var peakBytes = new byte[analysis.Peaks.Length * sizeof(float)];
                Buffer.BlockCopy(analysis.Peaks, 0, peakBytes, 0, peakBytes.Length);
                writer.Write(peakBytes);

                if (analysis.Spectrum is { } spectrum)
                {
                    writer.Write(spectrum.Frames);
                    writer.Write(spectrum.Values.Length);
                    writer.Write(spectrum.Values);
                }
                else
                {
                    writer.Write(0);
                }
            }
            File.Move(temp, file, overwrite: true);
            Prune();
        }
        catch (Exception ex)
        {
            // no cache this time: the song is analysed again next time
            AppLog.Event(AppLogLevel.Info, "waveform-cache.save-failed", ("exception", ex.GetType().Name));
        }
    }

    private static AudioAnalysis? Discard(string file)
    {
        try { File.Delete(file); } catch { /* in use: next time */ }
        return null;
    }

    /// <summary>Once per run: when there are too many files, the ones not used for the longest time go.</summary>
    private static void Prune()
    {
        if (_pruned) return;
        _pruned = true;
        try
        {
            var files = new DirectoryInfo(Dir).GetFiles("*.wf");
            if (files.Length <= MaxFiles) return;
            foreach (var old in files.OrderBy(f => f.LastAccessTimeUtc).Take(files.Length - MaxFiles * 4 / 5))
            {
                try { old.Delete(); } catch { /* skip */ }
            }
        }
        catch { /* best effort */ }
    }
}
