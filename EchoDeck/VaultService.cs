using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace EchoDeck;

public sealed record VaultItem(string Name, string FullPath, string SizeText, string? Origin = null,
    string Title = "", string Artist = "", string Album = "", string Genre = "", long DurationMs = 0)
{
    public bool IsAudio => MediaTypes.IsAudioExtension(Path.GetExtension(Name));
    public string Kind => IsAudio ? "Audio" : "Video";
    public string Subtitle => Kind + "  ·  " + SizeText;
    public System.Windows.Media.Geometry Icon => IconSet.Get(IsAudio ? "IcoMusic" : "IcoVideo");
}

internal sealed class VaultDbPlaylist
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Items { get; set; } = new();   // vault file names (not full paths)
}

internal sealed class VaultDbData
{
    public List<VaultDbPlaylist> Playlists { get; set; } = new();
}

/// <summary>
/// Owns the on-disk vault (%USERPROFILE%\.EchoDeckVault): AES-256-GCM encrypted files with random names,
/// the in-memory master key (loaded on Unlock, zeroed on Lock), and secure deletion.
/// </summary>
public sealed class VaultService
{
    private readonly object _keyLock = new();
    private readonly object _dbLock = new();
    private byte[]? _masterKey;
    private List<Playlist>? _playlists;

    private const string DbName = "vault.db";
    private const string ThumbExtension = ".svt";
    private static readonly byte[] DbMagic = { (byte)'S', (byte)'V', (byte)'D', 1 };
    private static readonly byte[] DbInfo = Encoding.UTF8.GetBytes("svv-db-v1");
    private static readonly byte[] ThumbInfo = Encoding.UTF8.GetBytes("svv-thumb-v1");

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
            LoadPlaylists();
        }
    }

    // ------------------------------------------------------------------ encrypted playlists (vault.db)

    /// <summary>
    /// The vault's playlists. Items are full paths of vault files while the app runs (so every playlist screen works the
    /// same as in normal mode); on disk they are stored by file name inside an AES-GCM encrypted file.
    /// </summary>
    public List<Playlist> Playlists
    {
        get
        {
            lock (_dbLock) return _playlists ??= new List<Playlist>();
        }
    }

    private string DbPath => Path.Combine(VaultPath, DbName);

    private byte[] DbKey(byte[] master) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, Array.Empty<byte>(), DbInfo);

    private void LoadPlaylists()   // called inside _keyLock with the key loaded
    {
        var list = new List<Playlist>();
        try
        {
            if (File.Exists(DbPath))
            {
                var raw = File.ReadAllBytes(DbPath);
                const int header = 4 + VaultFormat.NonceSize + VaultFormat.TagSize;
                if (raw.Length > header && raw.AsSpan(0, 4).SequenceEqual(DbMagic))
                {
                    var key = DbKey(_masterKey!);
                    try
                    {
                        int cipherLen = raw.Length - header;
                        var plain = new byte[cipherLen];
                        using (var aes = new AesGcm(key, VaultFormat.TagSize))
                        {
                            aes.Decrypt(raw.AsSpan(4, VaultFormat.NonceSize), raw.AsSpan(header, cipherLen),
                                raw.AsSpan(4 + VaultFormat.NonceSize, VaultFormat.TagSize), plain);
                        }

                        var data = JsonSerializer.Deserialize<VaultDbData>(plain);
                        if (data != null)
                            foreach (var p in data.Playlists)
                                list.Add(new Playlist
                                {
                                    Id = string.IsNullOrEmpty(p.Id) ? Guid.NewGuid().ToString("N") : p.Id,
                                    Name = p.Name,
                                    Items = p.Items.Select(n => Path.Combine(VaultPath, Path.GetFileName(n))).ToList()
                                });
                        CryptographicOperations.ZeroMemory(plain);
                    }
                    finally { CryptographicOperations.ZeroMemory(key); }
                }
            }
        }
        catch
        {
            // Unreadable (damaged, or written with another key): keep a copy and start with no playlists.
            try { File.Copy(DbPath, DbPath + ".bad", overwrite: true); } catch { /* ignore */ }
        }

        lock (_dbLock) _playlists = list;
    }

    /// <summary>Writes the playlists back, encrypted (atomic: temp file, then rename).</summary>
    public void SavePlaylists()
    {
        byte[] master;
        lock (_keyLock)
        {
            if (_masterKey == null) return;
            master = (byte[])_masterKey.Clone();
        }

        var key = DbKey(master);
        try
        {
            VaultDbData data;
            lock (_dbLock)
            {
                data = new VaultDbData
                {
                    Playlists = (_playlists ?? new List<Playlist>()).Select(p => new VaultDbPlaylist
                    {
                        Id = p.Id,
                        Name = p.Name,
                        Items = p.Items.Select(i => Path.GetFileName(i)).ToList()
                    }).ToList()
                };
            }

            var plain = JsonSerializer.SerializeToUtf8Bytes(data);
            var nonce = RandomNumberGenerator.GetBytes(VaultFormat.NonceSize);
            var cipher = new byte[plain.Length];
            var tag = new byte[VaultFormat.TagSize];
            using (var aes = new AesGcm(key, VaultFormat.TagSize))
                aes.Encrypt(nonce, plain, cipher, tag);
            CryptographicOperations.ZeroMemory(plain);

            EnsureDirectory();
            var tmp = DbPath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(DbMagic);
                fs.Write(nonce);
                fs.Write(tag);
                fs.Write(cipher);
                fs.Flush(true);
            }
            File.Move(tmp, DbPath, overwrite: true);
        }
        catch { /* best effort: the playlists stay in memory for this session */ }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Zero(master);
        }
    }

    /// <summary>Takes a deleted / moved-out vault file out of every playlist (call on the UI thread).</summary>
    public void RemoveFromPlaylists(string vaultFilePath)
    {
        bool changed = false;
        lock (_dbLock)
        {
            if (_playlists == null) return;
            foreach (var p in _playlists)
                changed |= p.Items.RemoveAll(i => string.Equals(i, vaultFilePath, StringComparison.OrdinalIgnoreCase)) > 0;
        }
        if (changed) SavePlaylists();
    }

    // ------------------------------------------------------------------ encrypted thumbnails (<guid>.svt)

    private static byte[] ThumbKey(byte[] master, string vaultFile) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32,
            Encoding.UTF8.GetBytes(Path.GetFileNameWithoutExtension(vaultFile)), ThumbInfo);

    private static string ThumbPathFor(string vaultFile) => Path.ChangeExtension(vaultFile, ThumbExtension);

    /// <summary>
    /// At import the original is still readable, so Explorer's thumbnail (video frame / album art) is encrypted into a
    /// small sidecar file. The picture is never written anywhere in the clear.
    /// </summary>
    private static void SaveThumb(string sourcePath, string vaultFile, byte[] master)
    {
        try
        {
            var bmp = ShellThumbnail.Get(sourcePath, 320, 180);
            if (bmp == null) return;
            if (MediaTypes.IsAudio(sourcePath)) bmp = ShellThumbnail.TrimBorders(bmp);

            var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            var plain = ms.ToArray();

            var key = ThumbKey(master, vaultFile);
            try
            {
                var nonce = RandomNumberGenerator.GetBytes(VaultFormat.NonceSize);
                var cipher = new byte[plain.Length];
                var tag = new byte[VaultFormat.TagSize];
                using (var aes = new AesGcm(key, VaultFormat.TagSize)) aes.Encrypt(nonce, plain, cipher, tag);

                using var fs = new FileStream(ThumbPathFor(vaultFile), FileMode.Create, FileAccess.Write, FileShare.None);
                fs.Write(nonce);
                fs.Write(tag);
                fs.Write(cipher);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        catch { /* no thumbnail: the tile shows its icon */ }
    }

    /// <summary>Decrypts a vault file's thumbnail in memory (null when it has none).</summary>
    public BitmapSource? LoadThumb(string vaultFile)
    {
        try
        {
            var path = ThumbPathFor(vaultFile);
            if (!File.Exists(path)) return null;

            var raw = File.ReadAllBytes(path);
            const int header = VaultFormat.NonceSize + VaultFormat.TagSize;
            if (raw.Length <= header) return null;

            var master = KeyCopy();
            var key = ThumbKey(master, vaultFile);
            try
            {
                int cipherLen = raw.Length - header;
                var plain = new byte[cipherLen];
                using (var aes = new AesGcm(key, VaultFormat.TagSize))
                    aes.Decrypt(raw.AsSpan(0, VaultFormat.NonceSize), raw.AsSpan(header, cipherLen),
                        raw.AsSpan(VaultFormat.NonceSize, VaultFormat.TagSize), plain);

                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(plain);
                image.EndInit();
                image.Freeze();
                return image;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                Zero(master);
            }
        }
        catch { return null; }
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
                     .Where(f => f.EndsWith(VaultFormat.Extension, StringComparison.OrdinalIgnoreCase)
                                 || f.EndsWith(ThumbExtension, StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(Path.GetFileName(f), DbName, StringComparison.OrdinalIgnoreCase)).ToList())
            File.Move(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);
        lock (_dbLock) _playlists = null;
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
        lock (_dbLock) _playlists = null;
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
                    items.Add(new VaultItem(meta.Name, path, FormatSize(meta.Length), meta.Origin,
                        meta.Title ?? "", meta.Artist ?? "", meta.Album ?? "", meta.Genre ?? "", meta.DurationMs));
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
            // Details + thumbnail are read from the plaintext original NOW (it may be deleted below) and stored encrypted.
            var tags = LibraryScanner.ReadTags(sourcePath);
            VaultFormat.EncryptFile(sourcePath, dest, key, Path.GetFileName(sourcePath), Path.GetDirectoryName(sourcePath), tags);
            SaveThumb(sourcePath, dest, key);

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
                try { var t = ThumbPathFor(dest); if (File.Exists(t)) File.Delete(t); } catch { }
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
    public async Task SecureDeleteAsync(string path)
    {
        EnsureInsideVault(path);
        await Task.Run(() => SecureDeleteCore(path));

        // Back on the caller's (UI) thread: tidy up everything that pointed at the file.
        try { var t = ThumbPathFor(path); if (File.Exists(t)) File.Delete(t); } catch { /* sidecar is encrypted anyway */ }
        RemoveFromPlaylists(path);
    }

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
