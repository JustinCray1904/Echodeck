using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EchoDeck;

/// <summary>
/// Audio Mod: presets (Sped Up, Nightcore, Slowed &amp; Reverb ...) and sliders for speed, pitch, distortion, reverb, echo and low / high
/// pass. Apply makes a modded copy of the playing song (see <see cref="AudioModRenderer"/>) and the main window plays it from the same
/// moment; Reset plays the original again.
/// </summary>
public partial class AudioModWindow : Window
{
    private readonly Func<AudioModSettings, IProgress<double>, CancellationToken, Task<bool>> _apply;
    private readonly Func<bool> _revert;
    private readonly Func<bool> _isModded;
    private readonly Action<AudioModSettings> _remember;
    private readonly List<(Border Card, AudioModPreset Preset)> _cards = new();
    private bool _loading;
    private bool _busy;
    private CancellationTokenSource? _cts;

    internal AudioModWindow(string songName, AudioModSettings start,
        Func<AudioModSettings, IProgress<double>, CancellationToken, Task<bool>> apply,
        Func<bool> revert, Func<bool> isModded, Action<AudioModSettings> remember)
    {
        InitializeComponent();
        _apply = apply;
        _revert = revert;
        _isModded = isModded;
        _remember = remember;

        SongText.Text = songName;
        BuildPresetCards();
        ShowSettings(start);
        StatusText.Text = _isModded() ? "This song is playing with Audio Mod." : "Pick a preset or move the sliders, then press Apply.";
        Closing += (_, _) => _cts?.Cancel();
    }

    // ------------------------------------------------------------------ presets

    private void BuildPresetCards()
    {
        foreach (var preset in AudioModPresets.All)
        {
            var from = (Color)ColorConverter.ConvertFromString(preset.ColorFrom);
            var to = (Color)ColorConverter.ConvertFromString(preset.ColorTo);
            var card = new Border
            {
                Width = 112,
                Height = 64,
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(0, 0, 8, 8),
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Background = new LinearGradientBrush(from, to, 45),
                Child = new TextBlock
                {
                    Text = preset.Name,
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6)
                }
            };
            var chosen = preset;
            card.MouseLeftButtonUp += (_, _) =>
            {
                if (_busy) return;
                ShowSettings(chosen.Settings);
                StatusText.Text = chosen.Name + ": press Apply to hear it.";
            };
            _cards.Add((card, preset));
            PresetPanel.Children.Add(card);
        }
    }

    private void HighlightPreset(AudioModSettings settings)
    {
        string key = settings.Key();
        foreach (var (card, preset) in _cards)
            card.BorderBrush = preset.Settings.Key() == key ? (Brush)FindResource("AccentBrush") : Brushes.Transparent;
    }

    // ------------------------------------------------------------------ controls <-> settings

    private AudioModSettings ReadSettings() => new AudioModSettings
    {
        SpeedPercent = (int)Math.Round(SpeedSlider.Value),
        PitchSemitones = (int)Math.Round(PitchSlider.Value),
        Distort = DistortSwitch.IsChecked == true,
        ReverbOn = ReverbSwitch.IsChecked == true,
        Reverb = (int)Math.Round(ReverbSlider.Value),
        DelayOn = DelaySwitch.IsChecked == true,
        DelayTime = (int)Math.Round(DelayTimeSlider.Value),
        DelayIntensity = (int)Math.Round(DelayIntensitySlider.Value),
        LowPass = (int)Math.Round(LowPassSlider.Value),
        HighPass = (int)Math.Round(HighPassSlider.Value),
    }.Clamp();

    private void ShowSettings(AudioModSettings settings)
    {
        var s = settings.Clamp();
        _loading = true;
        SpeedSlider.Value = s.SpeedPercent;
        PitchSlider.Value = s.PitchSemitones;
        DistortSwitch.IsChecked = s.Distort;
        ReverbSwitch.IsChecked = s.ReverbOn;
        ReverbSlider.Value = s.Reverb;
        DelaySwitch.IsChecked = s.DelayOn;
        DelayTimeSlider.Value = s.DelayTime;
        DelayIntensitySlider.Value = s.DelayIntensity;
        LowPassSlider.Value = s.LowPass;
        HighPassSlider.Value = s.HighPass;
        _loading = false;
        Refresh();
    }

    /// <summary>The numbers next to the sliders, which sliders can be used, and the highlighted preset.</summary>
    private void Refresh()
    {
        var s = ReadSettings();
        SpeedValue.Text = s.SpeedPercent.ToString(CultureInfo.InvariantCulture) + "%";
        PitchValue.Text = (s.PitchSemitones > 0 ? "+" : "") + s.PitchSemitones.ToString(CultureInfo.InvariantCulture);
        ReverbValue.Text = s.ReverbOn ? s.Reverb.ToString(CultureInfo.InvariantCulture) : "";
        DelayTimeValue.Text = s.DelayTime.ToString(CultureInfo.InvariantCulture);
        DelayIntensityValue.Text = s.DelayIntensity.ToString(CultureInfo.InvariantCulture);
        LowPassValue.Text = s.LowPass.ToString(CultureInfo.InvariantCulture);
        HighPassValue.Text = s.HighPass.ToString(CultureInfo.InvariantCulture);

        ReverbSlider.IsEnabled = s.ReverbOn && !_busy;
        ReverbSlider.Opacity = s.ReverbOn ? 1 : 0.4;
        DelayTimeSlider.IsEnabled = DelayIntensitySlider.IsEnabled = s.DelayOn && !_busy;
        DelayTimeSlider.Opacity = DelayIntensitySlider.Opacity = s.DelayOn ? 1 : 0.4;
        HighlightPreset(s);
    }

    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsLoaded) return;
        Refresh();
    }

    private void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Refresh();
    }

    // ------------------------------------------------------------------ apply / reset / close

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ApplyButton.IsEnabled = !busy;
        SpeedSlider.IsEnabled = PitchSlider.IsEnabled = LowPassSlider.IsEnabled = HighPassSlider.IsEnabled = !busy;
        DistortSwitch.IsEnabled = ReverbSwitch.IsEnabled = DelaySwitch.IsEnabled = !busy;
        if (!busy) RenderBar.Value = 0;
        Refresh();
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var settings = ReadSettings();
        SetBusy(true);
        StatusText.Text = settings.IsNeutral ? "Going back to the original…" : "Making the modded version of this song…";
        _cts = new CancellationTokenSource();
        var progress = new Progress<double>(p => RenderBar.Value = p * 100);   // reports on this window's thread
        try
        {
            bool done = await _apply(settings, progress, _cts.Token);
            if (done)
            {
                _remember(settings);
                var name = AudioModPresets.Match(settings)?.Name;
                StatusText.Text = settings.IsNeutral ? "Playing the original." : "Playing with Audio Mod" + (name != null ? ": " + name : "") + ".";
            }
            else
            {
                StatusText.Text = "Nothing was changed.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled: the song plays as it was.";
        }
        catch (InvalidOperationException ex)
        {
            StatusText.Text = ex.Message;   // a sentence made for the user (too long, can't be read ...)
        }
        catch (Exception ex)
        {
            AppLog.Write("Audio Mod", ex, "making the modded copy failed");
            StatusText.Text = "Something went wrong, so the song plays as it was.";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();   // a copy that is being made is dropped
        ShowSettings(new AudioModSettings());
        StatusText.Text = _busy ? "Cancelling…" : (_revert() ? "Back to the original." : "Everything is back to normal.");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
