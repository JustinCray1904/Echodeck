using System.Security.Cryptography;

namespace EchoDeck;

public sealed record VaultItem(string Name, string FullPath, string SizeText, string? Origin = null)
{
    public bool IsAudio => MediaTypes.IsAudioExtension(Path.GetExtension(Name));
    public string Kind => IsAudio ? "Audio" : "Video";
    public string Subtitle => Kind + "  ·  " + SizeText;
    public System.Windows.Media.Geometry Icon => IconSet.Get(IsAudio ? "IcoMusic" : "IcoVideo");
}

/// <summary>
/// Owns the on-disk vault (%USERPROFILE%\.EchoDeckVault): AES-256-GCM encrypted files with random names,
/// the in-memory master key (loaded on Unlock, zeroed on Lock), and secure deletion.
/// </summary>
public sealed class VaultService
{
    private readonly object _keyLock = new();
    private byte[]? _masterKey;

    public string VaultPath { get; }

    public VaultService()
    {
        VaultPath = AppPaths.VaultDir;
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

    /// <summary>
    /// Used when encrypted files exist but their key is gone (so they can never be read again): moves them into an
    /// "unreadable" sub-folder so a fresh vault can start. Returns that folder.
    /// </summary>
    public string ResetUnreadable()
    {
        var dest = Path.Combine(VaultPath, "unreadable");
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.EnumerateFiles(VaultPath)
                     .Where(f => f.EndsWith(VaultFormat.Extension, StringComparison.OrdinalIgnoreCase)).ToList())
            File.Move(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);
        return dest;
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
                    items.Add(new VaultItem(meta.Name, path, FormatSize(meta.Length), meta.Origin));
                }
                catch { /* unreadable or foreign file: skip */ }
            }
            items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return items;
        }
        finally { Zero(key); }
    });

    /// <summary>
    /// Encrypts a media file into the vault under a random name. The original is left untouched unless
    /// <paramref name="deleteOriginal"/> is set, in which case it is securely deleted after the encrypted copy
    /// has been re-opened and verified.
    /// </summary>
    public Task ImportAsync(string sourcePath, bool deleteOriginal = false) => Task.Run(() =>
    {
        EnsureDirectory();
        var key = KeyCopy();
        try
        {
            var dest = NewVaultFilePath();
            VaultFormat.EncryptFile(sourcePath, dest, key, Path.GetFileName(sourcePath), Path.GetDirectoryName(sourcePath));

            if (!deleteOriginal) return;

            try
            {
                using var verify = new EncryptedVaultStream(dest, key);
                if (verify.Length != new FileInfo(sourcePath).Length)
                    throw new InvalidDataException("Verification failed.");
            }
            catch
            {
                try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                throw;
            }

            SecureDeleteCore(sourcePath);
        }
        finally { Zero(key); }
    });

    /// <summary>
    /// Decrypts an item back out of the vault into <paramref name="destFolder"/> (never overwriting an existing file) and
    /// verifies the result. Returns the path it was written to. The encrypted copy is left alone; delete it afterwards.
    /// </summary>
    public Task<string> RestoreAsync(VaultItem item, string destFolder) => Task.Run(async () =>
    {
        Directory.CreateDirectory(destFolder);
        var target = UniquePath(destFolder, item.Name);
        var key = KeyCopy();
        try
        {
            long expected;
            using (var source = new EncryptedVaultStream(item.FullPath, key))
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
            {
                expected = source.Length;
                await source.CopyToAsync(output);
            }

            if (new FileInfo(target).Length != expected) throw new InvalidDataException("The restored file is incomplete.");
            return target;
        }
        catch
        {
            try { if (File.Exists(target)) File.Delete(target); } catch { /* leave nothing half-written behind */ }
            throw;
        }
        finally { Zero(key); }
    });

    private static string UniquePath(string folder, string name)
    {
        name = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        var candidate = Path.Combine(folder, name);
        if (!File.Exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 1; i < 1000; i++)
        {
            candidate = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(folder, stem + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ext);
    }

    /// <summary>Total size of the encrypted files on disk, in bytes.</summary>
    public long TotalBytes()
    {
        try
        {
            return new DirectoryInfo(VaultPath).EnumerateFiles("*" + VaultFormat.Extension).Sum(f => f.Length);
        }
        catch { return 0; }
    }

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

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{Math.Max(1, bytes / 1024)} KB"
    };
}
