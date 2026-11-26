namespace EchoDeck;

/// <summary>The channel modes of Settings > Audio and the bottom-bar popup (normal, mono, reverse stereo, left, right, Dolby surround).</summary>
internal static class StereoModes
{
    /// <summary>The names shown to the user, in the order of <see cref="AppSettings.StereoMode"/>.</summary>
    public static readonly string[] Names = { "Normal", "Mono", "Reverse stereo", "Left side only", "Right side only", "Dolby surround" };

    // LibVLC's "stereo-mode" values: unset (normal), mono, reverse stereo, left only, right only, Dolby surround.
    private static readonly int[] Codes = { 0, 7, 2, 3, 4, 5 };

    private static System.Reflection.MethodInfo? _setChannel;

    /// <summary>LibVLC's code for a saved setting (an unknown setting is the normal mode).</summary>
    public static int CodeFor(int setting) => Codes[Math.Clamp(setting, 0, Codes.Length - 1)];

    /// <summary>
    /// MediaPlayer.SetChannel (libvlc_audio_set_channel), looked up by name like the equaliser reset, so a different LibVLCSharp build
    /// can't break the build. Returns false until the song's audio output exists, or when the output refuses the mode.
    /// </summary>
    public static bool TrySet(LibVLCSharp.Shared.MediaPlayer player, int code)
    {
        try
        {
            _setChannel ??= typeof(LibVLCSharp.Shared.MediaPlayer).GetMethods()
                .FirstOrDefault(m => m.Name == "SetChannel" && m.GetParameters().Length == 1);
            if (_setChannel == null) return false;
            object argument = Enum.ToObject(_setChannel.GetParameters()[0].ParameterType, code);
            return _setChannel.Invoke(player, new[] { argument }) is true;
        }
        catch { return false; }
    }
}
