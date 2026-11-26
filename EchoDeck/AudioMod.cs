using System.Text.Json;

namespace EchoDeck;

/// <summary>
/// What the Audio Mod window asks for (speed, pitch, distortion, reverb, echo, low / high pass). The values are the numbers the user sees
/// in the window; <see cref="AudioModDsp"/> turns them into sound. <see cref="Clamp"/> keeps them in range, <see cref="Key"/> names the
/// effect they make (two settings with the same key sound the same), and an untouched copy is <see cref="IsNeutral"/>.
/// </summary>
internal sealed record AudioModSettings
{
    public int SpeedPercent { get; init; } = 100;   // 50..200: how fast the song plays; the pitch is not changed by it
    public int PitchSemitones { get; init; }        // -12..12
    public bool Distort { get; init; }
    public bool ReverbOn { get; init; }
    public int Reverb { get; init; } = 30;          // 0..100: how much reverb
    public bool DelayOn { get; init; }
    public int DelayTime { get; init; } = 4;        // 0..10: time between the echoes
    public int DelayIntensity { get; init; } = 2;   // 0..10: how many echoes and how loud
    public int LowPass { get; init; }               // 0..100: 0 = off, more = more muffled
    public int HighPass { get; init; }              // 0..100: 0 = off, more = thinner

    public const int MinSpeed = 50, MaxSpeed = 200;

    public AudioModSettings Clamp() => this with
    {
        SpeedPercent = Math.Clamp(SpeedPercent, MinSpeed, MaxSpeed),
        PitchSemitones = Math.Clamp(PitchSemitones, -12, 12),
        Reverb = Math.Clamp(Reverb, 0, 100),
        DelayTime = Math.Clamp(DelayTime, 0, 10),
        DelayIntensity = Math.Clamp(DelayIntensity, 0, 10),
        LowPass = Math.Clamp(LowPass, 0, 100),
        HighPass = Math.Clamp(HighPass, 0, 100),
    };

    /// <summary>True when nothing would change: the song would sound as it is.</summary>
    public bool IsNeutral
    {
        get
        {
            var c = Clamp();
            return c.SpeedPercent == 100 && c.PitchSemitones == 0 && !c.Distort && !(c.ReverbOn && c.Reverb > 0)
                   && !c.DelayOn && c.LowPass == 0 && c.HighPass == 0;
        }
    }

    /// <summary>A short text naming the effect (only the parts that are switched on). Part of the name of a rendered file.</summary>
    public string Key()
    {
        var c = Clamp();
        return $"v1s{c.SpeedPercent}p{c.PitchSemitones}d{(c.Distort ? 1 : 0)}r{(c.ReverbOn ? c.Reverb : 0)}"
               + $"e{(c.DelayOn ? c.DelayTime * 100 + c.DelayIntensity : -1)}l{c.LowPass}h{c.HighPass}";
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>The settings saved by <see cref="ToJson"/>; anything unreadable gives the untouched settings.</summary>
    public static AudioModSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AudioModSettings();
        try { return (JsonSerializer.Deserialize<AudioModSettings>(json) ?? new AudioModSettings()).Clamp(); }
        catch { return new AudioModSettings(); }
    }
}

/// <summary>A named starting point in the Audio Mod window, with the two colours of its card.</summary>
internal sealed record AudioModPreset(string Name, AudioModSettings Settings, string ColorFrom, string ColorTo);

internal static class AudioModPresets
{
    public static readonly AudioModPreset[] All =
    {
        new("Sped Up", new AudioModSettings { SpeedPercent = 120, PitchSemitones = 2 }, "#9C8A24", "#2B2610"),
        new("Nightcore", new AudioModSettings { SpeedPercent = 130, PitchSemitones = 4 }, "#7A3CFF", "#1B1140"),
        new("Slowed & Reverb", new AudioModSettings { SpeedPercent = 84, PitchSemitones = -2, ReverbOn = true, Reverb = 24, LowPass = 7 }, "#B9A24A", "#3F5560"),
        new("Lo-fi", new AudioModSettings { SpeedPercent = 95, PitchSemitones = -1, ReverbOn = true, Reverb = 12, LowPass = 55, HighPass = 25 }, "#C04A3A", "#2A1620"),
        new("Slowed", new AudioModSettings { SpeedPercent = 90, PitchSemitones = -1 }, "#5E5236", "#1E1A12"),
        new("Daycore", new AudioModSettings { SpeedPercent = 84, PitchSemitones = -2 }, "#D8345F", "#3A0F1E"),
        new("Muffled", new AudioModSettings { LowPass = 71 }, "#3C9C95", "#16313A"),
        new("Vaporwave", new AudioModSettings { SpeedPercent = 80, PitchSemitones = -3, ReverbOn = true, Reverb = 40, LowPass = 20 }, "#FF3C9A", "#321A78"),
        new("Echo", new AudioModSettings { DelayOn = true, DelayTime = 4, DelayIntensity = 4, ReverbOn = true, Reverb = 15 }, "#3C78D8", "#14264A"),
        new("Telephone", new AudioModSettings { HighPass = 62, LowPass = 62, Distort = true }, "#7C8590", "#262A30"),
    };

    /// <summary>The preset these settings are (the same effect), or null.</summary>
    public static AudioModPreset? Match(AudioModSettings settings) =>
        All.FirstOrDefault(p => p.Settings.Key() == settings.Key());
}
