using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EchoDeck;

/// <summary>One line of lyrics. <see cref="TimeMs"/> is -1 for lines that carry no timestamp.</summary>
public sealed class LyricLine : INotifyPropertyChanged
{
    private bool _isActive;

    public LyricLine(long timeMs, string text)
    {
        TimeMs = timeMs;
        Text = text;
    }

    public long TimeMs { get; }
    public string Text { get; }

    /// <summary>True for lyrics without timestamps: every line is shown at full strength.</summary>
    public bool IsPlain { get; init; }

    /// <summary>True for the line that is being sung right now.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Parsed lyrics: either timed (.lrc) or plain text.</summary>
public sealed class LyricsDocument
{
    public List<LyricLine> Lines { get; } = new();
    public bool Synced { get; init; }
    public string RawText { get; init; } = "";
    public string? SourcePath { get; init; }
    public bool IsEmpty => Lines.Count == 0;
}

internal static class LyricsParser
{
    // [mm:ss], [mm:ss.x], [mm:ss.xx], [mm:ss.xxx] (also [mm:ss:xx])
    private static readonly Regex TimeTag = new(@"\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
    private static readonly Regex MetaTag = new(@"^\[(?<k>[A-Za-z#]+):(?<v>.*)\]\s*$", RegexOptions.Compiled);
    private static readonly Regex WordTag = new(@"<\d{1,3}:\d{2}(?:[.:]\d{1,3})?>", RegexOptions.Compiled);

    public static bool HasTimestamps(string text) => TimeTag.IsMatch(text ?? "");

    public static LyricsDocument Parse(string text, string? sourcePath = null)
    {
        text ??= "";
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        long offsetMs = 0;
        var timed = new List<LyricLine>();
        var plain = new List<LyricLine>();

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var matches = TimeTag.Matches(line);

            if (matches.Count == 0)
            {
                var meta = MetaTag.Match(line.Trim());
                if (meta.Success)
                {
                    if (meta.Groups["k"].Value.Equals("offset", StringComparison.OrdinalIgnoreCase) &&
                        long.TryParse(meta.Groups["v"].Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var off))
                        offsetMs = off;
                    continue;   // [ar:], [ti:], [al:] ... are not lyrics
                }
                plain.Add(new LyricLine(-1, WordTag.Replace(line, "").Trim()) { IsPlain = true });
                continue;
            }

            // everything after the last leading tag is the text
            int end = 0;
            foreach (Match m in matches)
            {
                if (m.Index > end + 0 && line.Substring(end, m.Index - end).Trim().Length > 0) break;
                end = m.Index + m.Length;
            }
            var body = WordTag.Replace(line.Substring(end), "").Trim();

            foreach (Match m in matches)
            {
                if (m.Index >= end) break;
                long ms = (long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60
                           + long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)) * 1000;
                var frac = m.Groups[3].Value;
                if (frac.Length > 0)
                {
                    long f = long.Parse(frac, CultureInfo.InvariantCulture);
                    ms += frac.Length switch { 1 => f * 100, 2 => f * 10, _ => f };
                }
                timed.Add(new LyricLine(Math.Max(0, ms - offsetMs), body));
            }
        }

        if (timed.Count > 0)
        {
            var doc = new LyricsDocument { Synced = true, RawText = text, SourcePath = sourcePath };
            // stable sort by time
            foreach (var l in timed.Select((l, i) => (l, i)).OrderBy(x => x.l.TimeMs).ThenBy(x => x.i).Select(x => x.l))
                doc.Lines.Add(l);
            return doc;
        }

        var plainDoc = new LyricsDocument { Synced = false, RawText = text, SourcePath = sourcePath };
        // trim blank lines from the ends, keep the ones in between as spacing
        int first = plain.FindIndex(l => l.Text.Length > 0);
        int last = plain.FindLastIndex(l => l.Text.Length > 0);
        if (first >= 0)
            for (int i = first; i <= last; i++) plainDoc.Lines.Add(plain[i]);
        return plainDoc;
    }

    public static string FormatTime(long ms)
    {
        if (ms < 0) ms = 0;
        long totalCs = ms / 10;
        long cs = totalCs % 100;
        long sec = (totalCs / 100) % 60;
        long min = totalCs / 100 / 60;
        return $"[{min:00}:{sec:00}.{cs:00}]";
    }

    /// <summary>Moves every timestamp in the text by <paramref name="deltaMs"/> (never below zero).</summary>
    public static string Shift(string text, long deltaMs) =>
        TimeTag.Replace(text ?? "", m =>
        {
            long ms = (long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60
                       + long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)) * 1000;
            var frac = m.Groups[3].Value;
            if (frac.Length > 0)
            {
                long f = long.Parse(frac, CultureInfo.InvariantCulture);
                ms += frac.Length switch { 1 => f * 100, 2 => f * 10, _ => f };
            }
            return FormatTime(ms + deltaMs);
        });

    /// <summary>Removes all timestamps, leaving plain lyrics.</summary>
    public static string StripTimes(string text) =>
        string.Join("\n", (text ?? "").Replace("\r\n", "\n").Split('\n').Select(l => TimeTag.Replace(l, "").TrimStart()));

    /// <summary>Puts a timestamp at the start of a line, replacing any it already has.</summary>
    public static string StampLine(string line, long ms)
    {
        var clean = TimeTag.Replace(line ?? "", "").TrimStart();
        return FormatTime(ms) + clean;
    }
}

internal static class LyricsFiles
{
    /// <summary>
    /// %LOCALAPPDATA%\Tempo\Lyrics: lyrics Tempo finds, generates or saves are kept here (hidden), so the music folders stay clean.
    /// Each file is named after the song plus a short hash of the song's full path, so two songs with the same name in different
    /// folders never share lyrics.
    /// </summary>
    public static string StoreDir => Path.Combine(AppPaths.DataDir, "Lyrics");

    private static string StoreBaseName(string mediaPath)
    {
        string stem = Path.GetFileNameWithoutExtension(mediaPath) ?? "";
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(stem.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray()).Trim(' ', '.');
        if (clean.Length > 80) clean = clean.Substring(0, 80).TrimEnd(' ', '.');
        if (clean.Length == 0) clean = "song";

        string full;
        try { full = Path.GetFullPath(mediaPath); } catch { full = mediaPath; }
        var hash = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant()));
        return clean + "." + Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    public static bool IsInStore(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            return dir != null && string.Equals(dir.TrimEnd('\\', '/'), Path.GetFullPath(StoreDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Makes sure the hidden lyrics folder exists.</summary>
    private static void EnsureStore()
    {
        var dir = StoreDir;
        if (Directory.Exists(dir)) return;
        var info = Directory.CreateDirectory(dir);
        try { info.Attributes |= FileAttributes.Hidden; } catch { /* a hidden flag is only cosmetic */ }
    }

    /// <summary>
    /// The lyrics of this song: the hidden Tempo folder first (.lrc preferred over .txt), then, for lyrics made by earlier
    /// versions or by hand, a .lrc / .txt with exactly the same name next to the song. Null when there are none.
    /// </summary>
    public static string? Find(string mediaPath)
    {
        try
        {
            var baseName = StoreBaseName(mediaPath);
            foreach (var ext in new[] { ".lrc", ".txt" })
            {
                var stored = Path.Combine(StoreDir, baseName + ext);
                if (File.Exists(stored)) return stored;
            }

            var dir = Path.GetDirectoryName(mediaPath);
            if (string.IsNullOrEmpty(dir)) return null;
            var stem = Path.GetFileNameWithoutExtension(mediaPath);

            foreach (var ext in new[] { ".lrc", ".txt" })
            {
                var candidate = Path.Combine(dir, stem + ext);
                if (File.Exists(candidate)) return candidate;
            }

            // Windows file names are case-insensitive but a network share may not be: one directory scan as a fallback
            foreach (var ext in new[] { ".lrc", ".txt" })
            {
                var hit = Directory.EnumerateFiles(dir, "*" + ext)
                    .FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }
        }
        catch { /* unreadable folder */ }
        return null;
    }

    public static LyricsDocument? Load(string lyricsPath)
    {
        try
        {
            // detectEncodingFromByteOrderMarks handles UTF-8 / UTF-16 files; plain files are read as UTF-8
            var text = File.ReadAllText(lyricsPath, Encoding.UTF8);
            var doc = LyricsParser.Parse(text, lyricsPath);
            return doc.IsEmpty ? null : doc;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Path where lyrics for this song are saved: the hidden Tempo folder (.lrc when timed, otherwise .txt).</summary>
    public static string SavePathFor(string mediaPath, string text) =>
        Path.Combine(StoreDir, StoreBaseName(mediaPath) + (LyricsParser.HasTimestamps(text) ? ".lrc" : ".txt"));

    /// <summary>
    /// Saves the lyrics into the hidden folder and returns the file written. The other kind (.lrc / .txt) for the same song is
    /// removed from the hidden folder, so the new one is what gets loaded next time. Files next to the song are never touched.
    /// </summary>
    public static string Save(string mediaPath, string text)
    {
        EnsureStore();
        var target = SavePathFor(mediaPath, text);
        File.WriteAllText(target, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var other = Path.ChangeExtension(target, string.Equals(Path.GetExtension(target), ".lrc", StringComparison.OrdinalIgnoreCase) ? ".txt" : ".lrc");
        try { if (File.Exists(other)) File.Delete(other); } catch { /* leave it */ }
        return target;
    }
}
