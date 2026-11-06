using System.Collections.Concurrent;
using System.Text.Json;
using TagFile = TagLib.File;

namespace EchoDeck;

/// <summary>One song as the Music page lists it (missing tags already replaced by "Unknown ...").</summary>
public sealed record Track(string Path, string Title, string Artist, string Album, string Genre, string Duration);

/// <summary>Progress of a background library scan, shown as the loading bar on the Music / Videos pages.</summary>
public sealed record ScanProgress(bool Music, int Done, int Total, bool Indeterminate, string Text, bool Finished);

/// <summary>Song details as read from the file's own tags.</summary>
internal sealed record TagInfo(string Title, string Artist, string Album, string Genre, long DurationSeconds);

/// <summary>One cached song (what was read last time, plus the size / write time that prove the file is unchanged).</summary>
internal sealed class CachedTrack
{
    public string P { get; set; } = "";   // path
    public long S { get; set; }           // size
    public long M { get; set; }           // last write time (UTC ticks)
    public string T { get; set; } = "";   // title
    public string A { get; set; } = "";   // artist
    public string B { get; set; } = "";   // album
    public string G { get; set; } = "";   // genre
    public long D { get; set; }           // duration, seconds
}

internal sealed class LibraryCacheFile
{
    public int Version { get; set; } = 1;
    public List<CachedTrack> Tracks { get; set; } = new();
    public Dictionary<string, List<string>> Videos { get; set; } = new();
}

/// <summary>
/// Finds the media in the library folders and reads song details straight from the file tags (TagLib, in parallel).
/// Everything read is cached in %LOCALAPPDATA%\Tempo\library-cache.json, keyed by path + size + write time, so a later
/// launch only reads files that are new or changed, and a scan that was interrupted carries on where it stopped
/// (the cache is checkpointed while the scan runs).
/// </summary>
internal static class LibraryScanner
{
    private const int MaxFiles = 30000;
    private const int CheckpointEvery = 400;

    private static readonly object CacheLock = new();
    private static readonly object SaveLock = new();
    private static readonly ConcurrentDictionary<string, CachedTrack> TrackCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<string>> VideoCache = new();
    private static bool _loaded;

    private static List<Track>? _tracks;
    private static string _tracksKey = "";
    private static Task<List<Track>>? _tracksTask;
    private static List<string>? _videos;
    private static string _videosKey = "";
    private static CancellationTokenSource _cts = new();

    /// <summary>Bumped whenever the in-memory scans are dropped, so pages know to rebuild.</summary>
    public static int Version { get; private set; }

    /// <summary>Raised on background threads while a scan runs.</summary>
    public static event Action<ScanProgress>? Progress;

    private static string CachePath => Path.Combine(AppPaths.DataDir, "library-cache.json");

    /// <summary>The library folders changed: stop any running scan and drop the in-memory results (the file cache stays valid).</summary>
    public static void Invalidate()
    {
        Version++;
        try { _cts.Cancel(); } catch { /* already disposed */ }
        _cts = new CancellationTokenSource();
        _tracks = null;
        _tracksTask = null;
        _videos = null;
    }

    /// <summary>App is closing: stop scanning and keep what has been read so far.</summary>
    public static void Shutdown()
    {
        try { _cts.Cancel(); } catch { /* ignore */ }
        SaveCache();
    }

    private static string Key(IEnumerable<string> folders) =>
        string.Join("|", folders.Select(f => f.TrimEnd('\\').ToLowerInvariant()).OrderBy(f => f));

    private static void Report(bool music, int done, int total, bool indeterminate, string text, bool finished = false) =>
        Progress?.Invoke(new ScanProgress(music, done, total, indeterminate, text, finished));

    // ------------------------------------------------------------------ cache file

    private static void EnsureLoaded()
    {
        lock (CacheLock)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(CachePath)) return;
                var data = JsonSerializer.Deserialize<LibraryCacheFile>(File.ReadAllText(CachePath));
                if (data == null || data.Version != 1) return;

                foreach (var t in data.Tracks)
                    if (!string.IsNullOrEmpty(t.P)) TrackCache[t.P] = t;
                foreach (var kv in data.Videos) VideoCache[kv.Key] = kv.Value;
            }
            catch
            {
                TrackCache.Clear();   // a damaged cache is just rebuilt by the next scan
                VideoCache.Clear();
            }
        }
    }

    private static void SaveCache()
    {
        if (!_loaded) return;
        lock (SaveLock)
        {
            try
            {
                LibraryCacheFile snapshot;
                lock (CacheLock)
                {
                    snapshot = new LibraryCacheFile
                    {
                        Tracks = TrackCache.Values.ToList(),
                        Videos = VideoCache.ToDictionary(kv => kv.Key, kv => kv.Value)
                    };
                }

                Directory.CreateDirectory(AppPaths.DataDir);
                var tmp = CachePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot));
                File.Move(tmp, CachePath, overwrite: true);
            }
            catch { /* the cache is an optimisation; never fail over it */ }
        }
    }

    // ------------------------------------------------------------------ file enumeration

    private static List<FileInfo> EnumerateInfos(IEnumerable<string> folders, Func<string, bool> match,
        CancellationToken ct, Action<int>? onCount)
    {
        var found = new List<FileInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint
        };

        foreach (var root in folders.Where(Directory.Exists))
        {
            try
            {
                // FileInfo objects from an enumeration already carry size + write time: no extra disk access per file.
                foreach (var fi in new DirectoryInfo(root).EnumerateFiles("*", options))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!match(fi.Extension)) continue;
                    if (!seen.Add(fi.FullName)) continue;
                    found.Add(fi);
                    if (found.Count >= MaxFiles) return found;
                    if (found.Count % 250 == 0) onCount?.Invoke(found.Count);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* unreadable folder */ }
        }
        return found;
    }

    /// <summary>Plain list of matching file paths (used where no progress or caching is needed).</summary>
    public static List<string> Enumerate(IEnumerable<string> folders, Func<string, bool> match) =>
        EnumerateInfos(folders, match, CancellationToken.None, null).Select(f => f.FullName).ToList();

    private static bool UnderAny(string path, List<string> roots)
    {
        foreach (var r in roots)
            if (path.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static List<string> RootsOf(IEnumerable<string> folders) =>
        folders.Select(f => f.TrimEnd('\\') + "\\").ToList();

    // ------------------------------------------------------------------ videos

    /// <summary>What was found last time (instant). Empty when nothing is cached for these folders.</summary>
    public static Task<List<string>> CachedVideosAsync(IReadOnlyList<string> folders) => Task.Run(() =>
    {
        EnsureLoaded();
        lock (CacheLock)
            return VideoCache.TryGetValue(Key(folders), out var list) ? list.ToList() : new List<string>();
    });

    public static Task<List<string>> VideosAsync(IReadOnlyList<string> folders)
    {
        var key = Key(folders);
        if (_videos != null && key == _videosKey) return Task.FromResult(_videos);

        var ct = _cts.Token;
        return Task.Run(() =>
        {
            EnsureLoaded();
            Report(false, 0, 0, true, "Looking for videos…");
            try
            {
                var infos = EnumerateInfos(folders, e => MediaTypes.IsSupportedExtension(e) && !MediaTypes.IsAudioExtension(e), ct,
                    n => Report(false, n, 0, true, "Looking for videos…  " + n.ToString("N0") + " found"));
                var list = infos.Select(f => f.FullName).ToList();
                list.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.CurrentCultureIgnoreCase));

                _videos = list;
                _videosKey = key;
                lock (CacheLock) VideoCache[key] = list.ToList();
                SaveCache();
                return list;
            }
            finally
            {
                Report(false, 0, 0, false, "", finished: true);
            }
        }, ct);
    }

    // ------------------------------------------------------------------ music

    private static Track ToTrack(CachedTrack c)
    {
        string title = c.T.Length > 0 ? c.T : Path.GetFileNameWithoutExtension(c.P);
        string artist = c.A.Length > 0 ? FirstArtist(c.A) : "Unknown artist";
        string album = c.B.Length > 0 ? c.B : "Unknown album";
        string genre = c.G.Length > 0 ? c.G : "Unknown genre";
        return new Track(c.P, title, artist, album, genre, FormatDuration(c.D));
    }

    private static string FirstArtist(string s)
    {
        int i = s.IndexOf(';');
        return (i > 0 ? s.Substring(0, i) : s).Trim();
    }

    public static string FormatDuration(long seconds)
    {
        if (seconds <= 0) return "";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes:00}:{ts.Seconds:00}";
    }

    /// <summary>Title, artist, album, genre and length from the file's own tags (empty strings when unreadable).</summary>
    internal static TagInfo ReadTags(string path)
    {
        string title = "", artist = "", album = "", genre = "";
        long seconds = 0;
        try
        {
            var file = TagFile.Create(path, TagLib.ReadStyle.Average);
            try
            {
                var tag = file.Tag;
                if (tag != null)
                {
                    title = (tag.Title ?? "").Trim();
                    artist = (tag.FirstPerformer ?? tag.FirstAlbumArtist ?? "").Trim();
                    album = (tag.Album ?? "").Trim();
                    genre = (tag.FirstGenre ?? "").Trim();
                }
                var props = file.Properties;
                if (props != null) seconds = (long)props.Duration.TotalSeconds;
            }
            finally
            {
                (file as IDisposable)?.Dispose();
            }
        }
        catch
        {
            // unsupported or damaged tags: the library falls back to the file name
        }
        return new TagInfo(title, artist, album, genre, seconds);
    }

    private static CachedTrack ReadTrack(FileInfo fi)
    {
        var tags = ReadTags(fi.FullName);
        return new CachedTrack
        {
            P = fi.FullName, S = fi.Length, M = fi.LastWriteTimeUtc.Ticks,
            T = tags.Title, A = tags.Artist, B = tags.Album, G = tags.Genre, D = tags.DurationSeconds
        };
    }

    /// <summary>What was read last time for these folders (instant, possibly slightly out of date until the scan finishes).</summary>
    public static Task<List<Track>> CachedTracksAsync(IReadOnlyList<string> folders) => Task.Run(() =>
    {
        EnsureLoaded();
        var roots = RootsOf(folders);
        var list = TrackCache.Values.Where(c => UnderAny(c.P, roots)).Select(ToTrack).ToList();
        list.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
        return list;
    });

    public static Task<List<Track>> TracksAsync(IReadOnlyList<string> folders)
    {
        var key = Key(folders);
        if (_tracks != null && key == _tracksKey) return Task.FromResult(_tracks);
        if (_tracksTask != null && key == _tracksKey) return _tracksTask;

        _tracksKey = key;
        var ct = _cts.Token;
        var task = Task.Run(() => ScanTracks(folders, key, ct), ct);
        _tracksTask = task;
        task.ContinueWith(t =>
        {
            _ = t.Exception; // observed
            if (ReferenceEquals(_tracksTask, t)) _tracksTask = null;   // cancelled / failed: the next visit scans again
        }, TaskContinuationOptions.NotOnRanToCompletion);
        return task;
    }

    private static List<Track> ScanTracks(IReadOnlyList<string> folders, string key, CancellationToken ct)
    {
        EnsureLoaded();
        try
        {
            Report(true, 0, 0, true, "Looking for music files…");
            var infos = EnumerateInfos(folders, MediaTypes.IsAudioExtension, ct,
                n => Report(true, n, 0, true, "Looking for music files…  " + n.ToString("N0") + " found"));

            var result = new Track?[infos.Count];
            var pending = new List<int>();
            for (int i = 0; i < infos.Count; i++)
            {
                var fi = infos[i];
                if (TrackCache.TryGetValue(fi.FullName, out var cached) && cached.S == fi.Length && cached.M == fi.LastWriteTimeUtc.Ticks)
                    result[i] = ToTrack(cached);
                else
                    pending.Add(i);
            }

            int total = pending.Count;
            if (total > 0)
            {
                Report(true, 0, total, false, "Reading song details…");
                int done = 0;
                var options = new ParallelOptions
                {
                    CancellationToken = ct,
                    MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8)
                };

                Parallel.ForEach(pending, options, idx =>
                {
                    var fi = infos[idx];
                    var c = ReadTrack(fi);
                    TrackCache[fi.FullName] = c;
                    result[idx] = ToTrack(c);

                    int d = Interlocked.Increment(ref done);
                    if (d % 20 == 0 || d == total) Report(true, d, total, false, "Reading song details…");
                    if (d % CheckpointEvery == 0) SaveCache();   // an interrupted scan resumes from here
                });
            }

            // forget songs that were deleted since last time (only inside the folders just scanned)
            var live = new HashSet<string>(infos.Select(f => f.FullName), StringComparer.OrdinalIgnoreCase);
            var roots = RootsOf(folders);
            foreach (var path in TrackCache.Keys.ToList())
                if (UnderAny(path, roots) && !live.Contains(path)) TrackCache.TryRemove(path, out _);
            SaveCache();

            var list = result.Where(t => t != null).Select(t => t!).ToList();
            list.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
            _tracks = list;
            _tracksKey = key;
            return list;
        }
        catch (OperationCanceledException)
        {
            SaveCache();   // keep everything read so far
            throw;
        }
        finally
        {
            Report(true, 0, 0, false, "", finished: true);
        }
    }
}
