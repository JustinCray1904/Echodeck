using System.Security.Cryptography;
using System.Text;

namespace EchoDeck;

/// <summary>
/// Holds the vault's 256-bit master key, wrapped with Windows DPAPI (current user) and stored OUTSIDE the
/// vault folder, in %LOCALAPPDATA%\EchoDeck\vault.key. Only this Windows account on this
/// machine can unwrap it, so copying the vault folder (or the disk) elsewhere yields nothing readable.
/// </summary>
internal static class KeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Deefxult.SecureVideoVault.v1"); // unchanged so migrated keys still open

    private static string KeyPath => Path.Combine(AppPaths.DataDir, "vault.key");

    /// <param name="createIfMissing">
    /// False when encrypted files already exist: a missing key then means they can't be decrypted,
    /// and silently minting a new key would make that permanent.
    /// </param>
    public static byte[] LoadOrCreate(bool createIfMissing)
    {
        var path = KeyPath;

        if (File.Exists(path))
        {
            var blob = File.ReadAllBytes(path);
            var key = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
            if (key.Length != 32) throw new CryptographicException("The vault key file is damaged.");
            return key;
        }

        if (!createIfMissing)
            throw new FileNotFoundException(
                "The vault key is missing (" + path + "), so the existing vault files can't be decrypted.");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var fresh = RandomNumberGenerator.GetBytes(32);

        // Write to a temp file and rename, so a crash or power loss mid-write can never leave a truncated key
        // (which would make every vault file permanently undecryptable).
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, ProtectedData.Protect(fresh, Entropy, DataProtectionScope.CurrentUser));
        File.Move(tmp, path, overwrite: true);
        return fresh;
    }
}
