using System.Security.Cryptography;
using System.Text;
using System.IO;
using System;

namespace DeefxultMediaPlayer;

/// <summary>
/// Persists the user's PIN. Only a salted PBKDF2-SHA256 hash is stored (never the PIN itself), and the record is
/// additionally wrapped with Windows DPAPI for the current user, in %LOCALAPPDATA%\DeefxultMediaPlayer\pin.dat.
/// Until the user sets a PIN, the default (1234, or DEEFXULT_PIN) applies.
/// Note: a 4-digit PIN has only 10,000 possibilities, so the hash alone could be brute-forced; the DPAPI wrap is what
/// keeps the record unreadable to other accounts. The PIN is a UI gate; the vault's data protection is its AES key.
/// </summary>
internal static class PinStore
{
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Deefxult.Pin.v1");

    private static byte[]? _salt;
    private static byte[]? _hash;
    private static bool _loaded;

    private static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeefxultMediaPlayer");

    private static string FilePath => Path.Combine(DataDir, "pin.dat");
    private static string SkipMarkerPath => Path.Combine(DataDir, "pin-setup-skipped");

    /// <summary>True if the user dismissed the first-run PIN screen (so it isn't shown on every launch).</summary>
    public static bool SetupSkipped => File.Exists(SkipMarkerPath);

    public static void MarkSetupSkipped()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllBytes(SkipMarkerPath, Array.Empty<byte>());
        }
        catch { /* best effort */ }
    }

    /// <summary>True while no custom PIN has been saved.</summary>
    public static bool IsDefault
    {
        get { EnsureLoaded(); return _hash == null; }
    }

    public static bool Verify(string pin)
    {
        EnsureLoaded();
        if (_hash == null || _salt == null) return pin == AppConfig.Pin;
        return CryptographicOperations.FixedTimeEquals(Hash(pin, _salt), _hash);
    }

    public static void Set(string pin)
    {
        if (pin.Length != PinDetector.PinLength || !pin.All(c => c is >= '0' and <= '9'))
            throw new ArgumentException("The PIN must be exactly 4 digits.");

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Hash(pin, salt);

        var blob = new byte[SaltSize + HashSize];
        salt.CopyTo(blob, 0);
        hash.CopyTo(blob, SaltSize);

        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, ProtectedData.Protect(blob, Entropy, DataProtectionScope.CurrentUser));
        File.Move(tmp, path, overwrite: true);
        CryptographicOperations.ZeroMemory(blob);

        _salt = salt;
        _hash = hash;
        _loaded = true;
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            if (!File.Exists(FilePath)) return;
            var blob = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            if (blob.Length != SaltSize + HashSize) return;
            _salt = blob[..SaltSize];
            _hash = blob[SaltSize..];
        }
        catch
        {
            // Unreadable record (different account, damaged): fall back to the default PIN.
        }
    }

    private static byte[] Hash(string pin, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, HashSize);
}
