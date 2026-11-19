using System.Security.Cryptography;

namespace EchoDeck;

/// <summary>
/// Read-only, seekable stream that decrypts a vault file on the fly (the last 8 chunks of 256 KiB cached). Plain video
/// bytes exist only in memory. Read/Seek never throw: on any error (tampering, I/O, disposed) Read reports
/// end-of-stream and <see cref="Error"/> is set, because an exception escaping into a native LibVLC callback
/// would crash the process.
/// </summary>
public sealed class EncryptedVaultStream : Stream
{
    private readonly object _lock = new();
    private readonly FileStream _file;
    private readonly AesGcm _aes;
    private readonly long _dataOffset;
    private readonly long _length;
    private readonly byte[] _cipherBuf = new byte[VaultFormat.ChunkSize + VaultFormat.TagSize];

    // The last few chunks stay decrypted in memory: a player reads around the same place again and again (headers, index, small
    // seeks back), and a miss costs a disk read plus a decryption of 256 KiB.
    private const int CacheSlots = 8;
    private readonly byte[]?[] _plain = new byte[CacheSlots][];
    private readonly long[] _chunkAt = new long[CacheSlots];   // the chunk each slot holds (-1: empty)
    private readonly int[] _lenAt = new int[CacheSlots];
    private readonly long[] _usedAt = new long[CacheSlots];
    private long _tick;
    private long _position;
    private bool _disposed;

    /// <summary>Original file name, recovered from the encrypted header.</summary>
    public string Name { get; }

    /// <summary>Set if a read failed (e.g. authentication failure).</summary>
    public string? Error { get; private set; }

    public EncryptedVaultStream(string path, byte[] masterKey)
    {
        // FileShare.Read: nobody can write/delete the file while it's open, so deletes must release it first.
        // No "random access" hint: that switches off Windows' read-ahead, which playing a video from start to end depends on.
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.None);
        Array.Fill(_chunkAt, -1L);
        try
        {
            _aes = VaultFormat.ReadHeader(_file, masterKey, out var meta, out _dataOffset);
            _length = meta.Length;
            Name = meta.Name;
        }
        catch
        {
            _file.Dispose();
            throw;
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get { lock (_lock) return _position; }
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            if (_disposed) return 0;

            try
            {
                int total = 0;
                while (count > 0 && _position < _length)
                {
                    long chunk = _position / VaultFormat.ChunkSize;
                    int slot = EnsureChunk(chunk);

                    int inChunk = (int)(_position - chunk * VaultFormat.ChunkSize);
                    int n = Math.Min(count, _lenAt[slot] - inChunk);
                    Buffer.BlockCopy(_plain[slot]!, inChunk, buffer, offset, n);

                    _position += n;
                    offset += n;
                    count -= n;
                    total += n;
                }
                return total;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                return 0;
            }
        }
    }

    /// <summary>Makes sure <paramref name="chunk"/> is decrypted in memory and returns the cache slot that holds it.</summary>
    private int EnsureChunk(long chunk)
    {
        int victim = 0;
        for (int i = 0; i < CacheSlots; i++)
        {
            if (_chunkAt[i] == chunk) { _usedAt[i] = ++_tick; return i; }
            if (_chunkAt[victim] != -1 && (_chunkAt[i] == -1 || _usedAt[i] < _usedAt[victim])) victim = i;
        }

        _chunkAt[victim] = -1;   // not valid while it is being replaced: a failed read must never leave old data under the wrong chunk
        _plain[victim] ??= new byte[VaultFormat.ChunkSize];

        long plainStart = chunk * VaultFormat.ChunkSize;
        int plainLen = (int)Math.Min(VaultFormat.ChunkSize, _length - plainStart);

        _file.Seek(_dataOffset + chunk * (VaultFormat.ChunkSize + VaultFormat.TagSize), SeekOrigin.Begin);
        _file.ReadExactly(_cipherBuf, 0, plainLen + VaultFormat.TagSize);

        Span<byte> nonce = stackalloc byte[VaultFormat.NonceSize];
        VaultFormat.ChunkNonce(nonce, (ulong)chunk);

        _aes.Decrypt(
            nonce,
            _cipherBuf.AsSpan(0, plainLen),
            _cipherBuf.AsSpan(plainLen, VaultFormat.TagSize),
            _plain[victim].AsSpan(0, plainLen));

        _chunkAt[victim] = chunk;
        _lenAt[victim] = plainLen;
        _usedAt[victim] = ++_tick;
        return victim;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        lock (_lock)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => offset
            };
            _position = Math.Clamp(target, 0, _length);
            return _position;
        }
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    for (int i = 0; i < CacheSlots; i++)
                    {
                        _chunkAt[i] = -1;
                        if (_plain[i] is { } buffer) CryptographicOperations.ZeroMemory(buffer);
                    }
                    _aes.Dispose();
                    _file.Dispose(); // releases the file handle
                }
            }
        }
        base.Dispose(disposing);
    }
}
