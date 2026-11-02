using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace EchoDeck;

/// <summary>The loading bar (library scans, Anonymous imports) and the small toast for messages shown over the library.</summary>
public partial class MainWindow
{
    private bool _loadSliding;
    private bool _importBusy;   // an Anonymous import / restore owns the bar: scan progress must not touch it
    private DispatcherTimer? _toastTimer;

    // ------------------------------------------------------------------ loading bar

    /// <summary>Shows the bar. With a fraction it fills up (determinate), without one it slides (indeterminate).</summary>
    private void ShowLoading(string text, string count = "", double? fraction = null)
    {
        LoadText.Text = text;
        LoadCount.Text = count;

        if (LoadPanel.Visibility != Visibility.Visible)
        {
            LoadPanel.Visibility = Visibility.Visible;
            LoadPanel.UpdateLayout();   // the track needs a real width before the fill can be sized
        }

        if (fraction.HasValue)
        {
            StopLoadSlide();
            LoadFill.Width = Math.Max(0, LoadTrack.ActualWidth) * Math.Clamp(fraction.Value, 0, 1);
        }
        else
        {
            StartLoadSlide();
        }
    }

    private void HideLoading()
    {
        StopLoadSlide();
        LoadFill.Width = 0;
        LoadPanel.Visibility = Visibility.Collapsed;
    }

    private void StartLoadSlide()
    {
        if (_loadSliding) return;
        _loadSliding = true;

        double track = Math.Max(160, LoadTrack.ActualWidth);
        double bar = track * 0.28;
        LoadFill.Width = bar;
        LoadSlide.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-bar, track, TimeSpan.FromMilliseconds(1200)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private void StopLoadSlide()
    {
        if (!_loadSliding) return;
        _loadSliding = false;
        LoadSlide.BeginAnimation(TranslateTransform.XProperty, null);
        LoadSlide.X = 0;
    }

    /// <summary>Scan progress from the background scanner (already on the UI thread).</summary>
    private void OnScanProgress(ScanProgress p)
    {
        if (_importBusy) return;

        bool onThatPage = p.Music ? _mode == LibMode.Music : _mode == LibMode.Videos;
        if (p.Finished)
        {
            if (onThatPage) HideLoading();
            return;
        }
        if (!onThatPage) return;

        if (p.Indeterminate || p.Total <= 0) ShowLoading(p.Text);
        else ShowLoading(p.Text, p.Done.ToString("N0") + " / " + p.Total.ToString("N0"), (double)p.Done / p.Total);
    }

    // ------------------------------------------------------------------ toast (messages while the library is showing)

    /// <summary>A short message: over the video while one is showing, as a toast over the library otherwise.</summary>
    private void Notify(string text)
    {
        if (!_libraryVisible)
        {
            ShowOsd(text);
            return;
        }

        ToastText.Text = text;
        ToastBox.Visibility = Visibility.Visible;
        if (_toastTimer == null)
        {
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer!.Stop();
                ToastBox.Visibility = Visibility.Collapsed;
            };
        }
        _toastTimer.Stop();
        _toastTimer.Start();
    }
}
