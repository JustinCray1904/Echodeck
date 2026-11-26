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
public sealed partial class VaultService
{
    private readonly object _keyLock = new();
    private readonly object _dbLock = new();
    private byte[]? _masterKey;
    private List<Playlist>? _playlists;

    private const string DbName = "vault.db";
    private const string ThumbExtension = ".deet";
    private const string LegacyThumbExtension = ".svt";   // earlier builds
    private static readonly byte[] DbMagic = { (byte)'S', (byte)'V', (byte)'D', 1 };
    private static readonly byte[] DbInfo = Encoding.UTF8.GetBytes("svv-db-v1");
    private static readonly byte[] ThumbInfo = Encoding.UTF8.GetBytes("svv-thumb-v1");

    public string VaultPath { get; }

    public VaultService()
    {
        VaultPath = AppPaths.VaultDir;
        EnsureDirectory();
    }

    /// <summary>For tests: a vault in another folder that is already unlocked with the given key (no key file, no DPAPI).</summary>
    internal VaultService(string vaultPath, byte[] masterKey)
    {
        VaultPath = vaultPath;
        EnsureDirectory();
        _masterKey = (byte[])masterKey.Clone();
    }

    /// <summary>For tests: reads vault.db again (what Unlock does).</summary>
    internal void ReloadPlaylists()
    {
        lock (_keyLock) LoadPlaylists();
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
        catch (Exception ex)
        {
            // Unreadable (damaged, or written with another key): keep a copy and start with no playlists. Only the kind of failure is logged.
            AppLog.Failure("Vault playlists", ex, "vault.db could not be read, a copy was kept as vault.db.bad");
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
        catch (Exception ex)
        {
            // best effort: the playlists stay in memory for this session
            AppLog.Failure("Vault playlists", ex, "vault.db could not be saved");
        }
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
        try { var t = LegacyThumbPathFor(path); if (File.Exists(t)) File.Delete(t); } catch { /* sidecar is encrypted anyway */ }
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
                    // One block of random bytes, written over the file again and again: making fresh random data for every megabyte
                    // cost as much time as the encryption itself, and what the file held is gone just the same.
                    var buffer = new byte[4 << 20];
                    RandomNumberGenerator.Fill(buffer);
                    long remaining = fs.Length;
                    while (remaining > 0)
                    {
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
