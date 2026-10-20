using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EchoDeck;

public sealed record VaultMeta(string Name, long Length);

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
    public const string Extension = ".svv";
    public const int SaltSize = 16;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int ChunkSize = 256 * 1024;

    private const int MaxMetaSize = 4096;
    private static readonly byte[] Magic = { (byte)'S', (byte)'V', (byte)'V', 1 };
    private static readonly byte[] KeyInfo = Encoding.UTF8.GetBytes("svv-file-key-v1");

    internal static void ChunkNonce(Span<byte> nonce, ulong index)
    {
        nonce.Clear();
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], index);
    }

    private static byte[] DeriveFileKey(byte[] masterKey, byte[] salt) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, salt, KeyInfo);

    /// <summary>Encrypts <paramref name="sourcePath"/> into a new vault file (written to .part, then renamed).</summary>
    internal static void EncryptFile(string sourcePath, string destPath, byte[] masterKey, string originalName)
    {
        using var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
        long length = src.Length;

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var fileKey = DeriveFileKey(masterKey, salt);
        var tmpPath = destPath + ".part";

        try
        {
            using var aes = new AesGcm(fileKey, TagSize);
            var meta = JsonSerializer.SerializeToUtf8Bytes(new VaultMeta(originalName, length));

            using (var dst = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
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

                var plain = new byte[ChunkSize];
                var cipher = new byte[ChunkSize];
                var tag = new byte[TagSize];
                ulong index = 0;
                long remaining = length;

                while (remaining > 0)
                {
                    int want = (int)Math.Min(ChunkSize, remaining);
                    src.ReadExactly(plain, 0, want);
                    ChunkNonce(nonce, index++);
                    aes.Encrypt(nonce, plain.AsSpan(0, want), cipher.AsSpan(0, want), tag);
                    dst.Write(cipher, 0, want);
                    dst.Write(tag);
                    remaining -= want;
                }

                CryptographicOperations.ZeroMemory(plain);
                dst.Flush(true);
            }

            File.Move(tmpPath, destPath);
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
