using System;
using EchoDeck;
using Xunit;

namespace EchoDeck.Tests;

/// <summary>The passcode in progress: digits, Backspace for a mistake, and what a try is compared with.</summary>
public class PinDetectorTests
{
    private static PinDetector For(string pin) => new(candidate => candidate == pin, () => pin.Length);

    private static bool Type(PinDetector detector, string digits)
    {
        bool matched = false;
        foreach (char c in digits) matched = detector.Push(c);
        return matched;
    }

    [Fact]
    public void TheRightDigitsMatch()
    {
        Assert.True(Type(For("1234"), "1234"));
    }

    [Fact]
    public void BackspaceErasesTheLastDigitSoAMistakeCanBeCorrected()
    {
        var detector = For("1234");
        Assert.False(Type(detector, "129"));   // a mistake on the third digit
        Assert.Equal(3, detector.Count);

        detector.Backspace();
        detector.Backspace();                   // back to "1"
        Assert.Equal(1, detector.Count);
        Assert.True(Type(detector, "234"));
    }

    [Fact]
    public void BackspaceOnAnEmptyTryDoesNothing()
    {
        var detector = For("1234");
        detector.Backspace();
        Assert.Equal(0, detector.Count);
        Assert.True(Type(detector, "1234"));
    }

    [Fact]
    public void ResetClearsEverythingTyped()
    {
        var detector = For("1234");
        Type(detector, "12");
        detector.Reset();
        Assert.Equal(0, detector.Count);
    }

    [Fact]
    public void ADigitAfterALongPauseStartsOver()
    {
        var detector = For("1234");
        detector.IdleSeconds = 0;               // any pause counts as long
        Type(detector, "1");
        System.Threading.Thread.Sleep(20);
        detector.Push('2');
        Assert.Equal(1, detector.Count);        // the "1" was forgotten
    }
}
