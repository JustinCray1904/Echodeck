using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EchoDeck;
using Xunit;

namespace EchoDeck.Tests;

/// <summary>
/// The vault's encryption: round trips at the sizes where chunk / batch edges are, random seeks, tampering, and (most important) that the
/// file format is still exactly the documented one, by encrypting and decrypting with an independent, plain implementation of it.
/// </summary>
public sealed class VaultCryptoTests : IDisposable
{
    private const int Chunk = VaultFormat.ChunkSize;   // 256 KiB
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tempo-vault-tests-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    public VaultCryptoTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp folder */ }
    }

    public static IEnumerable<object[]> Sizes() => new[]
    {
        0, 1, 100, Chunk - 1, Chunk, Chunk + 1, 3 * Chunk, 16 * Chunk - 1, 16 * Chunk, 16 * Chunk + 1, 33 * Chunk + 12345,
    }.Select(size => new object[] { size });

    private static byte[] Data(int size)
    {
        var data = new byte[size];
        new Random(size * 31 + 7).NextBytes(data);
        return data;
    }

    private string Encrypt(byte[] data, out byte[] hash, string name = "song.bin")
    {
        string source = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".src");
        File.WriteAllBytes(source, data);
        string dest = Path.Combine(_dir, Guid.NewGuid().ToString("N") + VaultFormat.Extension);
        hash = VaultFormat.EncryptFile(source, dest, _key, name);
        return dest;
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void TheStreamReadsBackWhatWasEncrypted(int size)
    {
        var data = Data(size);
        string vault = Encrypt(data, out _);

        using var stream = new EncryptedVaultStream(vault, _key);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);

        Assert.Equal(data, copy.ToArray());
        Assert.Null(stream.Error);
        Assert.Equal("song.bin", stream.Name);
        Assert.Equal(size, stream.Length);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void DecryptFileRestoresTheFileAndReturnsItsHash(int size)
    {
        var data = Data(size);
        string vault = Encrypt(data, out var encryptHash);

        using var output = new MemoryStream();
        var decryptHash = VaultFormat.DecryptFile(vault, _key, output);

        Assert.Equal(data, output.ToArray());
        Assert.Equal(SHA256.HashData(data), encryptHash);   // the hash worked out while encrypting
        Assert.Equal(SHA256.HashData(data), decryptHash);   // and the one worked out while decrypting
        Assert.Equal(VaultFormat.DecryptFile(vault, _key, null), decryptHash);   // verifying only (no output) gives the same
    }

    [Fact]
    public void RandomSeeksAndReadsReturnTheRightBytes()
    {
        var data = Data(40 * Chunk + 999);
        string vault = Encrypt(data, out _);
        using var stream = new EncryptedVaultStream(vault, _key);
        var random = new Random(5);

        for (int i = 0; i < 300; i++)
        {
            long offset = random.Next(0, data.Length);
            int count = random.Next(1, 3 * Chunk);
            var buffer = new byte[count];

            stream.Seek(offset, SeekOrigin.Begin);
            int read = 0, got;
            while (read < count && (got = stream.Read(buffer, read, count - read)) > 0) read += got;

            int expected = (int)Math.Min(count, data.Length - offset);
            Assert.Equal(expected, read);
            Assert.True(data.AsSpan((int)offset, expected).SequenceEqual(buffer.AsSpan(0, expected)));
        }
        Assert.Null(stream.Error);
    }

    // ---- the format itself: written and read by a plain implementation that shares no code with the real one

    private byte[] ReferenceFileKey(byte[] salt) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _key, 32, salt, Encoding.UTF8.GetBytes("svv-file-key-v1"));

    private static byte[] Nonce(ulong index)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), index);
        return nonce;
    }

    private string ReferenceEncrypt(byte[] data, string name)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        using var aes = new AesGcm(ReferenceFileKey(salt), 16);
        var meta = JsonSerializer.SerializeToUtf8Bytes(new VaultMeta(name, data.Length));
        var metaCipher = new byte[meta.Length];
        var metaTag = new byte[16];
        aes.Encrypt(Nonce(ulong.MaxValue), meta, metaCipher, metaTag);

        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + VaultFormat.Extension);
        using var file = File.Create(path);
        file.Write(new byte[] { (byte)'S', (byte)'V', (byte)'V', 1 });
        file.Write(salt);
        var len = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)meta.Length);
        file.Write(len);
        file.Write(metaCipher);
        file.Write(metaTag);

        for (long chunk = 0, at = 0; at < data.Length; chunk++, at += Chunk)
        {
            int want = Math.Min(Chunk, data.Length - (int)at);
            var cipher = new byte[want];
            var tag = new byte[16];
            aes.Encrypt(Nonce((ulong)chunk), data.AsSpan((int)at, want), cipher, tag);
            file.Write(cipher);
            file.Write(tag);
        }
        return path;
    }

    private byte[] ReferenceDecrypt(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var salt = bytes.AsSpan(4, 16).ToArray();
        uint metaLen = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20, 4));
        using var aes = new AesGcm(ReferenceFileKey(salt), 16);

        var metaPlain = new byte[metaLen];
        aes.Decrypt(Nonce(ulong.MaxValue), bytes.AsSpan(24, (int)metaLen), bytes.AsSpan(24 + (int)metaLen, 16), metaPlain);
        long length = JsonSerializer.Deserialize<VaultMeta>(metaPlain)!.Length;

        var result = new byte[length];
        int pos = 24 + (int)metaLen + 16;
        for (long chunk = 0, at = 0; at < length; chunk++, at += Chunk)
        {
            int want = (int)Math.Min(Chunk, length - at);
            aes.Decrypt(Nonce((ulong)chunk), bytes.AsSpan(pos, want), bytes.AsSpan(pos + want, 16), result.AsSpan((int)at, want));
            pos += want + 16;
        }
        return result;
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void FilesMadeByThePlainReferenceStillOpen(int size)
    {
        var data = Data(size);
        string vault = ReferenceEncrypt(data, "old.bin");   // what an earlier build wrote

        using (var stream = new EncryptedVaultStream(vault, _key))
        using (var copy = new MemoryStream())
        {
            stream.CopyTo(copy);
            Assert.Equal(data, copy.ToArray());
        }

        using var output = new MemoryStream();
        VaultFormat.DecryptFile(vault, _key, output);
        Assert.Equal(data, output.ToArray());
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void FilesMadeNowOpenWithThePlainReference(int size)
    {
        var data = Data(size);
        string vault = Encrypt(data, out _);
        Assert.Equal(data, ReferenceDecrypt(vault));   // an earlier build / the documented format reads what is written now
    }

    // ---- damage

    [Fact]
    public void ADamagedByteIsDetectedWhenRestoringAndWhenPlaying()
    {
        var data = Data(20 * Chunk);
        string vault = Encrypt(data, out _);
        var bytes = File.ReadAllBytes(vault);
        bytes[bytes.Length / 2] ^= 0x40;   // somewhere in the middle of the content
        File.WriteAllBytes(vault, bytes);

        Assert.ThrowsAny<Exception>(() => VaultFormat.DecryptFile(vault, _key, null));

        using var stream = new EncryptedVaultStream(vault, _key);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        Assert.NotNull(stream.Error);                         // playback reports the failure instead of crashing
        Assert.True(copy.Length < data.Length);               // and stops before the damaged chunk
    }

    [Fact]
    public void ACutShortFileIsRefused()
    {
        string vault = Encrypt(Data(5 * Chunk), out _);
        var bytes = File.ReadAllBytes(vault);
        File.WriteAllBytes(vault, bytes.AsSpan(0, bytes.Length - 100).ToArray());

        Assert.ThrowsAny<Exception>(() => VaultFormat.DecryptFile(vault, _key, null));
        Assert.ThrowsAny<Exception>(() => new EncryptedVaultStream(vault, _key));
    }

    [Fact]
    public void TheWrongKeyIsRefused()
    {
        string vault = Encrypt(Data(Chunk + 5), out _);
        var wrong = Enumerable.Repeat((byte)9, 32).ToArray();

        Assert.ThrowsAny<Exception>(() => VaultFormat.DecryptFile(vault, wrong, null));
        Assert.ThrowsAny<Exception>(() => new EncryptedVaultStream(vault, wrong));
    }

    [Fact]
    public void NoHalfWrittenFileIsLeftBehind()
    {
        string source = Path.Combine(_dir, "in.bin");
        File.WriteAllBytes(source, Data(Chunk));
        string dest = Path.Combine(_dir, "out" + VaultFormat.Extension);

        VaultFormat.EncryptFile(source, dest, _key, "in.bin");

        Assert.True(File.Exists(dest));
        Assert.False(File.Exists(dest + ".part"));
    }
}
