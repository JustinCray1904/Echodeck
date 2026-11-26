using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace EchoDeck;

internal static class FileAssociations
{
    private static readonly string[] VideoExt = { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".flv", ".mpg", ".mpeg", ".ts", ".m2ts", ".3gp" };
    private static readonly string[] AudioExt = { ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".wma" };

    private const string VideoProgId = "Tempo.Video";
    private const string AudioProgId = "Tempo.Audio";
    private const string BackupKey = @"Software\Tempo\Associations";

    private static string ExePath => Environment.ProcessPath ?? "";

    /// <summary>True when this build of Tempo is registered as the handler for the common formats.</summary>
    public static bool IsRegistered()
    {
        try
        {
            using var cmd = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + VideoProgId + @"\shell\open\command");
            var value = cmd?.GetValue(null) as string;
            if (string.IsNullOrEmpty(value) || ExePath.Length == 0) return false;
            if (value.IndexOf(ExePath, StringComparison.OrdinalIgnoreCase) < 0) return false;

            using var ext = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.mp4");
            return string.Equals(ext?.GetValue(null) as string, VideoProgId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>What happened when registering / unregistering. A blocked registry (policy, antivirus) is reported, never thrown.</summary>
    public readonly record struct Outcome(bool Succeeded, int Failures, string? FirstProblem);

    /// <summary>
    /// Runs one registry step. A failure (access denied by a domain policy or security software, a locked key ...) is logged
    /// and counted; the remaining steps still run and nothing propagates to the caller.
    /// </summary>
    private static bool Step(string what, Action action, ref int failures, ref string? first)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            failures++;
            first ??= ex.Message;
            AppLog.Write("File associations", ex, what);
            return false;
        }
    }

    public static Outcome Register()
    {
        int failures = 0;
        string? first = null;

        var exe = ExePath;
        if (exe.Length == 0)
        {
            AppLog.Write("File associations", null, "Tempo can't find its own program file");
            return new Outcome(false, 1, "Tempo can't find its own program file.");
        }

        Step("video ProgID", () => WriteProgId(VideoProgId, "Tempo video", exe), ref failures, ref first);
        Step("audio ProgID", () => WriteProgId(AudioProgId, "Tempo audio", exe), ref failures, ref first);

        Step("application key", () =>
        {
            using var app = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\Tempo.exe");
            app?.SetValue("FriendlyAppName", "Tempo");
            using var types = app?.CreateSubKey("SupportedTypes");
            foreach (var ext in VideoExt.Concat(AudioExt)) types?.SetValue(ext, "");
            using var cmd = app?.CreateSubKey(@"shell\open\command");
            cmd?.SetValue(null, "\"" + exe + "\" \"%1\"");
        }, ref failures, ref first);

        // Capabilities: makes Tempo appear in Windows' "Default apps" list
        Step("capabilities", () =>
        {
            using (var cap = Registry.CurrentUser.CreateSubKey(@"Software\Tempo\Capabilities"))
            {
                cap?.SetValue("ApplicationName", "Tempo");
                cap?.SetValue("ApplicationDescription", "Video and music player");
                using var assoc = cap?.CreateSubKey("FileAssociations");
                foreach (var ext in VideoExt) assoc?.SetValue(ext, VideoProgId);
                foreach (var ext in AudioExt) assoc?.SetValue(ext, AudioProgId);
            }
            using var reg = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications");
            reg?.SetValue("Tempo", @"Software\Tempo\Capabilities");
        }, ref failures, ref first);

        RegistryKey? backup = null;
        try { backup = Registry.CurrentUser.CreateSubKey(BackupKey); }
        catch (Exception ex) { failures++; first ??= ex.Message; AppLog.Write("File associations", ex, "backup key"); }

        try
        {
            foreach (var (ext, progId) in VideoExt.Select(e => (e, VideoProgId)).Concat(AudioExt.Select(e => (e, AudioProgId))))
            {
                // one extension failing (a protected key) must not stop the others
                Step(ext, () =>
                {
                    using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ext);
                    if (key == null) return;

                    // remember what the extension pointed to, so turning this off puts it back
                    var previous = key.GetValue(null) as string ?? "";
                    if (!string.Equals(previous, progId, StringComparison.OrdinalIgnoreCase) && backup?.GetValue(ext) == null)
                        backup?.SetValue(ext, previous);

                    using (var with = key.CreateSubKey("OpenWithProgids"))
                        with?.SetValue(progId, new byte[0], RegistryValueKind.None);
                    key.SetValue(null, progId);
                }, ref failures, ref first);
            }
        }
        finally
        {
            backup?.Dispose();
        }

        NotifyShell();
        return new Outcome(failures == 0, failures, first);
    }

    public static Outcome Unregister()
    {
        int failures = 0;
        string? first = null;

        RegistryKey? backup = null;
        try { backup = Registry.CurrentUser.OpenSubKey(BackupKey); } catch (Exception ex) { AppLog.Write("File associations", ex, "backup key"); }

        try
        {
            foreach (var (ext, progId) in VideoExt.Select(e => (e, VideoProgId)).Concat(AudioExt.Select(e => (e, AudioProgId))))
            {
                Step(ext, () =>
                {
                    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ext, writable: true);
                    if (key == null) return;

                    using (var with = key.OpenSubKey("OpenWithProgids", writable: true))
                        with?.DeleteValue(progId, throwOnMissingValue: false);

                    if (string.Equals(key.GetValue(null) as string, progId, StringComparison.OrdinalIgnoreCase))
                    {
                        var previous = backup?.GetValue(ext) as string ?? "";
                        if (previous.Length > 0) key.SetValue(null, previous);
                        else key.DeleteValue("", throwOnMissingValue: false);
                    }
                }, ref failures, ref first);
            }
        }
        finally
        {
            backup?.Dispose();
        }

        foreach (var sub in new[]
        {
            @"Software\Classes\" + VideoProgId, @"Software\Classes\" + AudioProgId,
            @"Software\Classes\Applications\Tempo.exe", @"Software\Tempo\Capabilities", BackupKey
        })
        {
            var subKey = sub;
            Step(subKey, () => Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false), ref failures, ref first);
        }
        Step("registered applications", () =>
        {
            using var reg = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true);
            reg?.DeleteValue("Tempo", throwOnMissingValue: false);
        }, ref failures, ref first);

        NotifyShell();
        return new Outcome(failures == 0, failures, first);
    }

    private static void NotifyShell()
    {
        try { NativeShell.SHChangeNotify(NativeShell.SHCNE_ASSOCCHANGED, NativeShell.SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); }
        catch (Exception ex) { AppLog.Write("File associations", ex, "shell notification"); }
    }

    private static void WriteProgId(string progId, string description, string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + progId);
        key?.SetValue(null, description);
        using (var icon = key?.CreateSubKey("DefaultIcon")) icon?.SetValue(null, "\"" + exe + "\",0");
        using (var cmd = key?.CreateSubKey(@"shell\open\command")) cmd?.SetValue(null, "\"" + exe + "\" \"%1\"");
    }
}
