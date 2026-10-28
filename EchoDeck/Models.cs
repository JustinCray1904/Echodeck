using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EchoDeck;

public enum EntryKind { Folder, Video, Audio, Playlist, Album, Artist }

/// <summary>One tile / row in the library or the side panel. Thumbnails arrive lazily and are released again off-screen.</summary>
public sealed class BrowserEntry : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;
    private bool _selected;

    public BrowserEntry(string name, string fullPath, EntryKind kind, string subtitle = "", Playlist? playlist = null)
    {
        Name = name;
        FullPath = fullPath;
        Kind = kind;
        Subtitle = subtitle;
        Playlist = playlist;
    }

    public string Name { get; }
    public string FullPath { get; }
    public EntryKind Kind { get; }
    public string Subtitle { get; set; }
    public Playlist? Playlist { get; }

    // music metadata (songs / albums / artists pages)
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Genre { get; set; } = "";
    public string Duration { get; set; } = "";
    public bool Stripe { get; set; }
    public List<BrowserEntry>? Children { get; set; }

    public bool IsAlbum => Kind == EntryKind.Album;
    public bool IsArtist => Kind == EntryKind.Artist;
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name.Trim().Substring(0, 1).ToUpperInvariant();
    public bool IsDirectory => Kind == EntryKind.Folder;
    public bool IsAudio => Kind == EntryKind.Audio;
    public bool IsPlaylist => Kind == EntryKind.Playlist;
    public bool IsFile => Kind == EntryKind.Audio || Kind == EntryKind.Video;
    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);

    /// <summary>True while a tile for this entry is on screen (thumbnails are only kept for visible tiles).</summary>
    public bool Visible { get; set; }

    public Geometry Icon => IconSet.Get(Kind switch
    {
        EntryKind.Folder => "IcoFolder",
        EntryKind.Audio => "IcoMusic",
        EntryKind.Playlist => "IcoPlaylist",
        EntryKind.Album => "IcoDisc",
        EntryKind.Artist => "IcoUser",
        _ => "IcoVideo"
    });

    /// <summary>File whose Explorer thumbnail (or image) represents this entry; null for plain folders.</summary>
    public string? ThumbSource
    {
        get
        {
            if (IsFile) return FullPath;
            if (Children != null) return Children.FirstOrDefault(c => c.IsFile)?.FullPath;
            if (Playlist != null)
            {
                if (!string.IsNullOrEmpty(Playlist.Cover) && File.Exists(Playlist.Cover)) return Playlist.Cover;
                return Playlist.Items.FirstOrDefault(File.Exists);
            }
            return null;
        }
    }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    private bool _checked, _playing;

    /// <summary>The tick-box selection (several files at once).</summary>
    public bool IsChecked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            SelectionState.Instance.Change(value ? 1 : -1);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    /// <summary>True for the queue row that is playing right now.</summary>
    public bool IsPlaying
    {
        get => _playing;
        set
        {
            if (_playing == value) return;
            _playing = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlaying)));
        }
    }

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A row of up to four tiles (grid view) or a single entry (list view): this is what the virtualised list holds.</summary>
public sealed class EntryRow
{
    public EntryRow(List<BrowserEntry> items) => Items = items;
    public List<BrowserEntry> Items { get; }
}

/// <summary>One row in the play queue.</summary>
public sealed record QueueItem(string FullPath)
{
    public string Name => Path.GetFileName(FullPath);
    public Geometry Icon => IconSet.Get(MediaTypes.IsAudio(FullPath) ? "IcoMusic" : "IcoVideo");
}

internal static class IconSet
{
    public static Geometry Get(string key) => (Geometry)Application.Current.FindResource(key);
}

/// <summary>height = width * parameter (keeps thumbnails 16:9 whatever the window width).</summary>
public sealed class RatioConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d && parameter is string p ? d * double.Parse(p, CultureInfo.InvariantCulture) : 0.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

// ---------------------------------------------------------------------------------------------------------------------

public sealed class Playlist
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string? Cover { get; set; }
    public List<string> Items { get; set; } = new();
}

/// <summary>Playlists the user creates on purpose, saved to %LOCALAPPDATA%\EchoDeck\playlists.json.</summary>
public static class PlaylistStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static bool _loaded;

    public static List<Playlist> All { get; private set; } = new();

    private static string FilePath => Path.Combine(AppPaths.DataDir, "playlists.json");

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(FilePath))
                All = JsonSerializer.Deserialize<List<Playlist>>(File.ReadAllText(FilePath)) ?? new List<Playlist>();
        }
        catch { All = new List<Playlist>(); }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(All, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* best effort */ }
    }

    public static Playlist Create(string name)
    {
        EnsureLoaded();
        var pl = new Playlist { Name = name.Trim() };
        All.Add(pl);
        Save();
        return pl;
    }

    public static void Delete(Playlist pl)
    {
        All.Remove(pl);
        Save();
    }

    public static string CoversFolder => Path.Combine(AppPaths.DataDir, "covers");
}

/// <summary>Recently played files (newest first). Anonymous Mode never writes here.</summary>
public static class RecentStore
{
    private const int Max = 60;
    private static bool _loaded;
    private static List<string> _items = new();

    private static string FilePath => Path.Combine(AppPaths.DataDir, "recent.json");

    public static IReadOnlyList<string> Items
    {
        get { EnsureLoaded(); return _items; }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(FilePath))
                _items = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? new List<string>();
        }
        catch { _items = new List<string>(); }
    }

    public static void Add(string path)
    {
        EnsureLoaded();
        _items.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _items.Insert(0, path);
        if (_items.Count > Max) _items.RemoveRange(Max, _items.Count - Max);
        Save();
    }

    public static void Remove(string path)
    {
        EnsureLoaded();
        _items.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    public static void Clear()
    {
        EnsureLoaded();
        _items.Clear();
        Save();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_items));
        }
        catch { /* best effort */ }
    }
}

/// <summary>Loads Explorer thumbnails for the tiles that are on screen, three at a time.</summary>
internal static class ThumbnailLoader
{
    private static readonly SemaphoreSlim Gate = new(3);

    public static async void Request(BrowserEntry entry)
    {
        var source = entry.ThumbSource;
        if (source == null || entry.Thumbnail != null || !entry.Visible) return;

        await Gate.WaitAsync();
        try
        {
            if (!entry.Visible || entry.Thumbnail != null) return;
            var bmp = await Task.Run(() => Decode(source));
            if (bmp != null && entry.Visible) entry.Thumbnail = bmp;
        }
        catch { /* thumbnails are optional */ }
        finally
        {
            Gate.Release();
        }
    }

    private static BitmapSource? Decode(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".webp")
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.DecodePixelWidth = 320;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch { return null; }
        }
        return ShellThumbnail.Get(path, 256, 144);
    }
}


/// <summary>How many tiles are ticked: drives the selection bar and makes every tile show its tick box.</summary>
public sealed class SelectionState : INotifyPropertyChanged
{
    public static SelectionState Instance { get; } = new();

    private int _count;

    public int Count => _count;
    public bool Active => _count > 0;

    public void Change(int delta)
    {
        _count = Math.Max(0, _count + delta);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Active)));
    }

    public void Reset()
    {
        _count = 0;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Active)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record Track(string Path, string Title, string Artist, string Album, string Genre, string Duration);

/// <summary>
/// Finds the media in the library folders and reads song details (artist, album, genre, length) through the same
/// Windows shell properties Explorer shows. Both are cached per session and built off the UI thread.
/// </summary>
internal static class LibraryScanner
{
    private const int MaxFiles = 6000;
    private static List<Track>? _tracks;
    private static string _tracksKey = "";
    private static Task<List<Track>>? _tracksTask;
    private static List<string>? _videos;
    private static string _videosKey = "";

    /// <summary>Bumped whenever the cached scans are dropped, so pages know to rebuild.</summary>
    public static int Version { get; private set; }

    public static void Invalidate()
    {
        Version++;
        _tracks = null;
        _tracksTask = null;
        _videos = null;
    }

    private static string Key(IEnumerable<string> folders) =>
        string.Join("|", folders.Select(f => f.TrimEnd('\\').ToLowerInvariant()).OrderBy(f => f));

    public static List<string> Enumerate(IEnumerable<string> folders, Func<string, bool> match)
    {
        var found = new List<string>();
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
                foreach (var f in Directory.EnumerateFiles(root, "*", options))
                {
                    if (!match(Path.GetExtension(f))) continue;
                    found.Add(f);
                    if (found.Count >= MaxFiles) return found;
                }
            }
            catch { /* unreadable folder */ }
        }
        return found;
    }

    public static Task<List<string>> VideosAsync(IReadOnlyList<string> folders)
    {
        var key = Key(folders);
        if (_videos != null && key == _videosKey) return Task.FromResult(_videos);

        return Task.Run(() =>
        {
            var list = Enumerate(folders, e => MediaTypes.IsSupportedExtension(e) && !MediaTypes.IsAudioExtension(e));
            list.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.CurrentCultureIgnoreCase));
            _videos = list;
            _videosKey = key;
            return list;
        });
    }

    public static Task<List<Track>> TracksAsync(IReadOnlyList<string> folders)
    {
        var key = Key(folders);
        if (_tracks != null && key == _tracksKey) return Task.FromResult(_tracks);
        if (_tracksTask != null && key == _tracksKey) return _tracksTask;

        _tracksKey = key;
        _tracksTask = Task.Run(async () =>
        {
            var files = Enumerate(folders, MediaTypes.IsAudioExtension);
            var details = await ShellDetails.ReadAsync(files);

            var list = new List<Track>(files.Count);
            foreach (var f in files)
            {
                details.TryGetValue(f, out var d);
                string title = d != null && d[0].Length > 0 ? d[0] : Path.GetFileNameWithoutExtension(f);
                string artist = d != null && d[1].Length > 0 ? FirstArtist(d[1]) : "Unknown artist";
                string album = d != null && d[2].Length > 0 ? d[2] : "Unknown album";
                string genre = d != null && d[3].Length > 0 ? d[3] : "Unknown genre";
                string length = d != null ? ShortLength(d[4]) : "";
                list.Add(new Track(f, title, artist, album, genre, length));
            }
            list.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
            _tracks = list;
            return list;
        });
        return _tracksTask;
    }

    private static string FirstArtist(string s)
    {
        int i = s.IndexOf(';');
        return (i > 0 ? s.Substring(0, i) : s).Trim();
    }

    /// <summary>Explorer reports 00:03:12; show 03:12 (and keep hours when there are any).</summary>
    private static string ShortLength(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        if (s.StartsWith("00:", StringComparison.Ordinal) && s.Length > 3) return s.Substring(3);
        return s;
    }
}

/// <summary>Reads the Explorer "details" columns of audio files (title, artist, album, genre, length).</summary>
internal static class ShellDetails
{
    public static Task<Dictionary<string, string[]>> ReadAsync(IReadOnlyList<string> files)
    {
        var tcs = new TaskCompletionSource<Dictionary<string, string[]>>();
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(ReadSync(files)); }
            catch { tcs.SetResult(new Dictionary<string, string[]>()); }
        });
        thread.SetApartmentState(ApartmentState.STA); // Shell.Application needs a single-threaded apartment
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    private static Dictionary<string, string[]> ReadSync(IReadOnlyList<string> files)
    {
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type == null) return result;

        dynamic shell = Activator.CreateInstance(type)!;
        int cTitle = -1, cArtist = -1, cAlbum = -1, cGenre = -1, cLength = -1;

        foreach (var group in files.GroupBy(f => Path.GetDirectoryName(f) ?? ""))
        {
            if (group.Key.Length == 0) continue;
            dynamic? ns = null;
            try { ns = shell.NameSpace(group.Key); } catch { }
            if (ns == null) continue;

            if (cTitle < 0)
            {
                // column numbers differ between Windows versions, so look them up by name once
                for (int i = 0; i < 320; i++)
                {
                    string header;
                    try { header = ((string?)ns.GetDetailsOf(null, i) ?? "").Trim().ToLowerInvariant(); }
                    catch { continue; }

                    if (header == "title" && cTitle < 0) cTitle = i;
                    else if ((header == "contributing artists" || header == "artists" || header == "artist") && cArtist < 0) cArtist = i;
                    else if (header == "album" && cAlbum < 0) cAlbum = i;
                    else if (header == "genre" && cGenre < 0) cGenre = i;
                    else if (header == "length" && cLength < 0) cLength = i;
                }
                if (cTitle < 0) cTitle = 21;
            }

            foreach (var file in group)
            {
                try
                {
                    dynamic? item = ns.ParseName(Path.GetFileName(file));
                    if (item == null) continue;
                    string Get(int col) => col < 0 ? "" : ((string?)ns.GetDetailsOf(item, col) ?? "").Trim();
                    result[file] = new[] { Get(cTitle), Get(cArtist), Get(cAlbum), Get(cGenre), Get(cLength) };
                }
                catch { /* this file has no readable tags */ }
            }
        }
        return result;
    }
}
