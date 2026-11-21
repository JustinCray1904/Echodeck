namespace EchoDeck;

/// <summary>Which LibVLC audio filters the sound settings ask for. They can only be chosen when LibVLC starts (--audio-filter=...).</summary>
internal static class SoundFilters
{
    /// <summary>
    /// The filter chain in LibVLC's syntax (filters separated by ':'), or "" for none.
    /// <paramref name="soundSpace"/>: 0 off, 1 wide stereo, 2 room (3D), 3 wide + room. Unknown values mean off.
    /// </summary>
    public static string Chain(bool normalizeVolume, int soundSpace)
    {
        var filters = new List<string>();
        if (normalizeVolume) filters.Add("normvol");
        if (soundSpace is 1 or 3) filters.Add("stereo_widen");
        if (soundSpace is 2 or 3) filters.Add("spatializer");
        return string.Join(":", filters);
    }
}
