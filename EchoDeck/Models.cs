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

    /// <summary>Anonymous Mode entry: its file is encrypted, so the thumbnail comes from the vault, not from Explorer.</summary>
    public bool IsVault { get; set; }

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
    /// <summary>Set while Anonymous Mode is on: maps an encrypted file's path to its real name and kind.</summary>
    public static Func<string, (string Name, bool IsAudio)?>? Resolver { get; set; }

    public string Name => Resolver?.Invoke(FullPath)?.Name ?? Path.GetFileName(FullPath);
    public Geometry Icon => IconSet.Get((Resolver?.Invoke(FullPath)?.IsAudio ?? MediaTypes.IsAudio(FullPath)) ? "IcoMusic" : "IcoVideo");
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

/// <summary>Playlists the user creates on purpose, saved to %LOCALAPPDATA%\Tempo\playlists.json.</summary>
public static class PlaylistStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static bool _loaded;
    private static List<Playlist> _normal = new();
    private static VaultService? _vault;

    /// <summary>
    /// The playlists the pages work with: the normal ones, or - while Anonymous Mode is on - the encrypted vault
    /// playlists. Every playlist screen uses this one list, so it behaves the same in both modes.
    /// </summary>
    public static List<Playlist> All => _vault?.Playlists ?? _normal;

    /// <summary>Switches playlists to the vault (Anonymous Mode on) or back to the normal ones (null).</summary>
    internal static void UseVault(VaultService? vault) => _vault = vault;

    private static string FilePath => Path.Combine(AppPaths.DataDir, "playlists.json");

    public static void EnsureLoaded()
    {
        if (_vault != null || _loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(FilePath))
                _normal = JsonSerializer.Deserialize<List<Playlist>>(File.ReadAllText(FilePath)) ?? new List<Playlist>();
        }
        catch { _normal = new List<Playlist>(); }
    }

    public static void Save()
    {
        if (_vault != null)
        {
            _vault.SavePlaylists();
            return;
        }

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

    /// <summary>Set while Anonymous Mode is on: decrypts a vault file's small encrypted thumbnail (in memory only).</summary>
    public static Func<string, BitmapSource?>? VaultDecoder { get; set; }

    public static async void Request(BrowserEntry entry)
    {
        var source = entry.ThumbSource;
        if (source == null || entry.Thumbnail != null || !entry.Visible) return;

        await Gate.WaitAsync();
        try
        {
            if (!entry.Visible || entry.Thumbnail != null) return;
            bool vault = entry.IsVault;
            var bmp = await Task.Run(() => vault ? VaultDecoder?.Invoke(source) : Decode(source));
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
