namespace EchoDeck;

internal static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\EchoDeck: settings, wrapped vault key and passcode record.</summary>
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EchoDeck");

    /// <summary>%USERPROFILE%\.EchoDeckVault: the hidden folder of encrypted vault files.</summary>
    public static string VaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".EchoDeckVault");

    /// <summary>Carries over data from the earlier "Deefxult Media Player" build so an existing vault keeps working.</summary>
    public static void MigrateLegacyData()
    {
        try
        {
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            // The earlier builds kept the key + passcode record in a "Deefxult..." folder: pick it up wherever it is.
            foreach (var oldData in Directory.EnumerateDirectories(localApp, "*Deefxult*"))
            {
                Directory.CreateDirectory(DataDir);
                foreach (var name in new[] { "vault.key", "pin.dat" })
                {
                    var src = Path.Combine(oldData, name);
                    var dst = Path.Combine(DataDir, name);
                    if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst);
                }
            }

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var oldVault = Path.Combine(profile, ".SecureVideoVault");
            if (Directory.Exists(oldVault) && !Directory.Exists(VaultDir))
                Directory.Move(oldVault, VaultDir);
        }
        catch
        {
            // Best effort: a failed migration just means the old vault isn't picked up automatically.
        }
    }
}
