using System.Text;

namespace EchoDeck;

/// <summary>
/// Collects digits typed while Ctrl is held and reports when the last N digits match the passcode.
/// Deliberately tiny: it only appends to a buffer and compares, so it is safe to call from a key hook.
/// </summary>
internal sealed class PinDetector
{
    public const int MaxLength = 8;

    private readonly Func<string, bool> _verify;
    private readonly Func<int> _length;
    private readonly StringBuilder _buffer = new();
    private DateTime _lastDigitUtc = DateTime.MinValue;

    public PinDetector(Func<string, bool> verify, Func<int> length)
    {
        _verify = verify;
        _length = length;
    }

    public static bool TryGetDigit(int vk, out char digit)
    {
        if (vk is >= 0x30 and <= 0x39) { digit = (char)vk; return true; }                  // top row
        if (vk is >= 0x60 and <= 0x69) { digit = (char)('0' + (vk - 0x60)); return true; } // numpad
        digit = '\0';
        return false;
    }

    public bool Push(char digit)
    {
        if ((DateTime.UtcNow - _lastDigitUtc).TotalSeconds > 3) _buffer.Clear();
        _lastDigitUtc = DateTime.UtcNow;

        _buffer.Append(digit);
        if (_buffer.Length > MaxLength) _buffer.Remove(0, _buffer.Length - MaxLength);

        int len = _length();
        if (len < PinStore.MinLength || _buffer.Length < len) return false;

        var candidate = _buffer.ToString(_buffer.Length - len, len);
        if (!_verify(candidate)) return false;

        _buffer.Clear();
        return true;
    }

    public void Reset() => _buffer.Clear();
}
