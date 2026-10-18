using System.Windows;
using LibVLCSharp.Shared;
using System.IO;

namespace DeefxultMediaPlayer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (!TryInitializeLibVlc(out var error))
        {
            MessageBox.Show(error, "Deefxult Media Player", MessageBoxButton.OK, MessageBoxImage.Error);
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
            error = "Deefxult Media Player must run as a 64-bit (x64) process.";
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
                        "\n\nRebuild with the VideoLAN.LibVLC.Windows NuGet package restored, and always copy " +
                        "the whole output/publish folder (including the 'libvlc' subfolder) to the other machine.";
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
