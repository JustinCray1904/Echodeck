using System.Text.Json;

namespace EchoDeck;

/// <summary>Where a video was left off. Saved in %LOCALAPPDATA%\Tempo\resume.json (never in Anonymous Mode).</summary>
internal sealed class ResumeEntry
{
    public string P { get; set; } = "";   // full path
    public long Pos { get; set; }         // position in ms
    public long Len { get; set; }         // length in ms
    public long Size { get; set; }        // file size, so a replaced file doesn't resume at a stale spot
    public long At { get; set; }          // when it was saved (UTC ticks), oldest are dropped first
}

internal static class ResumeStore
{
    public const long MinPositionMs = 15_000;     // less than this in: nothing worth continuing
    public const long MinLengthMs = 90_000;       // clips shorter than this never ask
    private const int Max = 200;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, ResumeEntry> Items = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    private static string FilePath => Path.Combine(AppPaths.DataDir, "resume.json");

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            var list = JsonSerializer.Deserialize<List<ResumeEntry>>(File.ReadAllText(FilePath));
            if (list != null)
                foreach (var e in list)
                    if (!string.IsNullOrEmpty(e.P)) Items[e.P] = e;
        }
        catch { Items.Clear(); }
    }

    /// <summary>The saved spot for this file, or null when there is none (or the file changed since).</summary>
    public static ResumeEntry? Get(string path)
    {
        lock (Gate)
        {
            EnsureLoaded();
            if (!Items.TryGetValue(path, out var e)) return null;
            try
            {
                if (new FileInfo(path).Length != e.Size) { Items.Remove(path); return null; }
            }
            catch { return null; }
            return e;
        }
    }

    /// <summary>Remembers the position. Too early, too short or nearly finished means "forget it" instead.</summary>
    public static void Commit(string path, long positionMs, long lengthMs)
    {
        lock (Gate)
        {
            EnsureLoaded();

            if (lengthMs < MinLengthMs) return;
            long tail = Math.Max(15_000, lengthMs / 20);          // the last 5 %: it counts as watched
            if (positionMs >= lengthMs - tail)
            {
                if (Items.Remove(path)) Save();
                return;
            }
            if (positionMs < MinPositionMs) return;               // leave any earlier spot alone

            long size;
            try { size = new FileInfo(path).Length; } catch { return; }

            Items[path] = new ResumeEntry { P = path, Pos = positionMs, Len = lengthMs, Size = size, At = DateTime.UtcNow.Ticks };
            if (Items.Count > Max)
                foreach (var old in Items.Values.OrderBy(v => v.At).Take(Items.Count - Max).ToList())
                    Items.Remove(old.P);
            Save();
        }
    }

    public static void Remove(string path)
    {
        lock (Gate)
        {
            EnsureLoaded();
            if (Items.Remove(path)) Save();
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Items.Values.ToList()));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* remembering a position is a nicety */ }
    }
}
