using System.Globalization;
using System.Text;

namespace EchoDeck;

/// <summary>How serious a logged event is.</summary>
internal enum AppLogLevel { Info, Warning, Error }

/// <summary>
/// Quiet log for problems Tempo handled itself (a blocked registry write, an unusable AI model ...), kept apart from
/// crash.log, which is only for real crashes. Writing here can never throw. Two forms share the file tempo.log:
/// <see cref="Write"/> is a free-text line ("[where] note Type: message"), <see cref="Event"/> is a structured line with a level,
/// an event name and key=value fields (easy to search: <c>findstr "WARN" tempo.log</c>).
/// </summary>
internal static class AppLog
{
    private static readonly object Gate = new();

    /// <summary>Where the log goes instead of the data folder (tests only).</summary>
    internal static string? DirectoryOverride { get; set; }

    private static string Folder => DirectoryOverride ?? AppPaths.DataDir;

    public static string FilePath => Path.Combine(Folder, "tempo.log");

    /// <summary>
    /// Records a problem Tempo handled itself. <paramref name="includeMessage"/> false keeps only the kind of exception: for anything that
    /// touches the vault, whose messages can contain file names and paths that must not be written down (see <see cref="Failure"/>).
    /// </summary>
    public static void Write(string where, Exception? ex = null, string? note = null, bool includeMessage = true)
    {
        Append(FormatLine(DateTime.Now, where, ex, note, includeMessage));
    }

    /// <summary>
    /// Like <see cref="Write"/>, but writes only the type of the exception, never its message. For the vault: an exception message can
    /// carry a file name or path, and Anonymous Mode must leave nothing like that behind.
    /// </summary>
    public static void Failure(string where, Exception ex, string? note = null) => Write(where, ex, note, includeMessage: false);

    /// <summary>
    /// A structured line: time, level, event name and key=value fields, e.g.
    /// <c>2026-11-24 10:15:02 WARN settings.save-failed exception=IOException message="The disk is full."</c>.
    /// Never put a file name or path of the vault, or anything typed in Anonymous Mode, into a field.
    /// </summary>
    public static void Event(AppLogLevel level, string name, params (string Key, object? Value)[] fields)
    {
        Append(FormatEvent(DateTime.Now, level, name, fields));
    }

    // ------------------------------------------------------------------ formatting (plain functions, tested)

    internal static string FormatLine(DateTime time, string where, Exception? ex, string? note, bool includeMessage)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrEmpty(note)) parts.Add(note);
        if (ex != null) parts.Add(ex.GetType().Name + (includeMessage ? ": " + ex.Message : ""));
        return time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + $" [{where}]" + (parts.Count > 0 ? " " + string.Join(" ", parts) : "");
    }

    internal static string FormatEvent(DateTime time, AppLogLevel level, string name, IEnumerable<(string Key, object? Value)> fields)
    {
        var line = new StringBuilder();
        line.Append(time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(' ');
        line.Append(level switch { AppLogLevel.Error => "ERROR", AppLogLevel.Warning => "WARN", _ => "INFO" });
        line.Append(' ').Append(name);
        foreach (var (key, value) in fields) line.Append(' ').Append(key).Append('=').Append(FormatValue(value));
        return line.ToString();
    }

    /// <summary>A field value on one line: plain when it is a simple word or number, otherwise quoted.</summary>
    internal static string FormatValue(object? value)
    {
        string text = value switch
        {
            null => "-",
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "-"
        };

        bool plain = text.Length > 0 && text.All(c => !char.IsWhiteSpace(c) && c != '"' && c != '=');
        if (plain) return text;
        return "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
    }

    private static void Append(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var path = FilePath;

                // keep the file small: start over once it passes ~256 KB
                try { if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path); } catch { /* ignore */ }

                File.AppendAllText(path, line + "\r\n");
            }
        }
        catch { /* logging must never throw */ }
    }
}
