using System.Text;
using System;
using System.IO;

namespace DeefxultMediaPlayer;

/// <summary>Collects digits typed while Ctrl is held and reports when the last four match the PIN.</summary>
internal sealed class PinDetector
{
    public const int PinLength = 4;

    private readonly Func<string, bool> _verify;
    private readonly StringBuilder _buffer = new();
    private DateTime _lastDigitUtc = DateTime.MinValue;

    public PinDetector(Func<string, bool> verify) => _verify = verify;

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
        if (_buffer.Length > PinLength) _buffer.Remove(0, _buffer.Length - PinLength);

        if (_buffer.Length == PinLength && _verify(_buffer.ToString()))
        {
            _buffer.Clear();
            return true;
        }
        return false;
    }

    public void Reset() => _buffer.Clear();
}
