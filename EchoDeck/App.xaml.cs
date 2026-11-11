using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
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

    // ------------------------------------------------------------------ one Tempo at a time

    private static Mutex? _instanceMutex;                         // held for as long as this Tempo runs
    private static readonly List<string> PendingOpens = new();    // files that arrived before the window was ready

    /// <summary>A file (or "" = just come to the front) was handed over by another Tempo that was started from Explorer.</summary>
    internal static event Action<string>? ExternalOpenRequested;

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);

    private static string UserTag
    {
        get
        {
            var tag = new StringBuilder();
            foreach (var c in Environment.UserName) tag.Append(char.IsLetterOrDigit(c) ? c : '_');
            return tag.ToString();
        }
    }

    private static string MutexName => @"Local\Tempo.SingleInstance." + UserTag;
    private static string PipeName => "Tempo.Open." + UserTag;

    /// <summary>
    /// True when this is the only Tempo (it keeps running). False when another one is already open: the files from the command
    /// line were handed to it, so opening a file from Explorer plays it in the window that is already open, not in a new one.
    /// </summary>
    private static bool BecomeTheOnlyInstance(string[] args)
    {
        try
        {
            _instanceMutex = new Mutex(true, MutexName, out bool first);
            if (first)
            {
                StartListening();
                return true;
            }
        }
        catch
        {
            return true;   // couldn't tell: just run normally
        }

        var files = new List<string>();
        foreach (var arg in args)
        {
            try
            {
                if (MediaTypes.IsSupportedFile(arg)) files.Add(Path.GetFullPath(arg));
            }
            catch { /* a path that can't be resolved is ignored */ }
        }

        try
        {
            AllowSetForegroundWindow(-1);   // lets the running Tempo come to the front over Explorer
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            if (files.Count == 0) writer.WriteLine("SHOW");
            foreach (var file in files) writer.WriteLine("OPEN\t" + file);
            writer.Flush();
            return false;
        }
        catch
        {
            return true;   // the other Tempo is not answering (starting up or stuck): run on our own rather than do nothing
        }
    }

    private static void StartListening()
    {
        var listener = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server, new UTF8Encoding(false));
                    string? line;
                    while ((line = reader.ReadLine()) != null) HandleHandover(line);
                }
                catch (IOException) { /* the other side went away: wait for the next one */ }
                catch (ObjectDisposedException) { return; }
                catch (Exception ex)
                {
                    LogCrash("Single instance", ex);
                    Thread.Sleep(500);
                }
            }
        })
        { IsBackground = true, Name = "Tempo single-instance listener" };
        listener.Start();
    }

    private static void HandleHandover(string line)
    {
        string path;
        if (line == "SHOW") path = "";
        else if (line.StartsWith("OPEN\t", StringComparison.Ordinal)) path = line.Substring(5);
        else return;

        if (path.Length > 0 && !MediaTypes.IsSupportedFile(path)) return;

        Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ExternalOpenRequested != null) ExternalOpenRequested.Invoke(path);
            else if (path.Length > 0) PendingOpens.Add(path);
        }));
    }

    /// <summary>Files that were handed over before the main window was ready (taken once).</summary>
    internal static List<string> TakePendingOpens()
    {
        var list = new List<string>(PendingOpens);
        PendingOpens.Clear();
        return list;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!BecomeTheOnlyInstance(e.Args))
        {
            Shutdown(0);   // the running Tempo has the files now
            return;
        }

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
