using System.Windows;
using System.Windows.Media.Animation;

namespace EchoDeck;

/// <summary>Full-screen logo splash shown on the very first launch, before the onboarding tour.</summary>
public partial class StartupSplashWindow : Window
{
    public StartupSplashWindow()
    {
        InitializeComponent();

        // Cover the primary screen completely, so the main window loading behind it is never seen half-built.
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        Loaded += (_, _) => PlayIn();
    }

    private void PlayIn()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = ease });
        var grow = new DoubleAnimation(0.88, 1.0, TimeSpan.FromMilliseconds(760)) { EasingFunction = ease };
        LogoScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, grow);
        LogoScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, grow.Clone());

        var sweep = new DoubleAnimation(-44, 120, TimeSpan.FromMilliseconds(1100)) { RepeatBehavior = RepeatBehavior.Forever };
        SweepMove.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, sweep);
    }

    /// <summary>Fades the whole window out (resolves when the animation is done).</summary>
    public Task FadeOutAsync()
    {
        var tcs = new TaskCompletionSource();
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(420));
        fade.Completed += (_, _) => tcs.TrySetResult();
        BeginAnimation(OpacityProperty, fade);
        return tcs.Task;
    }
}
