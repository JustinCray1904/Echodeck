namespace EchoDeck;

/// <summary>Playback times as text.</summary>
internal static class TimeText
{
    /// <summary>Milliseconds as "hh:mm:ss" (hours keep counting past 99; a negative time is 00:00:00).</summary>
    public static string Clock(long milliseconds)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
    }
}
