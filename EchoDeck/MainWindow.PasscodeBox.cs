using System.Windows;
using System.Windows.Threading;

namespace EchoDeck;

// The box in the middle of Tempo while the Anonymous Mode passcode is typed. The digits themselves are still caught by the keyboard
// hook (Ctrl + digits to start, then with or without Ctrl), so the box never needs focus. It shows how many digits have been typed, with
// the window dimmed behind it; Backspace erases the last digit, Delete clears them all and Esc closes it.
public partial class MainWindow
{
    private PasscodeBoxWindow? _codeBox;
    private DialogScrim? _codeScrim;
    private DispatcherTimer? _codeTimer;

    /// <summary>Called after every digit: shows the box (the first time), fills its dots, and sets when it goes away if typing stops.</summary>
    private void ShowPasscodeProgress()
    {
        if (!_cfg.AnonymousPasscodeBox || _anon || !PinStore.IsSet) return;

        try
        {
            if (_codeBox == null)
            {
                if (!IsVisible || WindowState == WindowState.Minimized) return;
                _codeScrim = new DialogScrim(this, 130);
                _codeScrim.Show();
                _codeBox = new PasscodeBoxWindow(PinStore.Length) { Owner = this };
                _codeBox.Show();
            }

            int length = PinStore.Length;
            _codeBox.SetFilled(Math.Min(_pinDetector.Count, length));

            if (_pinDetector.Count >= length)
            {
                // a full-length try that did not match (a match never gets here: the box is closed first)
                _codeBox.ShowWrong();
                SchedulePasscodeBoxHide(700);
            }
            else SchedulePasscodeBoxHide(8200);   // typing stopped: the half-typed passcode is forgotten
        }
        catch (Exception ex)
        {
            AppLog.Write("Passcode box", ex);
            HidePasscodeBox();
        }
    }

    private void SchedulePasscodeBoxHide(int milliseconds)
    {
        _codeTimer?.Stop();
        _codeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        _codeTimer.Tick += (_, _) =>
        {
            _pinDetector.Reset();
            HidePasscodeBox();
        };
        _codeTimer.Start();
    }

    /// <summary>Closes the box and the dimming. Safe to call when nothing is shown.</summary>
    private void HidePasscodeBox()
    {
        _codeTimer?.Stop();
        _codeTimer = null;
        if (_codeBox == null && _codeScrim == null) return;

        try { _codeBox?.Close(); } catch { /* already closed */ }
        try { _codeScrim?.Close(); } catch { /* already closed */ }
        _codeBox = null;
        _codeScrim = null;
    }
}
