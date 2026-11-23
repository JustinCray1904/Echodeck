using System.Text.RegularExpressions;

namespace EchoDeck;

/// <summary>
/// "Smart queue": when one file is opened on its own (from Explorer, "Open with", a single drop, or a double-click in the library), the
/// other files of the same series are found by name and queued in order, so opening Episode 3 queues Episode 1 to 7 and plays Episode 3.
/// A series runs across its seasons: S01E10 is followed by S02E01, whether the seasons are in one folder or in folders next to each
/// other ("Season 1", "Season 2" / "Show S01", "Show S02"). Two files belong together when their names are the same apart from their
/// numbers ("Show S01E03 1080p.mkv", "Show S02E04 1080p.mkv"). Quality tags, release groups and bracketed parts are ignored; the numbers
/// that differ must be small (a counter, not a year or a camera's IMG_1234). Songs work the same way ("Track 01", "Track 02"), and only
/// files of the same kind (video with video, audio with audio) are considered. Pure functions: the disk is only read by the
/// one-argument SeriesOf.
/// </summary>
internal static class SmartQueue
{
    private static readonly Regex Brackets = new(@"[\[\(\{][^\]\)\}]*[\]\)\}]", RegexOptions.Compiled);

    private static readonly Regex Quality = new(
        @"\b(?:480|576|720|1080|1440|2160|4320)[pi]\b|\b[xh]\.?26[45]\b|\bhevc\b|\bav1\b|\b(?:blu-?ray|bdrip|brrip|web-?dl|web-?rip|hdtv|dvdrip|hdrip|remux|amzn|10bit|8bit|aac|ac3|dts|ddp?5\.1|atmos|hdr)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SeasonEpisode = new(
        @"(?<![a-z0-9])s(\d{1,2})[ ._-]?e\d{1,3}(?!\d)|(?<!\d)(\d{1,2})x\d{2,3}(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "Season 2", "Series 2", "S02" or "The Show S02" as a folder name
    private static readonly Regex SeasonFolder = new(
        @"(?:^|[ ._\-\[\(])(?:season|series|saison|temporada|staffel|s)[ ._-]*(\d{1,2})(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Digits = new(@"\d+", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s{2,}", RegexOptions.Compiled);

    private sealed record Signature(string Template, long[] Numbers, int? Season);

    /// <summary>"Season 2" gives (2, ""), "The Show S03" gives (3, "the show"); null when the folder is not a season folder.</summary>
    private static (int Season, string Base)? SeasonOfFolder(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return null;
        var m = SeasonFolder.Match(folderName);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out int season)) return null;

        string rest = folderName.Remove(m.Index, m.Length).ToLowerInvariant();
        rest = Quality.Replace(Brackets.Replace(rest, " "), " ");
        rest = rest.Replace('.', ' ').Replace('_', ' ').Replace('-', ' ');
        return (season, Spaces.Replace(rest, " ").Trim());
    }

    private static int? SeasonOfFolderOf(string path)
    {
        try { return SeasonOfFolder(Path.GetFileName(Path.GetDirectoryName(path)))?.Season; }
        catch { return null; }
    }

    private static Signature? SignatureOf(string path)
    {
        string stem = Path.GetFileNameWithoutExtension(path) ?? "";
        stem = Brackets.Replace(stem, " ");
        stem = Quality.Replace(stem, " ");
        stem = stem.Replace('.', ' ').Replace('_', ' ');
        stem = Spaces.Replace(stem, " ").Trim().ToLowerInvariant();

        int? season = null;
        var se = SeasonEpisode.Match(stem);
        if (se.Success)
        {
            string digits = se.Groups[1].Success ? se.Groups[1].Value : se.Groups[2].Value;
            if (int.TryParse(digits, out int s)) season = s;
        }
        season ??= SeasonOfFolderOf(path);   // "Season 2\Episode 1.mp4": the season is in the folder's name

        var numbers = new List<long>();
        string template = Digits.Replace(stem, m =>
        {
            numbers.Add(long.TryParse(m.Value, out long v) ? v : 0);
            return "#";
        });
        return numbers.Count == 0 ? null : new Signature(template, numbers.ToArray(), season);
    }

    private static string NumberKey(long[] numbers) => string.Join(",", numbers);

    /// <summary>The series that <paramref name="path"/> belongs to, in order (just the file itself when it is not part of one).</summary>
    internal static List<string> SeriesOf(IEnumerable<string> siblings, string path)
    {
        var alone = new List<string> { path };
        var mine = SignatureOf(path);
        if (mine == null) return alone;

        var matches = new List<(string Path, Signature Sig)>();
        foreach (var s in siblings)
        {
            if (string.Equals(s, path, StringComparison.OrdinalIgnoreCase)) { matches.Add((path, mine)); continue; }

            var sig = SignatureOf(s);
            if (sig == null || sig.Template != mine.Template || sig.Numbers.Length != mine.Numbers.Length) continue;
            matches.Add((s, sig));
        }
        if (!matches.Any(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase))) matches.Add((path, mine));

        // one file per episode number (the same episode as .mp4 and .mkv is not queued twice); the opened file wins
        var byNumber = new Dictionary<string, (string Path, Signature Sig)>();
        foreach (var m in matches)
        {
            string key = (m.Sig.Season?.ToString() ?? "") + "|" + NumberKey(m.Sig.Numbers);
            if (!byNumber.TryGetValue(key, out var kept) || string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase))
                byNumber[key] = m;
        }
        var list = byNumber.Values.ToList();
        if (list.Count < 2) return alone;

        // the numbers that differ between the files must be counters, not years or file counters
        for (int i = 0; i < mine.Numbers.Length; i++)
        {
            var values = list.Select(m => m.Sig.Numbers[i]).Distinct().ToList();
            if (values.Count > 1 && values.Any(v => v >= 1000)) return alone;
        }

        list.Sort((a, b) =>
        {
            int bySeason = (a.Sig.Season ?? 0).CompareTo(b.Sig.Season ?? 0);   // the season of a folder name comes before the episode numbers
            if (bySeason != 0) return bySeason;
            for (int i = 0; i < a.Sig.Numbers.Length; i++)
            {
                int c = a.Sig.Numbers[i].CompareTo(b.Sig.Numbers[i]);
                if (c != 0) return c;
            }
            return string.Compare(a.Path, b.Path, StringComparison.CurrentCultureIgnoreCase);
        });
        return list.Select(m => m.Path).ToList();
    }

    /// <summary>Reads the file's folder (and the season folders next to it) and finds its series. Never throws.</summary>
    internal static List<string> SeriesOf(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return new List<string> { path };
            bool audio = MediaTypes.IsAudio(path);

            var siblings = MediaIn(dir, audio);

            // "Show\Season 1" is next to "Show\Season 2": the next season is part of the same series
            var own = SeasonOfFolder(Path.GetFileName(dir));
            string? parent = Path.GetDirectoryName(dir);
            if (own != null && !string.IsNullOrEmpty(parent))
            {
                foreach (var other in Directory.EnumerateDirectories(parent))
                {
                    if (siblings.Count >= 3000) break;
                    if (string.Equals(other, dir, StringComparison.OrdinalIgnoreCase)) continue;
                    var theirs = SeasonOfFolder(Path.GetFileName(other));
                    if (theirs == null || theirs.Value.Base != own.Value.Base) continue;
                    siblings.AddRange(MediaIn(other, audio));
                }
            }
            return SeriesOf(siblings, path);
        }
        catch
        {
            return new List<string> { path };   // an unreadable folder: just the file
        }
    }

    private static List<string> MediaIn(string dir, bool audio)
    {
        try
        {
            return Directory.EnumerateFiles(dir)
                .Where(f => MediaTypes.IsSupportedFile(f) && MediaTypes.IsAudio(f) == audio)
                .Take(3000).ToList();
        }
        catch { return new List<string>(); }   // an unreadable folder next to this one
    }
}
