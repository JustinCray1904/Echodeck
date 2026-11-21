using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EchoDeck;

/// <summary>
/// The encrypted header of a vault file. Besides the name, size and original folder it carries the song / video details
/// read at import (all optional, so files imported by earlier builds still open and just show no details).
/// </summary>
public sealed record VaultMeta(string Name, long Length, string? Origin = null,
    string? Title = null, string? Artist = null, string? Album = null, string? Genre = null, long DurationMs = 0);

/// <summary>
/// On-disk format ("SVV1"). Each file gets its own random salt and AES-256-GCM key (HKDF from the master key),
/// and the content is split into 256 KiB chunks so playback can seek without decrypting the whole file.
///
///   magic "SVV\x01" (4) | salt (16) | metaLen u32 LE (4) | meta ciphertext | meta tag (16)
///   then for chunk i: ciphertext (up to 256 KiB) | tag (16)
///
/// Nonce = 4 zero bytes + chunk index (u64 big-endian); the metadata block uses index 0xFF..FF.
/// The metadata (original file name + plaintext length) is encrypted too, so file names never appear on disk
/// (files are stored under random GUID names). Chunk order is bound by the nonce, truncation is caught by
/// the authenticated length plus an exact file-size check.
/// </summary>
public static class VaultFormat
{
    /// <summary>The extension of encrypted files made now. (The format inside is unchanged.)</summary>
    public const string Extension = ".deex";

    /// <summary>The extension earlier builds used: those files are still found and opened exactly as before.</summary>
    public const string LegacyExtension = ".svv";

    /// <summary>Whether this file name is an encrypted vault file (new or earlier extension).</summary>
    public static bool IsVaultFile(string path) =>
        path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) || path.EndsWith(LegacyExtension, StringComparison.OrdinalIgnoreCase);
    public const int SaltSize = 16;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int ChunkSize = 256 * 1024;

    private const int MaxMetaSize = 16384;
    private const int MaxFieldChars = 120;

    // Keep non-ASCII text as real UTF-8 in the header (the default escapes it to \uXXXX and would bloat the block).
    private static readonly JsonSerializerOptions MetaJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string? Cap(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : (value.Length <= MaxFieldChars ? value : value.Substring(0, MaxFieldChars));
    private static readonly byte[] Magic = { (byte)'S', (byte)'V', (byte)'V', 1 };
    private static readonly byte[] KeyInfo = Encoding.UTF8.GetBytes("svv-file-key-v1");

    internal static void ChunkNonce(Span<byte> nonce, ulong index)
    {
        nonce.Clear();
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], index);
    }

    private static byte[] DeriveFileKey(byte[] masterKey, byte[] salt) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, salt, KeyInfo);

    // ---- speed: the file is processed in batches of 32 chunks (8 MiB): big reads and writes mean fewer trips of a hard disk's head
    // between the source and the destination. The format on disk is exactly the same as before.
    internal const int BatchChunks = 32;
    private static int Workers => Math.Clamp(Environment.ProcessorCount, 1, 8);

    /// <summary>
    /// Encrypts <paramref name="sourcePath"/> into a new vault file (written to .part, then renamed) and returns the SHA-256 of the
    /// plaintext, worked out in the same pass. The chunks of a batch are independent (each has its own nonce), so they are encrypted on
    /// several CPU cores at once, and hashing + writing a batch overlaps with reading and encrypting the next one.
    /// </summary>
    internal static byte[] EncryptFile(string sourcePath, string destPath, byte[] masterKey, string originalName,
        string? origin = null, TagInfo? tags = null)
    {
        using var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
        long length = src.Length;

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var fileKey = DeriveFileKey(masterKey, salt);
        var tmpPath = destPath + ".part";

        try
        {
            byte[] hash;
            using (var aes = new AesGcm(fileKey, TagSize))
            using (var dst = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var meta = JsonSerializer.SerializeToUtf8Bytes(new VaultMeta(originalName, length, origin,
                    Cap(tags?.Title), Cap(tags?.Artist), Cap(tags?.Album), Cap(tags?.Genre),
                    Math.Max(0, (tags?.DurationSeconds ?? 0) * 1000)), MetaJson);

                Span<byte> nonce = stackalloc byte[NonceSize];
                Span<byte> lenBytes = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(lenBytes, (uint)meta.Length);

                var metaCipher = new byte[meta.Length];
                var metaTag = new byte[TagSize];
                ChunkNonce(nonce, ulong.MaxValue);
                aes.Encrypt(nonce, meta, metaCipher, metaTag);

                dst.Write(Magic);
                dst.Write(salt);
                dst.Write(lenBytes);
                dst.Write(metaCipher);
                dst.Write(metaTag);

                hash = EncryptBody(src, dst, fileKey, length);
                dst.Flush(true);
            }

            File.Move(tmpPath, destPath);
            return hash;
        }
        catch
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* best effort */ }
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileKey);
        }
    }

    private static byte[] EncryptBody(Stream src, Stream dst, byte[] fileKey, long length)
    {
        const int stride = ChunkSize + TagSize;
        int batchBytes = BatchChunks * ChunkSize;
        var plain = new[] { new byte[batchBytes], new byte[batchBytes] };
        var packed = new[] { new byte[BatchChunks * stride], new byte[BatchChunks * stride] };
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var workers = new ThreadLocal<AesGcm>(() => new AesGcm(fileKey, TagSize), trackAllValues: true);
        var options = new ParallelOptions { MaxDegreeOfParallelism = Workers };

        Task? writing = null;   // the batch being hashed and written; at most one is in flight
        int slot = 0;
        ulong index = 0;
        long remaining = length;
        try
        {
            while (remaining > 0)
            {
                var p = plain[slot];
                var o = packed[slot];
                int n = (int)Math.Min(batchBytes, remaining);
                src.ReadExactly(p, 0, n);

                int chunks = (n + ChunkSize - 1) / ChunkSize;
                ulong first = index;
                RunParallel(chunks, options, i =>
                {
                    Span<byte> nonce = stackalloc byte[NonceSize];
                    ChunkNonce(nonce, first + (ulong)i);
                    int from = i * ChunkSize;
                    int want = Math.Min(ChunkSize, n - from);
                    int to = i * stride;
                    workers.Value!.Encrypt(nonce, p.AsSpan(from, want), o.AsSpan(to, want), o.AsSpan(to + want, TagSize));
                });
                int packedLength = n + chunks * TagSize;

                writing?.GetAwaiter().GetResult();   // the previous batch is on disk now (rethrows its error)
                writing = Task.Run(() =>
                {
                    hash.AppendData(p, 0, n);
                    dst.Write(o, 0, packedLength);
                });

                slot ^= 1;
                index += (ulong)chunks;
                remaining -= n;
            }

            writing?.GetAwaiter().GetResult();
            writing = null;
            return hash.GetHashAndReset();
        }
        finally
        {
            try { writing?.GetAwaiter().GetResult(); } catch { /* the error that got us here is the one that is thrown */ }
            CryptographicOperations.ZeroMemory(plain[0]);
            CryptographicOperations.ZeroMemory(plain[1]);
            foreach (var worker in workers.Values) worker.Dispose();
        }
    }

    /// <summary>Parallel.For that throws the real exception (not an AggregateException around it).</summary>
    private static void RunParallel(int count, ParallelOptions options, Action<int> body)
    {
        try
        {
            if (count == 1) body(0);
            else Parallel.For(0, count, options, body);
        }
        catch (AggregateException ex) when (ex.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    /// <summary>
    /// Reads a vault file back completely, authenticating every chunk, and returns the SHA-256 of what it holds. The plaintext is written
    /// to <paramref name="output"/> when one is given (restoring), or only hashed (verifying). Throws if anything was changed or cut short.
    /// Chunks of a batch are decrypted on several CPU cores at once, and writing a batch overlaps with reading and decrypting the next.
    /// </summary>
    internal static byte[] DecryptFile(string path, byte[] masterKey, Stream? output, CancellationToken ct = default)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using var headerKey = ReadHeader(file, masterKey, out var meta, out long dataOffset);   // authenticates the header and the file size

        // the per-file key once more: every worker thread needs a decryptor of its own
        var salt = new byte[SaltSize];
        file.Seek(4, SeekOrigin.Begin);
        file.ReadExactly(salt);
        var fileKey = DeriveFileKey(masterKey, salt);
        file.Seek(dataOffset, SeekOrigin.Begin);

        const int stride = ChunkSize + TagSize;
        var cipher = new[] { new byte[BatchChunks * stride], new byte[BatchChunks * stride] };
        var plain = new[] { new byte[BatchChunks * ChunkSize], new byte[BatchChunks * ChunkSize] };
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var workers = new ThreadLocal<AesGcm>(() => new AesGcm(fileKey, TagSize), trackAllValues: true);
        var options = new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = ct };

        long chunksTotal = (meta.Length + ChunkSize - 1) / ChunkSize;
        long chunkIndex = 0;
        long plainRemaining = meta.Length;
        Task? writing = null;
        int slot = 0;
        try
        {
            while (chunkIndex < chunksTotal)
            {
                ct.ThrowIfCancellationRequested();
                int chunks = (int)Math.Min(BatchChunks, chunksTotal - chunkIndex);
                int plainLength = (int)Math.Min((long)chunks * ChunkSize, plainRemaining);
                int cipherLength = plainLength + chunks * TagSize;
                var c = cipher[slot];
                var p = plain[slot];
                file.ReadExactly(c, 0, cipherLength);

                ulong first = (ulong)chunkIndex;
                RunParallel(chunks, options, i =>
                {
                    Span<byte> nonce = stackalloc byte[NonceSize];
                    ChunkNonce(nonce, first + (ulong)i);
                    int pFrom = i * ChunkSize;
                    int want = Math.Min(ChunkSize, plainLength - pFrom);
                    int cFrom = i * stride;
                    workers.Value!.Decrypt(nonce, c.AsSpan(cFrom, want), c.AsSpan(cFrom + want, TagSize), p.AsSpan(pFrom, want));
                });

                writing?.GetAwaiter().GetResult();
                writing = Task.Run(() =>
                {
                    hash.AppendData(p, 0, plainLength);
                    output?.Write(p, 0, plainLength);
                });

                slot ^= 1;
                chunkIndex += chunks;
                plainRemaining -= plainLength;
            }

            writing?.GetAwaiter().GetResult();
            writing = null;
            return hash.GetHashAndReset();
        }
        finally
        {
            try { writing?.GetAwaiter().GetResult(); } catch { /* the error that got us here is the one that is thrown */ }
            CryptographicOperations.ZeroMemory(plain[0]);
            CryptographicOperations.ZeroMemory(plain[1]);
            foreach (var worker in workers.Values) worker.Dispose();
            CryptographicOperations.ZeroMemory(fileKey);
        }
    }

    /// <summary>Parses and authenticates the header. The returned AesGcm (per-file key) is owned by the caller.</summary>
    internal static AesGcm ReadHeader(Stream file, byte[] masterKey, out VaultMeta meta, out long dataOffset)
    {
        Span<byte> head = stackalloc byte[4 + SaltSize + 4];
        file.ReadExactly(head);

        if (!head[..4].SequenceEqual(Magic)) throw new InvalidDataException("Not a vault file.");

        var salt = head.Slice(4, SaltSize).ToArray();
        uint metaLen = BinaryPrimitives.ReadUInt32LittleEndian(head[(4 + SaltSize)..]);
        if (metaLen == 0 || metaLen > MaxMetaSize) throw new InvalidDataException("Damaged vault header.");

        var metaCipher = new byte[metaLen];
        var metaTag = new byte[TagSize];
        file.ReadExactly(metaCipher);
        file.ReadExactly(metaTag);

        var fileKey = DeriveFileKey(masterKey, salt);
        AesGcm? aes = null;
        try
        {
            aes = new AesGcm(fileKey, TagSize);

            var metaPlain = new byte[metaLen];
            Span<byte> nonce = stackalloc byte[NonceSize];
            ChunkNonce(nonce, ulong.MaxValue);
            aes.Decrypt(nonce, metaCipher, metaTag, metaPlain);

            meta = JsonSerializer.Deserialize<VaultMeta>(metaPlain)
                   ?? throw new InvalidDataException("Damaged vault metadata.");
            if (meta.Length < 0) throw new InvalidDataException("Damaged vault metadata.");

            dataOffset = 4 + SaltSize + 4 + metaLen + TagSize;
            long chunks = (meta.Length + ChunkSize - 1) / ChunkSize;
            long expectedSize = dataOffset + meta.Length + chunks * TagSize;
            if (file.Length != expectedSize) throw new InvalidDataException("The vault file is truncated or damaged.");

            var result = aes;
            aes = null;
            return result;
        }
        finally
        {
            aes?.Dispose();
            CryptographicOperations.ZeroMemory(fileKey);
        }
    }

    internal static VaultMeta ReadMeta(string path, byte[] masterKey)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
        using var aes = ReadHeader(fs, masterKey, out var meta, out _);
        return meta;
    }
}
