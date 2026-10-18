using System.Security.Cryptography;
using System.IO;
using System;

namespace DeefxultMediaPlayer;

public sealed record VaultItem(string Name, string FullPath, string SizeText);

/// <summary>
/// Owns the on-disk vault (%USERPROFILE%\.SecureVideoVault): AES-256-GCM encrypted files with random names,
/// the in-memory master key (loaded on Unlock, zeroed on Lock), and secure deletion.
/// </summary>
public sealed class VaultService
{
    private readonly object _keyLock = new();
    private byte[]? _masterKey;

    public string VaultPath { get; }

    public VaultService()
    {
        VaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".SecureVideoVault");
        EnsureDirectory();
    }

    // ------------------------------------------------------------------ key lifetime

    /// <summary>Loads the master key into memory (called when the vault opens).</summary>
    public void Unlock()
    {
        lock (_keyLock)
        {
            if (_masterKey != null) return;
            bool hasFiles = HasEncryptedFiles();
            _masterKey = KeyStore.LoadOrCreate(createIfMissing: !hasFiles);
        }
    }

    /// <summary>Zeroes the in-memory master key (called when the vault closes).</summary>
    public void Lock()
    {
        lock (_keyLock)
        {
            if (_masterKey == null) return;
            CryptographicOperations.ZeroMemory(_masterKey);
            _masterKey = null;
        }
    }

    private byte[] KeyCopy()
    {
        lock (_keyLock)
        {
            if (_masterKey == null) throw new InvalidOperationException("The vault is locked.");
            return (byte[])_masterKey.Clone();
        }
    }

    private static void Zero(byte[] key) => CryptographicOperations.ZeroMemory(key);

    // ------------------------------------------------------------------ files

    private void EnsureDirectory()
    {
        var dir = Directory.CreateDirectory(VaultPath);
        try
        {
            dir.Refresh();
            if ((dir.Attributes & FileAttributes.Hidden) == 0)
                dir.Attributes |= FileAttributes.Hidden; // hidden from normal Explorer views
        }
        catch { /* best effort */ }
    }

    private bool HasEncryptedFiles() =>
        Directory.EnumerateFiles(VaultPath).Any(f => f.EndsWith(VaultFormat.Extension, StringComparison.OrdinalIgnoreCase));

    private string NewVaultFilePath() =>
        Path.Combine(VaultPath, Guid.NewGuid().ToString("N") + VaultFormat.Extension);

    private void EnsureInsideVault(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(VaultPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("That path is outside the vault.");
    }

    public Task<IReadOnlyList<VaultItem>> ListAsync() => Task.Run<IReadOnlyList<VaultItem>>(() =>
    {
        EnsureDirectory();
        var key = KeyCopy();
        try
        {
            var items = new List<VaultItem>();
            foreach (var path in Directory.EnumerateFiles(VaultPath))
            {
                if (!path.EndsWith(VaultFormat.Extension, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var meta = VaultFormat.ReadMeta(path, key);
                    items.Add(new VaultItem(meta.Name, path, FormatSize(meta.Length)));
                }
                catch { /* unreadable or foreign file: skip */ }
            }
            items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return items;
        }
        finally { Zero(key); }
    });

    /// <summary>Encrypts a video into the vault under a random name. The original file is left untouched.</summary>
    public Task ImportAsync(string sourcePath) => Task.Run(() =>
    {
        EnsureDirectory();
        var key = KeyCopy();
        try { VaultFormat.EncryptFile(sourcePath, NewVaultFilePath(), key, Path.GetFileName(sourcePath)); }
        finally { Zero(key); }
    });

    /// <summary>Opens a decrypting stream. The caller must dispose it to release the file handle.</summary>
    public EncryptedVaultStream OpenRead(string path)
    {
        EnsureInsideVault(path);
        var key = KeyCopy();
        try { return new EncryptedVaultStream(path, key); }
        finally { Zero(key); }
    }

    /// <summary>
    /// Encrypts any plaintext videos left in the folder by the earlier (unencrypted) version, verifies the
    /// result, then securely deletes the originals. Returns how many files were converted.
    /// </summary>
    public Task<int> MigrateLegacyAsync() => Task.Run(() =>
    {
        var legacy = new DirectoryInfo(VaultPath).EnumerateFiles()
            .Where(f => MediaTypes.IsSupportedExtension(f.Extension))
            .ToList();
        if (legacy.Count == 0) return 0;

        var key = KeyCopy();
        int converted = 0;
        try
        {
            foreach (var file in legacy)
            {
                var dest = NewVaultFilePath();
                try
                {
                    VaultFormat.EncryptFile(file.FullName, dest, key, file.Name);
                    using var verify = new EncryptedVaultStream(dest, key);
                    if (verify.Length != file.Length) throw new InvalidDataException("Verification failed.");
                }
                catch
                {
                    try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                    continue; // keep the original untouched
                }

                try { SecureDeleteCore(file.FullName); converted++; }
                catch { /* encrypted copy exists; original will be retried on a later run */ }
            }
        }
        finally { Zero(key); }
        return converted;
    });

    // ------------------------------------------------------------------ deletion

    /// <summary>
    /// Permanently deletes a vault file: overwrites its contents with random bytes, then removes it
    /// (it never goes to the Recycle Bin). Callers must release any open handle first (stop playback).
    /// </summary>
    public Task SecureDeleteAsync(string path) => Task.Run(() =>
    {
        EnsureInsideVault(path);
        SecureDeleteCore(path);
    });

    private static void SecureDeleteCore(string path)
    {
        Exception? last = null;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.SetAttributes(path, FileAttributes.Normal);

                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    var buffer = new byte[1 << 20];
                    long remaining = fs.Length;
                    while (remaining > 0)
                    {
                        RandomNumberGenerator.Fill(buffer);
                        int n = (int)Math.Min(buffer.Length, remaining);
                        fs.Write(buffer, 0, n);
                        remaining -= n;
                    }
                    fs.Flush(true);
                }

                File.Delete(path);
                return;
            }
            catch (IOException ex) { last = ex; Thread.Sleep(250); }              // e.g. a handle is still open
            catch (UnauthorizedAccessException ex) { last = ex; Thread.Sleep(250); }
        }

        throw new IOException("The file is still in use or couldn't be removed: " + last?.Message, last);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{Math.Max(1, bytes / 1024)} KB"
    };
}
