using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace EchoDeck;

/// <summary>
/// The music-mode seek bar from the Figma "Player - Music" frame: the song's waveform drawn as bars, the played part in the
/// accent colour. Click or drag anywhere to seek.
/// </summary>
public sealed class WaveformBar : FrameworkElement
{
    private float[]? _peaks;
    private double _progress;
    private bool _dragging;

    /// <summary>Raised while dragging (fraction 0..1) so the time label can follow.</summary>
    public event Action<double>? Scrubbing;

    /// <summary>Raised when the mouse is released: seek here (fraction 0..1).</summary>
    public event Action<double>? SeekCommitted;

    public bool IsDragging => _dragging;

    /// <summary>True = the plain seek bar used for video (track, fill, handle) instead of waveform bars.</summary>
    public bool Flat { get; set; }

    public float[]? Peaks
    {
        get => _peaks;
        set { _peaks = value; InvalidateVisual(); }
    }

    public double Progress
    {
        get => _progress;
        set
        {
            double v = Math.Clamp(value, 0, 1);
            if (Math.Abs(v - _progress) < 0.0005) return;
            _progress = v;
            InvalidateVisual();
        }
    }

    public WaveformBar()
    {
        Cursor = Cursors.Hand;
        Focusable = false;
        SnapsToDevicePixels = true;
    }

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 6) return;

        var track = Res("TrackBrush", Brushes.Black);
        var played = Res("AccentBrush", Brushes.DeepSkyBlue);
        var rest = Res("AccentMidBrush", Brushes.SlateGray);

        dc.DrawRoundedRectangle(track, null, new Rect(0, 0, w, h), 6, 6);

        if (Flat)
        {
            double fillW = Math.Max(0, (w - 4) * _progress);
            if (fillW > 0)
                dc.DrawRoundedRectangle(rest, null, new Rect(2, 2, fillW, h - 4), 4, 4);
            double thumbX = Math.Clamp(2 + fillW - 4, 2, w - 10);
            dc.DrawRoundedRectangle(played, null, new Rect(thumbX, 2, 8, h - 4), 4, 4);
            return;
        }

        const double pitch = 4.0, barW = 2.4, pad = 8.0;
        int count = Math.Max(1, (int)((w - pad * 2) / pitch));
        var peaks = _peaks;

        for (int i = 0; i < count; i++)
        {
            double frac = (i + 0.5) / count;
            double peak = 0.1;

            if (peaks != null && peaks.Length > 0)
            {
                int from = (int)Math.Floor(i * (double)peaks.Length / count);
                int to = Math.Max(from + 1, (int)Math.Floor((i + 1) * (double)peaks.Length / count));
                float max = 0;
                for (int k = from; k < Math.Min(to, peaks.Length); k++) max = Math.Max(max, peaks[k]);
                peak = Math.Max(0.06, max);
            }

            double barH = Math.Max(3, peak * (h - 10));
            double x = pad + i * pitch;
            dc.DrawRoundedRectangle(frac <= _progress ? played : rest, null,
                new Rect(x, (h - barH) / 2, barW, barH), 1.2, 1.2);
        }
    }

    private double FractionAt(MouseEventArgs e)
    {
        double w = Math.Max(1, ActualWidth);
        return Math.Clamp(e.GetPosition(this).X / w, 0, 1);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragging = true;
        CaptureMouse();
        Progress = FractionAt(e);
        Scrubbing?.Invoke(_progress);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        Progress = FractionAt(e);
        Scrubbing?.Invoke(_progress);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        Progress = FractionAt(e);
        SeekCommitted?.Invoke(_progress);
    }
}
