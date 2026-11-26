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
    private static readonly int[] CacheValues = { 300, 1000, 3000, 6000 };
    private static readonly string[] Aspects = { "Default", "16:9", "4:3", "1:1", "21:9", "16:10" };
    private static readonly string[] HwModes = { "auto", "d3d11", "dxva2" };
    private static readonly string[] WhisperLangs = { "auto", "en", "es", "fr", "de", "it", "pt", "nl", "ru", "ja", "ko", "zh" };

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
        _pages[NavLyrics] = PageLyrics;
        _pages[NavLibrary] = PageLibrary;
        _pages[NavNetwork] = PageNetwork;
        _pages[NavPerformance] = PagePerformance;
        _pages[NavRecording] = PageRecording;
        _pages[NavAbout] = PageAbout;
        _navItems.AddRange(_pages.Keys);

        LoadValues();
        _loading = false;

        if (page == "library") NavLibrary.IsChecked = true;
        else if (page == "privacy") NavPrivacy.IsChecked = true;
        else if (page == "controls") NavControls.IsChecked = true;
        else if (page == "lyrics") NavLyrics.IsChecked = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        PrivNewBox.Clear();
        PrivConfirmBox.Clear();
        base.OnClosed(e);
    }

    // ------------------------------------------------------------------ loading

    private static string FormatOffset(int ms) => (ms > 0 ? "+" : "") + (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";

    private void LyricsOffsetSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _cfg.LyricsOffsetMs = (int)Math.Round(e.NewValue / 100.0) * 100;
        LyricsOffsetValue.Text = FormatOffset(_cfg.LyricsOffsetMs);
        _cfg.Save();
    }

    private void DuplicatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cfg.MusicFolders.Count == 0)
        {
            TempoDialog.Show(this, "Add a music folder first (Settings > Library).", "Duplicate songs", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new DuplicatesWindow(_cfg.MusicFolders.ToList()) { Owner = this }.ShowDialog();
    }

    private void LoadValues()
    {
        ThemeCombo.SelectedIndex = _cfg.Theme is ThemeMode.LiquidGlass or ThemeMode.Amoled ? 0 : (int)_cfg.Theme;   // 0 Dark, 1 Light, 2 System, 3 Midnight
        AccentCombo.SelectedIndex = Math.Clamp(_cfg.AccentIndex, 0, ThemeManager.Accents.Length - 1);

        SidebarSwitch.IsChecked = _cfg.ShowSidebarAtStart;
        RememberRecentSwitch.IsChecked = _cfg.RememberRecent;
        AutoAdvanceSwitch.IsChecked = _cfg.AutoAdvance;
        SmartQueueSwitch.IsChecked = _cfg.SmartQueue;
        FullscreenSwitch.IsChecked = _cfg.StartFullscreen;
        AwakeSwitch.IsChecked = _cfg.KeepAwake;
        HwSwitch.IsChecked = _cfg.HardwareDecoding;
        SubsOnSwitch.IsChecked = _cfg.SubtitlesOnByDefault;
        ResumeSwitch.IsChecked = _cfg.RememberPosition;
        SmoothResizeSwitch.IsChecked = _cfg.SmoothResize;
        VizIntensitySlider.Value = Math.Clamp(_cfg.VisualizerIntensity, 0.4, 2.0);
        VizSensitivitySlider.Value = Math.Clamp(_cfg.VisualizerSensitivity, 0.4, 2.5);
        VizIntensityText.Text = VizIntensitySlider.Value.ToString("0.0") + "×";
        VizSensitivityText.Text = VizSensitivitySlider.Value.ToString("0.0") + "×";
        LyricsEngineCombo.SelectedIndex = string.Equals(_cfg.LyricsEngine, "cloud", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        CloudPanel.Visibility = LyricsEngineCombo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        CloudEndpointBox.Text = _cfg.CloudEndpoint;
        CloudModelBox.Text = _cfg.CloudModel;
        CloudKeyBox.Password = string.IsNullOrEmpty(_cfg.CloudKeyProtected) ? "" : KeyPlaceholder;
        GlobalKeysSwitch.IsChecked = _cfg.GlobalMediaKeys;
        GlobalVolSwitch.IsChecked = _cfg.GlobalVolumeKeys;
        GlobalVolSwitch.IsEnabled = _cfg.GlobalMediaKeys;
        TaskbarSwitch.IsChecked = _cfg.TaskbarControls;
        AssocSwitch.IsChecked = FileAssociations.IsRegistered();
        LyricsAutoSwitch.IsChecked = _cfg.AutoLoadLyrics;
        LyricsSaveSwitch.IsChecked = _cfg.SaveGeneratedLyrics;
        LyricsWordsSwitch.IsChecked = _cfg.LyricsWordByWord;
        LyricsBounceSwitch.IsChecked = _cfg.LyricsBounce;
        LyricsOffsetSlider.Value = Math.Clamp(_cfg.LyricsOffsetMs, -2000, 2000);
        LyricsOffsetValue.Text = FormatOffset(_cfg.LyricsOffsetMs);
        HideUiSwitch.IsChecked = _cfg.HideInterface;
        CleanTitlesSwitch.IsChecked = _cfg.CleanTitles;
        CrossfadeSwitch.IsChecked = _cfg.CrossfadeEnabled;
        GaplessSwitch.IsChecked = _cfg.GaplessEnabled;
        MiniDragVideoSwitch.IsChecked = _cfg.MiniVideoWhileDragging;
        NormalizeSwitch.IsChecked = _cfg.NormalizeVolume;
        StereoModeCombo.SelectedIndex = Math.Clamp(_cfg.StereoMode, 0, 5);
        SoundSpaceCombo.SelectedIndex = Math.Clamp(_cfg.SoundSpace, 0, 3);
        DiscordSwitch.IsChecked = _cfg.DiscordPresence;
        DiscordIdBox.Text = _cfg.DiscordClientId;
        DiscordDetailsBox.Text = _cfg.DiscordDetailsText;
        DiscordStateBox.Text = _cfg.DiscordStateText;
        CrossfadeCombo.SelectedIndex = Math.Clamp(_cfg.CrossfadeSeconds / 2 - 1, 0, 5);
        CrossfadeCombo.IsEnabled = _cfg.CrossfadeEnabled;
        CoversSwitch.IsChecked = _cfg.FetchCoversOnline;
        YouTubeCoversSwitch.IsChecked = _cfg.CoverFromYouTube;
        MotionCombo.SelectedIndex = Math.Clamp(_cfg.MotionMode, 0, 2);
        VisualizerCombo.SelectedIndex = Math.Clamp(_cfg.VisualizerStyle, 0, 2);
        HwModeCombo.SelectedIndex = Math.Max(0, Array.IndexOf(HwModes, _cfg.HardwareMode));
        HwModeCombo.IsEnabled = _cfg.HardwareDecoding;
        WhisperLangCombo.SelectedIndex = Math.Max(0, Array.IndexOf(WhisperLangs, _cfg.WhisperLanguage));
        WhisperFolderText.Text = "Folder checked automatically: " + WhisperService.DefaultFolder;
        UpdateWhisperStatus();

        LoopCombo.SelectedIndex = (int)_cfg.Loop;
        SpeedCombo.SelectedIndex = NearestIndex(Speeds, _cfg.DefaultSpeed);
        SkipCombo.SelectedIndex = Math.Max(0, Array.IndexOf(SkipValues, _cfg.SkipSeconds));
        AspectCombo.SelectedIndex = Math.Max(0, Array.IndexOf(Aspects, _cfg.AspectRatio));
        SubSizeCombo.SelectedIndex = Math.Clamp(_cfg.SubtitleSize, 0, 3);
        ViewCombo.SelectedIndex = _cfg.LibraryGrid ? 0 : 1;
        StartPageCombo.SelectedIndex = _cfg.StartPage == "Home" ? 1 : _cfg.StartPage == "Blank" ? 2 : 0;
        NetCacheCombo.SelectedIndex = Math.Max(0, Array.IndexOf(CacheValues, _cfg.NetworkCachingMs));
        SnapFolderBox.Text = _cfg.SnapshotFolder;

        HueSlider.Value = _cfg.Hue;
        BrightnessSlider.Value = _cfg.Brightness;
        ContrastSlider.Value = _cfg.Contrast;
        SaturationSlider.Value = _cfg.Saturation;
        GammaSlider.Value = _cfg.Gamma;
        VolumeSlider.Value = _cfg.Volume;
        UpdateSliderLabels();

        PopulateFolders();
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

    // ------------------------------------------------------------------ passcode

    private void UpdatePasscodeForm()
    {
        bool set = PinStore.IsSet;
        PrivSetupBlock.Visibility = set ? Visibility.Collapsed : Visibility.Visible;
        PrivManagedHint.Visibility = set ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearRecentHistory_Click(object sender, RoutedEventArgs e) => RecentStore.Clear();

    private void Digits_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(c => c is >= '0' and <= '9');

    private void SavePasscode_Click(object sender, RoutedEventArgs e)
    {
        var error = PasscodeForm.TrySet(null, PrivNewBox.Password, PrivConfirmBox.Password);
        if (error != null)
        {
            PrivStatus.Text = error;
            return;
        }

        PrivNewBox.Clear();
        PrivConfirmBox.Clear();
        UpdatePasscodeForm();
    }
}
