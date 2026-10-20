using System.Security.Cryptography;

namespace EchoDeck;

/// <summary>
/// Read-only, seekable stream that decrypts a vault file on the fly (one 256 KiB chunk cached). Plain video
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
    private readonly byte[] _plainBuf = new byte[VaultFormat.ChunkSize];
    private long _position;
    private long _cachedChunk = -1;
    private int _cachedLen;
    private bool _disposed;

    /// <summary>Original file name, recovered from the encrypted header.</summary>
    public string Name { get; }

    /// <summary>Set if a read failed (e.g. authentication failure).</summary>
    public string? Error { get; private set; }

    public EncryptedVaultStream(string path, byte[] masterKey)
    {
        // FileShare.Read: nobody can write/delete the file while it's open, so deletes must release it first.
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
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
                    EnsureChunk(chunk);

                    int inChunk = (int)(_position - chunk * VaultFormat.ChunkSize);
                    int n = Math.Min(count, _cachedLen - inChunk);
                    Buffer.BlockCopy(_plainBuf, inChunk, buffer, offset, n);

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

    private void EnsureChunk(long chunk)
    {
        if (chunk == _cachedChunk) return;
        _cachedChunk = -1;

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
            _plainBuf.AsSpan(0, plainLen));

        _cachedChunk = chunk;
        _cachedLen = plainLen;
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
                    _cachedChunk = -1;
                    CryptographicOperations.ZeroMemory(_plainBuf);
                    _aes.Dispose();
                    _file.Dispose(); // releases the file handle
                }
            }
        }
        base.Dispose(disposing);
    }
}
