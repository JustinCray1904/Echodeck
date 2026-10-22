using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EchoDeck;

public enum EntryKind { Folder, Video, Audio, Playlist }

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
        _ => "IcoVideo"
    });

    /// <summary>File whose Explorer thumbnail (or image) represents this entry; null for plain folders.</summary>
    public string? ThumbSource
    {
        get
        {
            if (IsFile) return FullPath;
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
