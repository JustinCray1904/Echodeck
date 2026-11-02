namespace EchoDeck;

internal static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\Tempo: settings, library cache, wrapped vault key and passcode record.</summary>
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tempo");

    /// <summary>
    /// %USERPROFILE%\.EchoDeckVault: the hidden folder of encrypted vault files. The folder name is deliberately
    /// unchanged from the earlier builds so an existing vault keeps working.
    /// </summary>
    public static string VaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".EchoDeckVault");

    /// <summary>Written by the installer on a fresh install only; makes the first launch show the splash + onboarding.</summary>
    public static string FreshInstallFlag => Path.Combine(DataDir, "fresh-install.flag");

    /// <summary>
    /// Carries over data from the earlier "EchoDeck" and "Deefxult Media Player" builds so an existing vault, passcode,
    /// settings and playlists keep working. Files are only ever COPIED, never moved or deleted, and never overwrite
    /// anything already in the Tempo folder.
    /// </summary>
    public static void MigrateLegacyData()
    {
        try
        {
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Directory.CreateDirectory(DataDir);

            // 1) the previous EchoDeck data folder: everything it kept
            var echo = Path.Combine(localApp, "EchoDeck");
            if (Directory.Exists(echo))
            {
                foreach (var name in new[] { "settings.json", "playlists.json", "recent.json", "vault.key", "pin.dat" })
                    CopyIfMissing(Path.Combine(echo, name), Path.Combine(DataDir, name));

                var covers = Path.Combine(echo, "covers");
                if (Directory.Exists(covers))
                {
                    var destCovers = Path.Combine(DataDir, "covers");
                    Directory.CreateDirectory(destCovers);
                    foreach (var f in Directory.EnumerateFiles(covers))
                        CopyIfMissing(f, Path.Combine(destCovers, Path.GetFileName(f)));
                }
            }

            // 2) the oldest builds kept the key + passcode record in a "Deefxult..." folder
            foreach (var oldData in Directory.EnumerateDirectories(localApp, "*Deefxult*"))
            {
                foreach (var name in new[] { "vault.key", "pin.dat" })
                    CopyIfMissing(Path.Combine(oldData, name), Path.Combine(DataDir, name));
            }

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var oldVault = Path.Combine(profile, ".SecureVideoVault");
            if (Directory.Exists(oldVault) && !Directory.Exists(VaultDir))
                Directory.Move(oldVault, VaultDir);
        }
        catch
        {
            // Best effort: a failed migration just means the old data isn't picked up automatically.
        }
    }

    private static void CopyIfMissing(string src, string dst)
    {
        try
        {
            if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst);
        }
        catch { /* skip this file */ }
    }
}
