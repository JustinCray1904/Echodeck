using System.Text.Json;
using System.Text.Json.Serialization;

namespace EchoDeck;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ThemeMode { Dark, Light, System }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LoopMode { Off, One, All }

/// <summary>
/// User preferences, stored as JSON in %LOCALAPPDATA%\EchoDeck\settings.json.
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

    // ---- interface / privacy
    public bool ShowSidebarAtStart { get; set; } = true;
    public bool ShowAnonymousInMenu { get; set; } = true;
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

    // ---- performance (applies on next launch)
    public bool HardwareDecoding { get; set; } = true;

    public static string FilePath => Path.Combine(AppPaths.DataDir, "settings.json");

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            Current = new AppSettings();
        }
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
