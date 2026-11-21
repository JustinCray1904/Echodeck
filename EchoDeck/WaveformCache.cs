using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace EchoDeck;

/// <summary>
/// Keeps the waveform (and the visualiser data) of every song that was analysed, so the next time it opens at once instead of
/// being worked out again. One small compressed file per song in %LOCALAPPDATA%\Tempo\Waveforms, named after a fingerprint of the
/// song's path, size and last-changed time: a song that is edited, replaced or moved simply gets a new entry. The folder is kept
/// under about 300-400 MB; the songs not played for the longest time are removed first. Only used for songs on the disk: a song in the
/// Anonymous Mode vault is read from its own decrypting stream and nothing about it is ever stored here.
/// </summary>
internal static class WaveformCache
{
    private const int Magic = 0x31465754;   // "TWF1"
    private const long MaxBytes = 400L * 1024 * 1024;
    private const long PruneToBytes = 300L * 1024 * 1024;

    /// <summary>The folder (settable for tests).</summary>
    internal static string Dir { get; set; } = Path.Combine(AppPaths.DataDir, "Waveforms");

    private static int _writes;

    /// <summary>The fingerprint a song is stored under (null when the file can't be read).</summary>
    internal static string? KeyFor(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            string text = path.ToLowerInvariant() + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)));
        }
        catch { return null; }
    }

    /// <summary>The stored analysis of this song, or null (never stored, changed since, or damaged).</summary>
    internal static AudioAnalysis? TryRead(string path)
    {
        string? key = KeyFor(path);
        if (key == null) return null;
        string file = Path.Combine(Dir, key + ".twf");

        try
        {
            if (!File.Exists(file)) return null;

            AudioAnalysis? result;
            using (var raw = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (var brotli = new BrotliStream(raw, CompressionMode.Decompress))
            using (var reader = new BinaryReader(brotli))
            {
                if (reader.ReadInt32() != Magic) return null;

                int peakCount = reader.ReadInt32();
                if (peakCount <= 0 || peakCount > 100_000) return null;
                var peaks = new float[peakCount];
                for (int i = 0; i < peakCount; i++) peaks[i] = reader.ReadSingle();

                SpectrumData? spectrum = null;
                if (reader.ReadBoolean())
                {
                    int frames = reader.ReadInt32();
                    int length = reader.ReadInt32();
                    if (frames < 0 || length != frames * SpectrumData.Bands || length > 200_000_000) return null;
                    var values = reader.ReadBytes(length);
                    if (values.Length != length) return null;
                    spectrum = new SpectrumData(values, frames);
                }
                result = new AudioAnalysis(peaks, spectrum);
            }

            try { File.SetLastWriteTimeUtc(file, DateTime.UtcNow); } catch { /* the order of use is only a hint for clean-up */ }
            return result;
        }
        catch
        {
            try { File.Delete(file); } catch { /* damaged entry: gone, it will be made again */ }
            return null;
        }
    }

    /// <summary>Stores the analysis (best effort: a failure only means it is worked out again next time).</summary>
    internal static void Write(string path, AudioAnalysis analysis)
    {
        string? key = KeyFor(path);
        if (key == null || analysis.Peaks.Length == 0) return;

        try
        {
            Directory.CreateDirectory(Dir);
            string file = Path.Combine(Dir, key + ".twf");
            string temp = file + ".tmp";

            using (var raw = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var brotli = new BrotliStream(raw, CompressionLevel.Fastest))
            using (var writer = new BinaryWriter(brotli))
            {
                writer.Write(Magic);
                writer.Write(analysis.Peaks.Length);
                foreach (var peak in analysis.Peaks) writer.Write(peak);

                writer.Write(analysis.Spectrum != null);
                if (analysis.Spectrum != null)
                {
                    writer.Write(analysis.Spectrum.Frames);
                    writer.Write(analysis.Spectrum.Values.Length);
                    writer.Write(analysis.Spectrum.Values);
                }
            }
            File.Move(temp, file, overwrite: true);

            if (Interlocked.Increment(ref _writes) % 25 == 0) Prune();
        }
        catch { /* best effort */ }
    }

    /// <summary>Keeps the folder small: the entries used least recently go first.</summary>
    internal static void Prune(long maxBytes = MaxBytes, long pruneTo = PruneToBytes)
    {
        try
        {
            var files = new DirectoryInfo(Dir).GetFiles("*.twf");
            long total = files.Sum(f => f.Length);
            if (total <= maxBytes) return;

            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= pruneTo) break;
                try { total -= file.Length; file.Delete(); } catch { /* in use */ }
            }
        }
        catch { /* best effort */ }
    }
}
