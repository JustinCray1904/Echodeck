using System.Windows;
using LibVLCSharp.Shared;

namespace EchoDeck;

public partial class App : Application
{
    private int _crashDialogs;

    public App()
    {
        // Anything that goes wrong is written to %LOCALAPPDATA%\EchoDeck\crash.log and shown in a message box,
        // instead of Windows' anonymous "unknown software exception" dialog.
        DispatcherUnhandledException += (_, e) =>
        {
            ReportCrash("UI thread", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash("Fatal", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("Background task", e.Exception);
            e.SetObserved();
        };
    }

    private static string LogCrash(string where, Exception? ex)
    {
        var path = Path.Combine(AppPaths.DataDir, "crash.log");
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.AppendAllText(path, $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{where}] ====\r\n{ex}\r\n\r\n");
        }
        catch { /* logging must never throw */ }
        return path;
    }

    private void ReportCrash(string where, Exception? ex)
    {
        var log = LogCrash(where, ex);
        if (_crashDialogs++ >= 3) return; // don't bury the user in dialogs

        var root = ex;
        while (root?.InnerException != null) root = root.InnerException;
        var detail = (root ?? ex)?.ToString() ?? "Unknown error";
        if (detail.Length > 1400) detail = detail.Substring(0, 1400) + " ...";

        try
        {
            MessageBox.Show(
                "EchoDeck hit an error (" + where + "):\n\n" + detail + "\n\nThe full details were saved to:\n" + log,
                "EchoDeck", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* UI not available */ }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        AppPaths.MigrateLegacyData();
        AppSettings.Load();
        ThemeManager.Apply(AppSettings.Current.Theme, AppSettings.Current.AccentIndex);

        if (!TryInitializeLibVlc(out var error))
        {
            MessageBox.Show(error, "EchoDeck", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        base.OnStartup(e);
    }

    /// <summary>
    /// Locates the x64 LibVLC natives explicitly and reports a clear message if they're missing,
    /// instead of letting the app die with a DllNotFoundException.
    /// </summary>
    private static bool TryInitializeLibVlc(out string error)
    {
        error = string.Empty;

        if (!Environment.Is64BitProcess)
        {
            error = "EchoDeck must run as a 64-bit (x64) process.";
            return false;
        }

        try
        {
            var baseDir = AppContext.BaseDirectory;
            var nested = Path.Combine(baseDir, "libvlc", "win-x64");

            if (File.Exists(Path.Combine(nested, "libvlc.dll")))
            {
                Core.Initialize(nested);
            }
            else if (File.Exists(Path.Combine(baseDir, "libvlc.dll")))
            {
                Core.Initialize(baseDir);
            }
            else
            {
                error = "libvlc.dll was not found.\n\nExpected it here:\n  " + nested +
                        "\n\nReinstall EchoDeck, or when running the portable build keep the 'libvlc' folder " +
                        "next to EchoDeck.exe.";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = "LibVLC could not be initialised:\n\n" + ex.Message;
            return false;
        }
    }
}
