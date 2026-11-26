using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace EchoDeck;

public partial class SettingsWindow
{
    // ------------------------------------------------------------------ controls -> settings

    private const string KeyPlaceholder = "••••••••••••";
    private readonly System.Windows.Threading.DispatcherTimer _vizSave = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private bool ConfirmCloudChoice() =>
        TempoDialog.Show(this,
            "With the cloud engine, the audio of every song you generate lyrics for is uploaded to the online service you set below.\n\nNothing is sent until you press Generate. Use it?",
            "Cloud lyrics", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private void Discord_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        string id = new string(DiscordIdBox.Text.Where(char.IsDigit).ToArray());   // an application ID is only digits
        _cfg.DiscordClientId = id;
        _cfg.DiscordDetailsText = DiscordDetailsBox.Text.Trim();
        _cfg.DiscordStateText = DiscordStateBox.Text.Trim();
        _cfg.Save();
    }

    private void Cloud_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _cfg.CloudEndpoint = CloudEndpointBox.Text.Trim();
        _cfg.CloudModel = CloudModelBox.Text.Trim();

        var typed = CloudKeyBox.Password;
        if (typed.Length > 0 && typed != KeyPlaceholder)
        {
            try
            {
                _cfg.CloudKeyProtected = CloudTranscriber.ProtectKey(typed);
                CloudKeyBox.Password = KeyPlaceholder;
            }
            catch (Exception ex)
            {
                AppLog.Write("Cloud key", ex, "could not encrypt the key");
                TempoDialog.Show(this, "Couldn't store the key securely: " + ex.Message, "Cloud lyrics", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        else if (typed.Length == 0)
        {
            _cfg.CloudKeyProtected = "";
        }
        _cfg.Save();
    }

    private void VizSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || sender is not Slider slider) return;

        if (slider == VizIntensitySlider)
        {
            _cfg.VisualizerIntensity = Math.Round(slider.Value, 2);
            VizIntensityText.Text = slider.Value.ToString("0.0") + "×";
        }
        else
        {
            _cfg.VisualizerSensitivity = Math.Round(slider.Value, 2);
            VizSensitivityText.Text = slider.Value.ToString("0.0") + "×";
        }

        // save once the slider stops moving (and the visualiser follows right away)
        _vizSave.Stop();
        _vizSave.Tick -= VizSave_Tick;
        _vizSave.Tick += VizSave_Tick;
        _vizSave.Start();
    }

    private void VizSave_Tick(object? sender, EventArgs e)
    {
        _vizSave.Stop();
        _cfg.Save();
    }

    private void WhisperDownload_Click(object sender, RoutedEventArgs e)
    {
        var window = new WhisperSetupWindow { Owner = this };
        window.ShowDialog();
        UpdateWhisperStatus();
    }

    private void ToggleAssociation(CheckBox box, bool on)
    {
        FileAssociations.Outcome outcome;
        try
        {
            outcome = on ? FileAssociations.Register() : FileAssociations.Unregister();
        }
        catch (Exception ex)
        {
            // Register / Unregister already contain their failures; this is the last safety net
            AppLog.Write("File associations", ex);
            outcome = new FileAssociations.Outcome(false, 1, ex.Message);
        }

        if (!outcome.Succeeded)
        {
            // show what the registry really says now; playback and everything else are unaffected
            box.IsChecked = FileAssociations.IsRegistered();
            TempoDialog.Show(this,
                "Windows (or security software on this PC) blocked part of the change, so Tempo couldn't fully " + (on ? "register" : "unregister") +
                " the file types.\n\nNothing else is affected. You can still choose Tempo under Settings > Apps > Default apps.\n\nDetails were written to " + AppLog.FilePath + ".",
                "Tempo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (on)
            TempoDialog.Show(this,
                "Tempo is registered for common video and audio formats.\n\nIf Windows still opens another player, choose Tempo for that file type under Settings > Apps > Default apps.",
                "Tempo", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenDefaultApps_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); }
        catch { /* older Windows without the Settings app page */ }
    }

    private void WhisperExe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Choose the whisper.cpp program", Filter = "Programs (*.exe)|*.exe", CheckFileExists = true };
        if (dlg.ShowDialog(this) != true) return;
        _cfg.WhisperExePath = dlg.FileName;
        _cfg.Save();
        UpdateWhisperStatus();
    }

    private void WhisperModel_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Choose a Whisper model", Filter = "ggml models (*.bin)|*.bin|All files (*.*)|*.*", CheckFileExists = true };
        if (dlg.ShowDialog(this) != true) return;
        _cfg.WhisperModelPath = dlg.FileName;
        _cfg.Save();
        UpdateWhisperStatus();
    }

    private void UpdateWhisperStatus()
    {
        var (exe, model) = WhisperService.Resolve(_cfg);
        var modelProblem = model == null ? null : WhisperService.CheckModel(model);
        WhisperExeText.Text = exe ?? "Not found yet";
        WhisperModelText.Text = model == null ? "Not found yet" : modelProblem == null ? model : model + "  (" + modelProblem + ")";

        bool stub = WhisperService.IsDeprecatedStub(exe);
        bool ready = exe != null && !stub && model != null && modelProblem == null;
        WhisperStatusText.Text = ready
            ? "Ready: open a song, press Lyrics, then Generate with AI."
            : stub
                ? "main.exe is an old stub that does nothing in recent whisper.cpp releases. Choose whisper-cli.exe instead."
                : modelProblem != null
                ? "Model missing or invalid: " + modelProblem + ". Choose a working ggml model."
                : "Choose both the program and a model to turn on AI lyrics.";
        WhisperStatusText.SetResourceReference(TextBlock.ForegroundProperty, ready ? "AccentBrush" : "MutedBrush");
    }

    private void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not CheckBox box) return;
        bool on = box.IsChecked == true;

        switch (box.Name)
        {
            case nameof(SidebarSwitch): _cfg.ShowSidebarAtStart = on; break;
            case nameof(RememberRecentSwitch): _cfg.RememberRecent = on; break;
            case nameof(AutoAdvanceSwitch): _cfg.AutoAdvance = on; break;
            case nameof(SmartQueueSwitch): _cfg.SmartQueue = on; break;
            case nameof(FullscreenSwitch): _cfg.StartFullscreen = on; break;
            case nameof(AwakeSwitch): _cfg.KeepAwake = on; break;
            case nameof(HwSwitch): _cfg.HardwareDecoding = on; HwModeCombo.IsEnabled = on; break;
            case nameof(SubsOnSwitch): _cfg.SubtitlesOnByDefault = on; break;
            case nameof(ResumeSwitch): _cfg.RememberPosition = on; break;
            case nameof(SmoothResizeSwitch): _cfg.SmoothResize = on; break;
            case nameof(GlobalKeysSwitch): _cfg.GlobalMediaKeys = on; GlobalVolSwitch.IsEnabled = on; break;
            case nameof(GlobalVolSwitch): _cfg.GlobalVolumeKeys = on; break;
            case nameof(TaskbarSwitch): _cfg.TaskbarControls = on; break;
            case nameof(LyricsAutoSwitch): _cfg.AutoLoadLyrics = on; break;
            case nameof(LyricsSaveSwitch): _cfg.SaveGeneratedLyrics = on; break;
            case nameof(LyricsWordsSwitch): _cfg.LyricsWordByWord = on; break;
            case nameof(LyricsBounceSwitch): _cfg.LyricsBounce = on; break;
            case nameof(HideUiSwitch): _cfg.HideInterface = on; break;
            case nameof(CleanTitlesSwitch): _cfg.CleanTitles = on; break;
            case nameof(CrossfadeSwitch): _cfg.CrossfadeEnabled = on; CrossfadeCombo.IsEnabled = on; break;
            case nameof(GaplessSwitch): _cfg.GaplessEnabled = on; break;
            case nameof(MiniDragVideoSwitch): _cfg.MiniVideoWhileDragging = on; break;
            case nameof(NormalizeSwitch): _cfg.NormalizeVolume = on; break;
            case nameof(DiscordSwitch): _cfg.DiscordPresence = on; break;
            case nameof(CoversSwitch): _cfg.FetchCoversOnline = on; break;
            case nameof(YouTubeCoversSwitch): _cfg.CoverFromYouTube = on; break;
            case nameof(AssocSwitch): ToggleAssociation(box, on); return;
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
            case nameof(ViewCombo): _cfg.LibraryGrid = i == 0; break;
            case nameof(StartPageCombo): _cfg.StartPage = i == 1 ? "Home" : i == 2 ? "Blank" : "Recent"; break;
            case nameof(NetCacheCombo): _cfg.NetworkCachingMs = CacheValues[i]; break;
            case nameof(VisualizerCombo): _cfg.VisualizerStyle = i; break;
            case nameof(MotionCombo): _cfg.MotionMode = i; break;
            case nameof(CrossfadeCombo): _cfg.CrossfadeSeconds = (i + 1) * 2; break;
            case nameof(StereoModeCombo): _cfg.StereoMode = i; break;
            case nameof(SoundSpaceCombo): _cfg.SoundSpace = i; break;
            case nameof(HwModeCombo): _cfg.HardwareMode = HwModes[i]; break;
            case nameof(WhisperLangCombo): _cfg.WhisperLanguage = WhisperLangs[i]; break;
            case nameof(LyricsEngineCombo):
                if (i == 1 && !ConfirmCloudChoice())
                {
                    _loading = true;
                    LyricsEngineCombo.SelectedIndex = 0;
                    _loading = false;
                    return;
                }
                _cfg.LyricsEngine = i == 1 ? "cloud" : "local";
                CloudPanel.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
                break;
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

    // ------------------------------------------------------------------ library locations

    private void PopulateFolders()
    {
        FillFolderList(MusicFoldersPanel, _cfg.MusicFolders);
        FillFolderList(VideoFoldersPanel, _cfg.VideoFolders);
    }

    private void FillFolderList(StackPanel panel, List<string> folders)
    {
        panel.Children.Clear();
        if (folders.Count == 0)
        {
            var none = new TextBlock { Text = "No folders yet.", Style = (Style)FindResource("HintText") };
            panel.Children.Add(none);
            return;
        }

        foreach (var folder in folders.ToList())
        {
            var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 8, 8), Margin = new Thickness(0, 0, 0, 6) };
            row.SetResourceReference(Border.BackgroundProperty, "PanelBrush");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new TextBlock { Text = folder, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13 };
            grid.Children.Add(text);

            var remove = new Button { Style = (Style)FindResource("FigmaIconButton"), Width = 30, Height = 30, ToolTip = "Remove this folder" };
            var icon = new System.Windows.Shapes.Path
            {
                Data = (System.Windows.Media.Geometry)FindResource("IcoClose"),
                Style = (Style)FindResource("Ico")
            };
            remove.Content = icon;
            var captured = folder;
            remove.Click += (_, _) =>
            {
                folders.RemoveAll(f => string.Equals(f, captured, StringComparison.OrdinalIgnoreCase));
                _cfg.Save();
                PopulateFolders();
            };
            Grid.SetColumn(remove, 1);
            grid.Children.Add(remove);

            row.Child = grid;
            panel.Children.Add(row);
        }
    }

    private void AddFolder(List<string> target, string title)
    {
        var dlg = new OpenFolderDialog { Title = title };
        if (dlg.ShowDialog(this) != true) return;
        if (target.Any(f => string.Equals(f, dlg.FolderName, StringComparison.OrdinalIgnoreCase))) return;

        target.Add(dlg.FolderName);
        _cfg.Save();
        PopulateFolders();
    }

    private void AddMusicFolder_Click(object sender, RoutedEventArgs e) => AddFolder(_cfg.MusicFolders, "Add a music folder");

    private void AddVideoFolder_Click(object sender, RoutedEventArgs e) => AddFolder(_cfg.VideoFolders, "Add a video folder");

    private void RefreshLibraries_Click(object sender, RoutedEventArgs e)
    {
        LibraryScanner.Invalidate();
        _cfg.Save();
    }

    private void BrowseSnap_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose where snapshots are saved" };
        if (Directory.Exists(_cfg.SnapshotFolder)) dlg.InitialDirectory = _cfg.SnapshotFolder;
        if (dlg.ShowDialog(this) != true) return;

        _cfg.SnapshotFolder = dlg.FolderName;
        SnapFolderBox.Text = dlg.FolderName;
        _cfg.Save(notify: false);
    }

    private void OpenSnap_Click(object sender, RoutedEventArgs e)
    {
        var dir = !string.IsNullOrWhiteSpace(_cfg.SnapshotFolder)
            ? _cfg.SnapshotFolder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Tempo");
        try
        {
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch { /* nothing to open */ }
    }
}
