namespace EchoDeck;

public static class MediaTypes
{
    public static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v" };
    public static readonly string[] AudioExtensions = { ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".wma" };

    public static IEnumerable<string> Extensions => VideoExtensions.Concat(AudioExtensions);

    private static string Pattern(IEnumerable<string> exts) => string.Join(';', exts.Select(e => "*" + e));

    public static string DialogFilter =>
        "Media files|" + Pattern(Extensions) +
        "|Video files|" + Pattern(VideoExtensions) +
        "|Audio files|" + Pattern(AudioExtensions) +
        "|All files|*.*";

    public static bool IsSupportedExtension(string? ext) =>
        !string.IsNullOrEmpty(ext) && Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase);

    public static bool IsAudioExtension(string? ext) =>
        !string.IsNullOrEmpty(ext) && AudioExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);

    public static bool IsSupportedFile(string path) =>
        File.Exists(path) && IsSupportedExtension(Path.GetExtension(path));

    public static bool IsAudio(string path) => IsAudioExtension(Path.GetExtension(path));
}
