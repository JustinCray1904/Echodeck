using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace EchoDeck;

/// <summary>The full settings page for the player (layout follows the Figma "Settings" frames).</summary>
public partial class SettingsWindow : Window
{
    private static readonly double[] Speeds = { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 };
    private static readonly int[] SkipValues = { 5, 10, 15, 30 };
    private static readonly string[] Aspects = { "Default", "16:9", "4:3", "1:1", "21:9", "16:10" };

    private readonly AppSettings _cfg = AppSettings.Current;
    private readonly Dictionary<RadioButton, UIElement> _pages = new();
    private readonly List<RadioButton> _navItems = new();
    private bool _loading = true;

    public SettingsWindow(string? page = null)
    {
        InitializeComponent();
        WindowHelper.AttachChrome(this);

        _pages[NavGeneral] = PageGeneral;
        _pages[NavInterface] = PageInterface;
        _pages[NavPrivacy] = PagePrivacy;
        _pages[NavControls] = PageControls;
        _pages[NavPlayback] = PagePlayback;
        _pages[NavVideo] = PageVideo;
        _pages[NavAudio] = PageAudio;
        _pages[NavSubtitles] = PageSubtitles;
        _pages[NavLibrary] = PageLibrary;
        _pages[NavAbout] = PageAbout;
        _navItems.AddRange(_pages.Keys);

        LoadValues();
        _loading = false;

        if (page == "privacy") NavPrivacy.IsChecked = true;
        else if (page == "controls") NavControls.IsChecked = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        PrivCurrentBox.Clear();
        PrivNewBox.Clear();
        PrivConfirmBox.Clear();
        base.OnClosed(e);
    }

    // ------------------------------------------------------------------ loading

    private void LoadValues()
    {
        ThemeCombo.SelectedIndex = (int)_cfg.Theme;
        AccentCombo.SelectedIndex = Math.Clamp(_cfg.AccentIndex, 0, ThemeManager.Accents.Length - 1);

        SidebarSwitch.IsChecked = _cfg.ShowSidebarAtStart;
        MenuSwitch.IsChecked = _cfg.ShowAnonymousInMenu;
        MinimizeSwitch.IsChecked = _cfg.AnonymousExitOnMinimize;
        AutoAdvanceSwitch.IsChecked = _cfg.AutoAdvance;
        FullscreenSwitch.IsChecked = _cfg.StartFullscreen;
        AwakeSwitch.IsChecked = _cfg.KeepAwake;
        HwSwitch.IsChecked = _cfg.HardwareDecoding;
        SubsOnSwitch.IsChecked = _cfg.SubtitlesOnByDefault;

        LoopCombo.SelectedIndex = (int)_cfg.Loop;
        SpeedCombo.SelectedIndex = NearestIndex(Speeds, _cfg.DefaultSpeed);
        SkipCombo.SelectedIndex = Math.Max(0, Array.IndexOf(SkipValues, _cfg.SkipSeconds));
        AspectCombo.SelectedIndex = Math.Max(0, Array.IndexOf(Aspects, _cfg.AspectRatio));
        SubSizeCombo.SelectedIndex = Math.Clamp(_cfg.SubtitleSize, 0, 3);

        HueSlider.Value = _cfg.Hue;
        BrightnessSlider.Value = _cfg.Brightness;
        ContrastSlider.Value = _cfg.Contrast;
        SaturationSlider.Value = _cfg.Saturation;
        GammaSlider.Value = _cfg.Gamma;
        VolumeSlider.Value = _cfg.Volume;
        UpdateSliderLabels();

        FolderBox.Text = _cfg.MediaFolder;
        UpdatePasscodeForm();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = "Version " + (version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0");
    }

    private static int NearestIndex(double[] values, double v)
    {
        int best = 0;
        for (int i = 1; i < values.Length; i++)
            if (Math.Abs(values[i] - v) < Math.Abs(values[best] - v)) best = i;
        return best;
    }

    private void UpdateSliderLabels()
    {
        HueSliderValue.Text = HueSlider.Value.ToString("0", CultureInfo.CurrentCulture);
        BrightnessSliderValue.Text = BrightnessSlider.Value.ToString("0.00", CultureInfo.CurrentCulture);
        ContrastSliderValue.Text = ContrastSlider.Value.ToString("0.00", CultureInfo.CurrentCulture);
        SaturationSliderValue.Text = SaturationSlider.Value.ToString("0.00", CultureInfo.CurrentCulture);
        GammaSliderValue.Text = GammaSlider.Value.ToString("0.00", CultureInfo.CurrentCulture);
        VolumeSliderValue.Text = VolumeSlider.Value.ToString("0", CultureInfo.CurrentCulture) + "%";
    }

    // ------------------------------------------------------------------ navigation + search

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        foreach (var pair in _pages)
            pair.Value.Visibility = pair.Key.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var text = SearchBox.Text.Trim();
        SearchHint.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var nav in _navItems)
        {
            bool match = text.Length == 0 ||
                         (nav.Content?.ToString() ?? "").IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0;
            nav.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
        }

        var sectionVisibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SecApp.Visibility = sectionVisibility;
        SecMedia.Visibility = sectionVisibility;
        SecSystem.Visibility = sectionVisibility;
    }

    // ------------------------------------------------------------------ controls -> settings

    private void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not CheckBox box) return;
        bool on = box.IsChecked == true;

        switch (box.Name)
        {
            case nameof(SidebarSwitch): _cfg.ShowSidebarAtStart = on; break;
            case nameof(MenuSwitch): _cfg.ShowAnonymousInMenu = on; break;
            case nameof(MinimizeSwitch): _cfg.AnonymousExitOnMinimize = on; break;
            case nameof(AutoAdvanceSwitch): _cfg.AutoAdvance = on; break;
            case nameof(FullscreenSwitch): _cfg.StartFullscreen = on; break;
            case nameof(AwakeSwitch): _cfg.KeepAwake = on; break;
            case nameof(HwSwitch): _cfg.HardwareDecoding = on; break;
            case nameof(SubsOnSwitch): _cfg.SubtitlesOnByDefault = on; break;
            default: return;
        }
        _cfg.Save();
    }

    private void Combo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || sender is not ComboBox combo || combo.SelectedIndex < 0) return;
        int i = combo.SelectedIndex;

        switch (combo.Name)
        {
            case nameof(ThemeCombo): _cfg.Theme = (ThemeMode)i; break;
            case nameof(AccentCombo): _cfg.AccentIndex = i; break;
            case nameof(LoopCombo): _cfg.Loop = (LoopMode)i; break;
            case nameof(SpeedCombo): _cfg.DefaultSpeed = Speeds[i]; break;
            case nameof(SkipCombo): _cfg.SkipSeconds = SkipValues[i]; break;
            case nameof(AspectCombo): _cfg.AspectRatio = Aspects[i]; break;
            case nameof(SubSizeCombo): _cfg.SubtitleSize = i; break;
            default: return;
        }
        _cfg.Save();
    }

    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || sender is not Slider slider) return;

        switch (slider.Name)
        {
            case nameof(HueSlider): _cfg.Hue = slider.Value; break;
            case nameof(BrightnessSlider): _cfg.Brightness = slider.Value; break;
            case nameof(ContrastSlider): _cfg.Contrast = slider.Value; break;
            case nameof(SaturationSlider): _cfg.Saturation = slider.Value; break;
            case nameof(GammaSlider): _cfg.Gamma = slider.Value; break;
            case nameof(VolumeSlider): _cfg.Volume = (int)slider.Value; break;
            default: return;
        }
        UpdateSliderLabels();
        _cfg.Save();
    }

    private void ResetPicture_Click(object sender, RoutedEventArgs e)
    {
        _cfg.Hue = 0; _cfg.Brightness = 1; _cfg.Contrast = 1; _cfg.Saturation = 1; _cfg.Gamma = 1;
        _loading = true;
        HueSlider.Value = 0;
        BrightnessSlider.Value = 1;
        ContrastSlider.Value = 1;
        SaturationSlider.Value = 1;
        GammaSlider.Value = 1;
        _loading = false;
        UpdateSliderLabels();
        _cfg.Save();
    }

    private void ShowTour_Click(object sender, RoutedEventArgs e)
    {
        _cfg.OnboardingDone = false;
        _cfg.Save(notify: false);
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose your media folder" };
        if (Directory.Exists(_cfg.MediaFolder)) dlg.InitialDirectory = _cfg.MediaFolder;
        if (dlg.ShowDialog(this) != true) return;

        _cfg.MediaFolder = dlg.FolderName;
        FolderBox.Text = dlg.FolderName;
        _cfg.Save(notify: false);
    }

    // ------------------------------------------------------------------ passcode

    private void UpdatePasscodeForm()
    {
        bool set = PinStore.IsSet;
        PrivCurrentPanel.Visibility = set ? Visibility.Visible : Visibility.Collapsed;
        PrivNewLabel.Text = set ? "New passcode" : "Passcode";
        PrivSaveButton.Content = set ? "Change passcode" : "Set passcode";
    }

    private void Digits_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(c => c is >= '0' and <= '9');

    private void SavePasscode_Click(object sender, RoutedEventArgs e)
    {
        var error = PasscodeForm.TrySet(PrivCurrentBox.Password, PrivNewBox.Password, PrivConfirmBox.Password);
        if (error != null)
        {
            PrivStatus.Text = error;
            return;
        }

        PrivCurrentBox.Clear();
        PrivNewBox.Clear();
        PrivConfirmBox.Clear();
        PrivStatus.Text = "Passcode saved. Hold Ctrl and type it to enter Anonymous Mode.";
        UpdatePasscodeForm();
    }
}
