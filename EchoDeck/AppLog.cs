namespace EchoDeck;

/// <summary>
/// Quiet log for problems Tempo handled itself (a blocked registry write, an unusable AI model ...), kept apart from
/// crash.log, which is only for real crashes. Writing here can never throw.
/// </summary>
internal static class AppLog
{
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(AppPaths.DataDir, "tempo.log");

    public static void Write(string where, Exception? ex = null, string? note = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                var path = FilePath;

                // keep the file small: start over once it passes ~256 KB
                try { if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path); } catch { /* ignore */ }

                File.AppendAllText(path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{where}] {note}{(ex != null ? " " + ex.GetType().Name + ": " + ex.Message : "")}\r\n");
            }
        }
        catch { /* logging must never throw */ }
    }
}
