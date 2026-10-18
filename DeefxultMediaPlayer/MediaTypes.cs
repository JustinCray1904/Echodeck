namespace DeefxultMediaPlayer;

public static class MediaTypes
{
    public static readonly string[] Extensions = { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v" };

    public static string DialogFilter =>
        "Video files|" + string.Join(';', Extensions.Select(e => "*" + e)) + "|All files|*.*";

    public static bool IsSupportedExtension(string ext) =>
        Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase);

    public static bool IsSupportedFile(string path) =>
        File.Exists(path) && IsSupportedExtension(Path.GetExtension(path));
}
