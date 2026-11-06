using System.Windows;

namespace EchoDeck;

/// <summary>Small modal that asks for a line of text (playlist names).</summary>
public partial class TextPromptWindow : Window
{
    public string Value => InputBox.Text.Trim();

    public TextPromptWindow(string title, string initial = "", string okText = "OK")
    {
        InitializeComponent();
        WindowHelper.AttachChrome(this);
        PromptTitle.Text = title;
        InputBox.Text = initial;
        OkButton.Content = okText;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Value.Length == 0) return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
