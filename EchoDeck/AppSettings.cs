using System.Text.Json;
using System.Text.Json.Serialization;

namespace EchoDeck;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ThemeMode { Dark, Light, System, Midnight, Amoled, LiquidGlass /* retired: only kept so an old settings.json still loads; it becomes Dark */ }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LoopMode { Off, One, All }

/// <summary>
/// User preferences, stored as JSON in %LOCALAPPDATA%\Tempo\settings.json.
/// Nothing about what was watched or played is ever stored here (no history, no resume positions).
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Current { get; private set; } = new();

    /// <summary>Raised after a setting changed and was saved, so the player can apply it live.</summary>
    public static event Action? Changed;

    // ---- first run / look
    public bool OnboardingDone { get; set; }
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;
    public int AccentIndex { get; set; }                 // 0 = Sky (light blue)
    public string MediaFolder { get; set; } = "";
    public List<string> MusicFolders { get; set; } = new();
    public List<string> VideoFolders { get; set; } = new();

    // ---- interface / privacy
    public bool ShowSidebarAtStart { get; set; }
    public bool LibraryGrid { get; set; } = true;
    public string StartPage { get; set; } = "Recent";     // Recent, Home or Blank
    public bool RememberRecent { get; set; } = true;
    public bool AnonymousExitOnMinimize { get; set; } = true;
    public bool AnonymousDeleteOriginals { get; set; }

    // ---- playback
    public bool AutoAdvance { get; set; } = true;
    public LoopMode Loop { get; set; } = LoopMode.Off;
    public double DefaultSpeed { get; set; } = 1.0;
    public int SkipSeconds { get; set; } = 10;
    public int Volume { get; set; } = 80;

    // ---- video
    public bool StartFullscreen { get; set; }
    public bool KeepAwake { get; set; } = true;
    public double Hue { get; set; }                      // -180 .. 180 (0 = unchanged)
    public double Brightness { get; set; } = 1.0;        // 0 .. 2
    public double Contrast { get; set; } = 1.0;         // 0 .. 2
    public double Saturation { get; set; } = 1.0;        // 0 .. 3
    public double Gamma { get; set; } = 1.0;             // 0.1 .. 4
    public string AspectRatio { get; set; } = "Default";

    // ---- subtitles (size applies on next launch)
    public bool SubtitlesOnByDefault { get; set; } = true;
    public int SubtitleSize { get; set; } = 1;           // 0 small, 1 normal, 2 large, 3 larger

    // ---- performance / network / recording (cache + decoding apply on next launch)
    public bool HardwareDecoding { get; set; } = true;
    public int NetworkCachingMs { get; set; } = 1000;
    public string SnapshotFolder { get; set; } = "";
    public bool Shuffle { get; set; }
    public string HardwareMode { get; set; } = "auto";   // auto, d3d11, dxva2 (used while HardwareDecoding is on)

    // ---- interface additions
    public int VisualizerStyle { get; set; }              // music view: 0 ripples, 1 radial bars, 2 wave ring
    public bool TaskbarControls { get; set; } = true;     // taskbar thumbnail buttons + progress on the taskbar icon

    // ---- system-wide keys
    public string MiniDock { get; set; } = "right";        // small player docks to the "left" or "right" while browsing
    public bool SmoothResize { get; set; } = true;          // the video area slides when the side bar / queue opens (otherwise one quick step)
    public double VisualizerIntensity { get; set; } = 1.0;   // how tall / bold the visualiser moves (0.4 - 2.0)
    public double VisualizerSensitivity { get; set; } = 1.0; // how strongly it reacts to the song (0.4 - 2.5)
    public string LyricsEngine { get; set; } = "local";      // "local" (whisper.cpp on this PC) or "cloud" (OpenAI-compatible service)
    public string CloudEndpoint { get; set; } = "https://api.openai.com/v1";
    public string CloudModel { get; set; } = "whisper-1";
    public string CloudKeyProtected { get; set; } = "";      // API key, encrypted with Windows DPAPI for this user
    public bool RememberPosition { get; set; } = true;    // offer to continue a video where it was left off
    public bool GlobalMediaKeys { get; set; } = true;     // Play/Pause, Next, Previous, Stop keys work with Tempo in the background
    public bool GlobalVolumeKeys { get; set; }            // volume keys change Tempo's volume instead of the system volume

    // ---- lyrics
    public bool AutoLoadLyrics { get; set; } = true;      // lyrics from the hidden Tempo lyrics folder (or an .lrc / .txt next to the song)
    public bool LyricsOpen { get; set; }                  // lyrics panel is open in the music view
    public bool SaveGeneratedLyrics { get; set; } = true; // keep AI-generated lyrics in the hidden Tempo lyrics folder
    public string WhisperExePath { get; set; } = "";      // whisper.cpp command line program (whisper-cli.exe)
    public string WhisperModelPath { get; set; } = "";    // ggml model, for example ggml-base.en.bin
    public string WhisperLanguage { get; set; } = "auto"; // auto, en, es, fr ...

    /// <summary>Library locations: seeded from the single folder chosen earlier, or the Windows Music / Videos folders.</summary>
    public void EnsureFolders()
    {
        MusicFolders = MusicFolders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        VideoFolders = VideoFolders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (MusicFolders.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(MediaFolder) && Directory.Exists(MediaFolder)) MusicFolders.Add(MediaFolder);
            MusicFolders.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        }
        if (VideoFolders.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(MediaFolder) && Directory.Exists(MediaFolder)) VideoFolders.Add(MediaFolder);
            VideoFolders.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        }
        MusicFolders = MusicFolders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        VideoFolders = VideoFolders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string FilePath => Path.Combine(AppPaths.DataDir, "settings.json");

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            if (Current.Theme == ThemeMode.LiquidGlass) Current.Theme = ThemeMode.Dark;   // the Liquid Glass theme was removed
        }
        catch
        {
            Current = new AppSettings();
        }
        Current.EnsureFolders();
    }

    /// <summary>Saves to disk (best effort) and notifies listeners.</summary>
    public void Save(bool notify = true)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* settings are a convenience; never crash over them */ }

        if (notify) Changed?.Invoke();
    }
}
