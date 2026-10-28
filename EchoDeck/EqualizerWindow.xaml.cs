using System.Windows;
using System.Windows.Controls;

namespace EchoDeck;

/// <summary>Ten-band equaliser with presets (applies live to whatever is playing).</summary>
public partial class EqualizerWindow : Window
{
    public static readonly string[] BandLabels = { "60 Hz", "170 Hz", "310 Hz", "600 Hz", "1 kHz", "3 kHz", "6 kHz", "12 kHz", "14 kHz", "16 kHz" };

    private static readonly (string Name, float[] Gains)[] Presets =
    {
        ("Flat", new float[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
        ("Classical", new float[] { 0, 0, 0, 0, 0, 0, -7, -7, -7, -9 }),
        ("Club", new float[] { 0, 0, 8, 6, 6, 6, 3, 0, 0, 0 }),
        ("Dance", new float[] { 10, 7, 2, 0, 0, -6, -7, -7, 0, 0 }),
        ("Full bass", new float[] { -8, 10, 10, 6, 2, -4, -8, -10, -11, -11 }),
        ("Full bass and treble", new float[] { 7, 6, 0, -7, -5, 2, 8, 11, 12, 12 }),
        ("Full treble", new float[] { -10, -10, -10, -4, 2, 11, 12, 12, 12, 12 }),
        ("Headphones", new float[] { 5, 11, 6, -3, -2, 2, 5, 10, 12, 12 }),
        ("Large hall", new float[] { 10, 10, 6, 6, 0, -5, -5, -5, 0, 0 }),
        ("Live", new float[] { -5, 0, 4, 6, 6, 6, 4, 2, 2, 2 }),
        ("Party", new float[] { 7, 7, 0, 0, 0, 0, 0, 0, 7, 7 }),
        ("Pop", new float[] { -2, 5, 7, 8, 6, 0, -2, -2, -2, -2 }),
        ("Reggae", new float[] { 0, 0, 0, -6, 0, 6, 6, 0, 0, 0 }),
        ("Rock", new float[] { 8, 5, -6, -8, -3, 4, 9, 11, 11, 11 }),
        ("Ska", new float[] { -2, -5, -4, 0, 4, 6, 9, 10, 11, 10 }),
        ("Soft", new float[] { 5, 2, 0, -2, 0, 4, 8, 10, 11, 12 }),
        ("Soft rock", new float[] { 4, 4, 2, 0, -4, -6, -3, 0, 2, 9 }),
        ("Techno", new float[] { 8, 6, 0, -6, -5, 0, 8, 10, 10, 8 })
    };

    // remembered while the app runs
    public static bool Enabled { get; private set; }
    public static float[] Gains { get; } = new float[10];
    private static int _presetIndex;

    private readonly Action<bool, float[]> _apply;
    private readonly Slider[] _bands = new Slider[10];
    private bool _loading = true;

    public EqualizerWindow(Action<bool, float[]> apply)
    {
        InitializeComponent();
        WindowHelper.AttachChrome(this);
        _apply = apply;

        var style = (Style)FindResource("VSlider");
        for (int i = 0; i < 10; i++)
        {
            int band = i;
            var slider = new Slider { Style = style, Minimum = -12, Maximum = 12, Value = Gains[i], IsMoveToPointEnabled = true };
            slider.ValueChanged += (_, e) =>
            {
                if (_loading) return;
                Gains[band] = (float)Math.Round(e.NewValue, 1);
                if (PresetCombo.SelectedIndex >= 0) { _loading = true; PresetCombo.SelectedIndex = -1; _loading = false; }
                Push();
            };
            _bands[i] = slider;

            var column = new StackPanel { Width = 52 };
            column.Children.Add(slider);
            column.Children.Add(new TextBlock
            {
                Text = BandLabels[i],
                FontSize = 11,
                Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            ((TextBlock)column.Children[1]).SetResourceReference(TextBlock.ForegroundProperty, "Fg1Brush");
            BandsPanel.Children.Add(column);
        }

        foreach (var p in Presets) PresetCombo.Items.Add(p.Name);
        PresetCombo.SelectedIndex = _presetIndex >= 0 && _presetIndex < Presets.Length && Matches(_presetIndex) ? _presetIndex : -1;

        EnableSwitch.IsChecked = Enabled;
        UpdateState();
        _loading = false;
    }

    private static bool Matches(int preset)
    {
        for (int i = 0; i < 10; i++)
            if (Math.Abs(Presets[preset].Gains[i] - Gains[i]) > 0.05f) return false;
        return true;
    }

    private void UpdateState()
    {
        StateText.Text = EnableSwitch.IsChecked == true ? "On" : "Off";
        foreach (var b in _bands) b.Opacity = EnableSwitch.IsChecked == true ? 1.0 : 0.5;
    }

    private void Push() => _apply(Enabled, Gains);

    private void Enable_Click(object sender, RoutedEventArgs e)
    {
        Enabled = EnableSwitch.IsChecked == true;
        UpdateState();
        Push();
    }

    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PresetCombo.SelectedIndex < 0) return;
        _presetIndex = PresetCombo.SelectedIndex;

        _loading = true;
        for (int i = 0; i < 10; i++)
        {
            Gains[i] = Presets[_presetIndex].Gains[i];
            _bands[i].Value = Gains[i];
        }
        // choosing a preset switches the equaliser on (it would otherwise change nothing audible)
        Enabled = true;
        EnableSwitch.IsChecked = true;
        UpdateState();
        _loading = false;
        Push();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
