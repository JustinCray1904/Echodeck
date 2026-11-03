using System.Windows;
using System.Windows.Media;

namespace EchoDeck;

/// <summary>
/// The little "now playing" bars in the queue and playlist rows. They only move while the song is really playing
/// (<see cref="IsActive"/>); when it is paused or stopped they stand still.
/// </summary>
public sealed class EqualizerBars : FrameworkElement
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(EqualizerBars),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((EqualizerBars)d).Sync()));

    private static readonly double[] Phase = { 0.0, 1.9, 3.7 };
    private static readonly double[] Speed = { 5.1, 6.7, 4.3 };
    private static readonly double[] Still = { 0.55, 0.9, 0.38 };
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private bool _subscribed;

    public EqualizerBars()
    {
        Loaded += (_, _) => Sync();
        Unloaded += (_, _) => Unsubscribe();
        IsVisibleChanged += (_, _) => Sync();
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private void Sync()
    {
        if (IsActive && IsLoaded && IsVisible) Subscribe(); else Unsubscribe();
        InvalidateVisual();
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var brush = TryFindResource("AccentBrush") as Brush ?? Brushes.White;
        double barW = w / 5.0;
        double t = _clock.Elapsed.TotalSeconds;

        for (int i = 0; i < 3; i++)
        {
            double f = IsActive ? 0.25 + 0.75 * Math.Abs(Math.Sin(t * Speed[i] + Phase[i])) : Still[i];
            double bh = Math.Max(2, h * f);
            double x = barW * (1 + i * 1.5) - barW * 0.5;
            dc.DrawRoundedRectangle(brush, null, new Rect(x, h - bh, barW, bh), 1.2, 1.2);
        }
    }
}
