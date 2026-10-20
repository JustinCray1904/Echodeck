using System.Windows;
using LibVLCSharp.Shared;

namespace EchoDeck;

public partial class App : Application
{
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
