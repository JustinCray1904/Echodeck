using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EchoDeck;

/// <summary>The window behind <see cref="TempoDialog"/>: an icon, the message and the buttons, in Tempo's style.</summary>
public partial class TempoDialogWindow : Window
{
    private readonly MessageBoxResult _cancelResult;

    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    public TempoDialogWindow(string text, string caption, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult)
    {
        InitializeComponent();
        WindowHelper.AttachChrome(this);

        Title = caption;
        Body.Text = text;

        switch (image)
        {
            case MessageBoxImage.Error:
                IconGlyph.Text = "×";
                IconDisc.Background = (Brush)FindResource("DangerBrush");
                IconGlyph.Foreground = Brushes.White;
                break;
            case MessageBoxImage.Warning:
                IconGlyph.Text = "!";
                IconDisc.Background = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
                IconGlyph.Foreground = new SolidColorBrush(Color.FromRgb(0x2A, 0x1C, 0x04));
                break;
            case MessageBoxImage.Question:
                IconGlyph.Text = "?";
                break;
            case MessageBoxImage.Information:
                IconGlyph.Text = "i";
                break;
            default:
                IconDisc.Visibility = Visibility.Collapsed;
                break;
        }

        // The button order is: the way out on the left, the main choice (accent coloured, answers to Enter) on the right.
        switch (buttons)
        {
            case MessageBoxButton.OK:
                _cancelResult = MessageBoxResult.OK;
                AddButton("OK", MessageBoxResult.OK, main: true);
                break;
            case MessageBoxButton.OKCancel:
                _cancelResult = MessageBoxResult.Cancel;
                AddButton("Cancel", MessageBoxResult.Cancel, main: defaultResult == MessageBoxResult.Cancel);
                AddButton("OK", MessageBoxResult.OK, main: defaultResult != MessageBoxResult.Cancel);
                break;
            case MessageBoxButton.YesNo:
                _cancelResult = MessageBoxResult.No;
                AddButton("No", MessageBoxResult.No, main: defaultResult == MessageBoxResult.No);
                AddButton("Yes", MessageBoxResult.Yes, main: defaultResult != MessageBoxResult.No);
                break;
            default:   // YesNoCancel
                _cancelResult = MessageBoxResult.Cancel;
                AddButton("Cancel", MessageBoxResult.Cancel, main: defaultResult == MessageBoxResult.Cancel);
                AddButton("No", MessageBoxResult.No, main: defaultResult == MessageBoxResult.No);
                AddButton("Yes", MessageBoxResult.Yes, main: defaultResult != MessageBoxResult.Cancel && defaultResult != MessageBoxResult.No);
                break;
        }

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Result = _cancelResult;
            e.Handled = true;
            Close();
        };

        // the X in the corner (or Alt+F4) answers like Esc
        Closing += (_, _) =>
        {
            if (Result == MessageBoxResult.None) Result = _cancelResult;
        };
    }

    private void AddButton(string label, MessageBoxResult result, bool main)
    {
        var button = new Button
        {
            Content = label,
            MinWidth = 96,
            Margin = new Thickness(ButtonRow.Children.Count == 0 ? 0 : 8, 0, 0, 0),
            Padding = new Thickness(18, 9, 18, 9),
            IsDefault = main,
            Style = (Style)FindResource(main ? "AccentButton" : "FlatButton"),
        };
        button.Click += (_, _) =>
        {
            Result = result;
            Close();
        };
        ButtonRow.Children.Add(button);
    }
}
