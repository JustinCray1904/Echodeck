using System.Security.Cryptography;
using System.Text;

namespace EchoDeck;

/// <summary>
/// Persists the Anonymous Mode passcode (4 to 8 digits). Only a salted PBKDF2-SHA256 hash is stored (never the
/// passcode itself) and the record is wrapped with Windows DPAPI for the current user, in
/// %LOCALAPPDATA%\EchoDeck\pin.dat. Record layout: [length byte][16 byte salt][32 byte hash]
/// (the older 48-byte layout without the length byte is read as a 4-digit passcode).
/// There is no default passcode: until one is set, the Ctrl+passcode gesture is disabled.
/// The passcode is a UI gate; the vault's data protection is its AES key (see KeyStore).
/// </summary>
internal static class PinStore
{
    public const int MinLength = 4;
    public const int MaxLength = 8;

    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Deefxult.Pin.v1"); // unchanged so migrated records still open

    private static byte[]? _salt;
    private static byte[]? _hash;
    private static int _length = MinLength;
    private static bool _loaded;

    private static string FilePath => Path.Combine(AppPaths.DataDir, "pin.dat");

    /// <summary>True once the user has chosen a passcode.</summary>
    public static bool IsSet
    {
        get { EnsureLoaded(); return _hash != null; }
    }

    /// <summary>Number of digits in the saved passcode (4 while none is set).</summary>
    public static int Length
    {
        get { EnsureLoaded(); return _length; }
    }

    public static bool Verify(string pin)
    {
        EnsureLoaded();
        if (_hash == null || _salt == null || pin.Length != _length) return false;
        return CryptographicOperations.FixedTimeEquals(Hash(pin, _salt), _hash);
    }

    public static bool IsValidFormat(string pin) =>
        pin.Length is >= MinLength and <= MaxLength && pin.All(c => c is >= '0' and <= '9');

    public static void Set(string pin)
    {
        if (!IsValidFormat(pin))
            throw new ArgumentException($"The passcode must be {MinLength} to {MaxLength} digits.");

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Hash(pin, salt);

        var blob = new byte[1 + SaltSize + HashSize];
        blob[0] = (byte)pin.Length;
        salt.CopyTo(blob, 1);
        hash.CopyTo(blob, 1 + SaltSize);

        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, ProtectedData.Protect(blob, Entropy, DataProtectionScope.CurrentUser));
        File.Move(tmp, path, overwrite: true);
        CryptographicOperations.ZeroMemory(blob);

        _salt = salt;
        _hash = hash;
        _length = pin.Length;
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

            if (blob.Length == 1 + SaltSize + HashSize)
            {
                int len = blob[0];
                if (len is < MinLength or > MaxLength) return;
                _length = len;
                _salt = blob[1..(1 + SaltSize)];
                _hash = blob[(1 + SaltSize)..];
            }
            else if (blob.Length == SaltSize + HashSize) // legacy 4-digit record
            {
                _length = 4;
                _salt = blob[..SaltSize];
                _hash = blob[SaltSize..];
            }
        }
        catch
        {
            // Unreadable record (different account, damaged): treat as "no passcode set".
        }
    }

    private static byte[] Hash(string pin, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, HashSize);
}
