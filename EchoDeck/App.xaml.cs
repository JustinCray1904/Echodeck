using System.Windows;
using LibVLCSharp.Shared;

namespace EchoDeck;

public partial class App : Application
{
    private int _crashDialogs;

    /// <summary>The first-launch splash (null once it is gone, or when this isn't the first launch).</summary>
    internal static StartupSplashWindow? Splash { get; private set; }
    private static DateTime _splashShownAt;
    private const int SplashMinMs = 2600;

    public App()
    {
        // Anything that goes wrong is written to %LOCALAPPDATA%\Tempo\crash.log and shown in a message box,
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
        try { Splash?.Close(); Splash = null; } catch { /* already closed */ }
        if (_crashDialogs++ >= 3) return; // don't bury the user in dialogs

        var root = ex;
        while (root?.InnerException != null) root = root.InnerException;
        var detail = (root ?? ex)?.ToString() ?? "Unknown error";
        if (detail.Length > 1400) detail = detail.Substring(0, 1400) + " ...";

        try
        {
            MessageBox.Show(
                "Tempo hit an error (" + where + "):\n\n" + detail + "\n\nThe full details were saved to:\n" + log,
                "Tempo", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* UI not available */ }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        AppPaths.MigrateLegacyData();
        AppSettings.Load();
        ApplyFreshInstallFlag();
        ThemeManager.Apply(AppSettings.Current.Theme, AppSettings.Current.AccentIndex);

        if (!TryInitializeLibVlc(out var error))
        {
            MessageBox.Show(error, "Tempo", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // First launch ever (fresh install): logo splash first, then the onboarding tour once the player is ready.
        if (!AppSettings.Current.OnboardingDone)
        {
            try
            {
                Splash = new StartupSplashWindow();
                Splash.Show();
                _splashShownAt = DateTime.UtcNow;
            }
            catch { Splash = null; }
        }

        base.OnStartup(e);
    }

    /// <summary>
    /// The installer drops fresh-install.flag only on a brand-new install (never on an upgrade). settings.json survives
    /// an uninstall, so without this a reinstall would skip the first-run tour.
    /// </summary>
    private static void ApplyFreshInstallFlag()
    {
        try
        {
            if (!File.Exists(AppPaths.FreshInstallFlag)) return;
            AppSettings.Current.OnboardingDone = false;
            AppSettings.Current.Save(notify: false);
            File.Delete(AppPaths.FreshInstallFlag);
        }
        catch { /* never block start-up over this */ }
    }

    /// <summary>Keeps the splash up for a minimum time, fades it out and closes it. Safe to call when there is none.</summary>
    internal static async Task FinishSplashAsync()
    {
        var splash = Splash;
        if (splash == null) return;
        Splash = null;

        try
        {
            var left = TimeSpan.FromMilliseconds(SplashMinMs) - (DateTime.UtcNow - _splashShownAt);
            if (left > TimeSpan.Zero) await Task.Delay(left);
            await splash.FadeOutAsync();
        }
        catch { /* just close it */ }
        finally
        {
            try { splash.Close(); } catch { /* already closed */ }
        }
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
            error = "Tempo must run as a 64-bit (x64) process.";
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
                        "\n\nReinstall Tempo, or when running the portable build keep the 'libvlc' folder " +
                        "next to Tempo.exe.";
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
