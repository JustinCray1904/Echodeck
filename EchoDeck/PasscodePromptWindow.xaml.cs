using System.Windows;
using System.Windows.Input;

namespace EchoDeck;

/// <summary>Small modal that asks for the Anonymous Mode passcode (used by the app menu entry).</summary>
public partial class PasscodePromptWindow : Window
{
    private int _attempts;

    public PasscodePromptWindow()
    {
        InitializeComponent();
        WindowHelper.AttachChrome(this);
        Loaded += (_, _) => PinBox.Focus();
    }

    private void Digits_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(c => c is >= '0' and <= '9');

    private void Enter_Click(object sender, RoutedEventArgs e)
    {
        if (PinStore.Verify(PinBox.Password))
        {
            DialogResult = true;
            return;
        }

        _attempts++;
        PinBox.Clear();
        ErrorText.Text = _attempts >= 3 ? "That's not it. Check the number of digits too." : "Wrong passcode.";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
