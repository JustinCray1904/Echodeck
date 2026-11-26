using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace EchoDeck;

public sealed partial class VaultService
{
    // ------------------------------------------------------------------ encrypted thumbnails (<guid>.svt)

    private static byte[] ThumbKey(byte[] master, string vaultFile) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32,
            Encoding.UTF8.GetBytes(Path.GetFileNameWithoutExtension(vaultFile)), ThumbInfo);

    private static string ThumbPathFor(string vaultFile) => Path.ChangeExtension(vaultFile, ThumbExtension);
    private static string LegacyThumbPathFor(string vaultFile) => Path.ChangeExtension(vaultFile, LegacyThumbExtension);

    /// <summary>
    /// At import the original is still readable, so a small picture (a song's embedded cover) is encrypted into a sidecar file.
    /// Songs are read with TagLib, inside Tempo: nothing is handed to Windows. A video frame can only come from Windows' own thumbnail
    /// extraction, which keeps a plaintext copy in the Windows thumbnail cache (thumbcache_*.db) that survives deleting the original, so
    /// that is used ONLY when "Preview pictures for videos" is switched on in the Anonymous settings (off by default).
    /// </summary>
    private static void SaveThumb(string sourcePath, string vaultFile, byte[] master)
    {
        try
        {
            System.Windows.Media.Imaging.BitmapSource? bmp;
            if (MediaTypes.IsAudio(sourcePath)) bmp = EmbeddedCover(sourcePath);
            else bmp = AppSettings.Current.AnonymousVideoThumbnails ? ShellThumbnail.Get(sourcePath, 320, 180) : null;
            if (bmp == null) return;

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

    /// <summary>The cover picture stored inside a song's tags, scaled down; read by Tempo itself (no Windows thumbnail cache involved).</summary>
    private static System.Windows.Media.Imaging.BitmapSource? EmbeddedCover(string path)
    {
        try
        {
            var file = TagLib.File.Create(path, TagLib.ReadStyle.Average);
            try
            {
                var pictures = file.Tag?.Pictures;
                if (pictures == null || pictures.Length == 0) return null;
                byte[] data = pictures[0].Data.Data;
                if (data == null || data.Length < 100) return null;

                using var stream = new MemoryStream(data);
                var image = new System.Windows.Media.Imaging.BitmapImage();
                image.BeginInit();
                image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 320;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
            finally { (file as IDisposable)?.Dispose(); }
        }
        catch { return null; }   // no tags, no picture, or a picture format WPF can't read: the tile shows its icon
    }

    /// <summary>Decrypts a vault file's thumbnail in memory (null when it has none).</summary>
    public BitmapSource? LoadThumb(string vaultFile)
    {
        try
        {
            var path = ThumbPathFor(vaultFile);
            if (!File.Exists(path))
            {
                path = LegacyThumbPathFor(vaultFile);   // pictures saved by earlier builds
                if (!File.Exists(path)) return null;
            }

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
                     .Where(f => VaultFormat.IsVaultFile(f)
                                 || f.EndsWith(ThumbExtension, StringComparison.OrdinalIgnoreCase)
                                 || f.EndsWith(LegacyThumbExtension, StringComparison.OrdinalIgnoreCase)
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

    private static bool _stalePartsCleaned;

    /// <summary>
    /// Encrypted files are written to "name.part" and renamed when complete. If Tempo was killed, crashed or lost power in the middle
    /// of one, that unfinished file is still there (never under a real name, and the original was not touched). It is removed once per
    /// run, before anything of this run is written, so nothing half-finished piles up in the vault folder.
    /// </summary>
    private void CleanStaleParts()
    {
        if (_stalePartsCleaned) return;
        _stalePartsCleaned = true;
        try
        {
            foreach (var pattern in new[] { "*" + VaultFormat.Extension + ".part", "*" + VaultFormat.LegacyExtension + ".part" })
            {
                foreach (var part in Directory.EnumerateFiles(VaultPath, pattern))
                {
                    try { File.Delete(part); } catch { /* in use or locked: next run */ }
                }
            }
        }
        catch { /* best effort */ }
    }

    private void EnsureDirectory()
    {
        var dir = Directory.CreateDirectory(VaultPath);
        CleanStaleParts();
        try
        {
            dir.Refresh();
            if ((dir.Attributes & FileAttributes.Hidden) == 0)
                dir.Attributes |= FileAttributes.Hidden; // hidden from normal Explorer views
        }
        catch { /* best effort */ }
    }

    private bool HasEncryptedFiles() =>
        Directory.EnumerateFiles(VaultPath).Any(VaultFormat.IsVaultFile);

    private string NewVaultFilePath() =>
        Path.Combine(VaultPath, Guid.NewGuid().ToString("N") + VaultFormat.Extension);

    private void EnsureInsideVault(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(VaultPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("That path is outside the vault.");
    }

    /// <summary>How many encrypted files the last <see cref="ListAsync"/> could not read (damaged, cut short, or made with another key).</summary>
    public int LastUnreadableCount { get; private set; }

    public Task<IReadOnlyList<VaultItem>> ListAsync() => Task.Run<IReadOnlyList<VaultItem>>(() =>
    {
        EnsureDirectory();
        var key = KeyCopy();
        try
        {
            int unreadable = 0;
            var found = new System.Collections.Concurrent.ConcurrentBag<VaultItem>();
            var files = Directory.EnumerateFiles(VaultPath).Where(VaultFormat.IsVaultFile).ToList();

            // Every file's header has to be opened, read and decrypted. One after the other, a hard drive spends most of that time moving
            // its head from file to file, which is what made entering Anonymous Mode slow; several at once lets the drive and the CPU overlap.
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 4 }, path =>
            {
                try
                {
                    var meta = VaultFormat.ReadMeta(path, key);
                    found.Add(new VaultItem(meta.Name, path, FormatSize(meta.Length), meta.Origin,
                        meta.Title ?? "", meta.Artist ?? "", meta.Album ?? "", meta.Genre ?? "", meta.DurationMs));
                }
                catch { Interlocked.Increment(ref unreadable); /* damaged or foreign file: left untouched, reported to the user */ }
            });
            var items = found.ToList();
            LastUnreadableCount = unreadable;
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
            var before = new FileInfo(sourcePath);
            long lengthBefore = before.Length;
            DateTime writtenBefore = before.LastWriteTimeUtc;
            byte[] sourceHash = VaultFormat.EncryptFile(sourcePath, dest, key, Path.GetFileName(sourcePath), Path.GetDirectoryName(sourcePath), tags);
            SaveThumb(sourcePath, dest, key);

            if (!deleteOriginal) return;

            // The original is only destroyed after the encrypted copy has been read back IN FULL (every chunk authenticated) and holds exactly
            // what was read from the original (SHA-256, worked out while encrypting, so the original is not read a second time), and the
            // original is still the file that was encrypted. Comparing sizes alone would not notice damaged data.
            try
            {
                var after = new FileInfo(sourcePath);
                if (after.Length != lengthBefore || after.LastWriteTimeUtc != writtenBefore)
                    throw new InvalidDataException("The file changed while it was being encrypted, so it was kept.");

                byte[] copyHash = VaultFormat.DecryptFile(dest, key, null);
                if (!CryptographicOperations.FixedTimeEquals(copyHash, sourceHash))
                    throw new InvalidDataException("Verification failed: the encrypted copy does not match the original, which was kept.");
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
            // Written to "name.part" and renamed only when complete: an interruption (exit, crash, power loss) can never leave a
            // half-written file that looks like the real one. The encrypted copy stays until the caller deletes it after success.
            long expected;
            var temp = target + ".part";
            expected = VaultFormat.ReadMeta(item.FullPath, key).Length;
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                VaultFormat.DecryptFile(item.FullPath, key, output);   // fast, parallel, every chunk authenticated
                output.Flush(true);
            }

            if (new FileInfo(temp).Length != expected) throw new InvalidDataException("The restored file is incomplete.");
            File.Move(temp, target);
            return target;
        }
        catch
        {
            try { if (File.Exists(target + ".part")) File.Delete(target + ".part"); } catch { /* leave nothing half-written behind */ }
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
            return new DirectoryInfo(VaultPath).EnumerateFiles().Where(f => VaultFormat.IsVaultFile(f.Name)).Sum(f => f.Length);
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
                catch (Exception ex)
                {
                    // encrypted copy exists; original will be retried on a later run (the type of failure only: no names)
                    AppLog.Event(AppLogLevel.Warning, "vault.original-not-removed", ("exception", ex.GetType().Name));
                }
            }
        }
        finally { Zero(key); }
        return converted;
    });
}
