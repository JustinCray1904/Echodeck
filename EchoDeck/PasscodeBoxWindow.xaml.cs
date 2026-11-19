using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace EchoDeck;

/// <summary>
/// The box shown in the middle of Tempo while the Anonymous Mode passcode is being typed (Ctrl + the digits): one dot per digit of the
/// passcode, filled as they are typed. It never takes focus and clicks go through it, so typing carries on in the main window.
/// </summary>
public partial class PasscodeBoxWindow : Window
{
    private readonly List<Ellipse> _dots = new();

    public PasscodeBoxWindow(int length)
    {
        InitializeComponent();
        for (int i = 0; i < Math.Clamp(length, 1, PinDetector.MaxLength); i++)
        {
            var dot = new Ellipse { Width = 16, Height = 16, Margin = new Thickness(7, 0, 7, 0), Fill = Res("SlateBrush") };
            _dots.Add(dot);
            Dots.Children.Add(dot);
        }

        Opacity = 0;
        Loaded += (_, _) => BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    /// <summary>The first <paramref name="filled"/> dots are filled.</summary>
    public void SetFilled(int filled)
    {
        Hint.Text = "Keep typing your passcode";
        Hint.Foreground = Res("MutedBrush");
        for (int i = 0; i < _dots.Count; i++)
            _dots[i].Fill = i < filled ? Res("AccentBrush") : Res("SlateBrush");
    }

    /// <summary>A full-length try that was not the passcode.</summary>
    public void ShowWrong()
    {
        Hint.Text = "That is not the passcode";
        Hint.Foreground = Res("DangerBrush");
        foreach (var dot in _dots) dot.Fill = Res("DangerBrush");
    }
}
