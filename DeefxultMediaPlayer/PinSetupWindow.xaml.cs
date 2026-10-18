using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace DeefxultMediaPlayer;

/// <summary>First-run screen that lets the user choose their own 4-digit PIN instead of keeping the default.</summary>
public partial class PinSetupWindow : Window
{
    private bool _saved;

    public PinSetupWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PinBox.Focus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.ApplyDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    private void Digits_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(c => c is >= '0' and <= '9');

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var pin = PinBox.Password;

        if (pin.Length != PinDetector.PinLength || !pin.All(c => c is >= '0' and <= '9'))
        {
            ErrorText.Text = "Enter exactly 4 digits.";
            return;
        }
        if (pin != ConfirmBox.Password)
        {
            ErrorText.Text = "The two PINs don't match.";
            return;
        }

        try
        {
            PinStore.Set(pin);
            _saved = true;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ErrorText.Text = "Couldn't save the PIN: " + ex.Message;
        }
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    protected override void OnClosed(EventArgs e)
    {
        // Skipped, cancelled or closed with the X: don't nag on every launch (the vault still reminds the user).
        if (!_saved) PinStore.MarkSetupSkipped();
        base.OnClosed(e);
    }
}
