using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;

namespace EchoDeck;

public partial class MainWindow
{
    // ------------------------------------------------------------------ menus

    private MenuItem MenuEntry(string header, string iconKey, RoutedEventHandler? click, string gesture = "")
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = (System.Windows.Media.Geometry)FindResource(iconKey),
            Width = 24,
            Height = 24,
            Stretch = System.Windows.Media.Stretch.None,
            StrokeThickness = 1.5,
            StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeLineJoin = System.Windows.Media.PenLineJoin.Round
        };
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush");

        var mi = new MenuItem
        {
            Header = header,
            InputGestureText = gesture,
            Icon = new Viewbox { Width = 18, Height = 18, Child = path }
        };
        if (click != null) mi.Click += click;
        return mi;
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = sender as UIElement ?? MenuButton, Placement = PlacementMode.Bottom };

        if (!_anon)
        {
            menu.Items.Add(MenuEntry("Open file(s)", "IcoVideo", Open_Click, "Ctrl+O"));
            menu.Items.Add(MenuEntry("Open folder", "IcoFolder", OpenFolder_Click));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Keyboard shortcuts", "IcoKeyboard", Shortcuts_Click));
        }
        else
        {
            menu.Items.Add(MenuEntry("Import files…", "IcoImport", ImportFiles_Click, "Ctrl+O"));
            menu.Items.Add(MenuEntry("Import folder…", "IcoFolder", ImportFolder_Click));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(MenuEntry("Settings", "IcoSettings", AllSettings_Click, "Ctrl+,"));
        menu.Items.Add(MenuEntry("About Tempo", "IcoInfo", About_Click));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Exit", "IcoClose", Close_Click));
        menu.IsOpen = true;
    }

    /// <summary>Right-click menu (video, library, anywhere in the window).</summary>
    private void ShowContextMenu()
    {
        if (_modalOpen || _transition) return;
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };

        if (!_libraryVisible)
        {
            menu.Items.Add(MenuEntry(_player != null && _player.IsPlaying ? "Pause" : "Play", "IcoPlay", PlayPause_Click, "Space"));
            {
                menu.Items.Add(MenuEntry("Previous", "IcoPrevious", Previous_Click, "Shift+,"));
                menu.Items.Add(MenuEntry("Next", "IcoNext", Next_Click, "Shift+."));
                menu.Items.Add(new Separator());
                if (!_anon) menu.Items.Add(MenuEntry("Open file(s)", "IcoVideo", Open_Click, "Ctrl+O"));
                menu.Items.Add(MenuEntry("Show play queue", "IcoPlaylist", (_, _) => ShowSide(SideMode.Queue), "Q"));
                menu.Items.Add(MenuEntry("Show playlists", "IcoPlaylist", (_, _) => ShowSide(SideMode.Playlists), "P"));
                if (_hasMedia && _queueIndex >= 0 && _queueIndex < _queue.Count)
                {
                    var current = _queue[_queueIndex].FullPath;
                    menu.Items.Add(MenuEntry("Properties", "IcoInfo", (_, _) => ShowProperties(current)));
                }
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Equaliser", "IcoSliders", (_, _) => OpenEqualizer()));
            menu.Items.Add(MenuEntry("Speed", "IcoClock", (_, _) => ShowSpeedMenu()));
            menu.Items.Add(MenuEntry("Audio track", "IcoAudio", Audio_Click, "A"));
            menu.Items.Add(MenuEntry("Subtitles", "IcoSubtitles", Subtitle_Click, "V"));
            menu.Items.Add(MenuEntry("Video settings", "IcoSettings", (_, _) => OpenQuick(QTabVideo, forceOpen: true)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Fullscreen", "IcoFullscreen", Fullscreen_Click, "F"));
            menu.Items.Add(MenuEntry(_cfg.HideInterface ? "Show interface" : "Hide interface", "IcoCursor", (_, _) => ToggleHideUi(), "H"));
            menu.Items.Add(MenuEntry(SleepActive ? "Sleep timer (on)" : "Sleep timer", "IcoClock", (_, _) => ShowSleepMenu()));
            if (_lyricsOpen && _lyrics is { Synced: true })
            {
                menu.Items.Add(new Separator());
                string now = _lyricOffsetMs == 0 ? "" : $" (now {(_lyricOffsetMs > 0 ? "+" : "")}{_lyricOffsetMs / 1000.0:0.0} s)";
                menu.Items.Add(MenuEntry("Lyrics earlier by 0.1 s" + now, "IcoClock", (_, _) => NudgeLyricOffset(-100), "["));
                menu.Items.Add(MenuEntry("Lyrics later by 0.1 s" + now, "IcoClock", (_, _) => NudgeLyricOffset(+100), "]"));
            }
            menu.Items.Add(MenuEntry("Back to library", "IcoHome", (_, _) => ShowLibrary(true), "Backspace"));
        }
        else
        {
            if (_anon)
            {
                menu.Items.Add(MenuEntry("Import files…", "IcoImport", ImportFiles_Click, "Ctrl+O"));
                menu.Items.Add(MenuEntry("Import folder…", "IcoFolder", ImportFolder_Click));
            }
            else
            {
                menu.Items.Add(MenuEntry("Open file(s)", "IcoVideo", Open_Click, "Ctrl+O"));
                menu.Items.Add(MenuEntry("Open folder", "IcoFolder", OpenFolder_Click));
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Settings", "IcoSettings", AllSettings_Click, "Ctrl+,"));
        }

        if (menu.Items.Count == 0) return;
        if (ShowFastMenu(menu)) return;   // appears at once (no fade); the real menu is only the fallback
        SkinMenu(menu);
        menu.IsOpen = true;
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        TempoDialog.Show(this, $"Tempo {v?.Major}.{v?.Minor}.{v?.Build}\nA modern media player built on LibVLC.",
            "About Tempo", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Shortcuts_Click(object sender, RoutedEventArgs e) => ShowSettings("controls");

    /// <summary>The bundle's gear: opens the quick settings panel on the Video tab.</summary>
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenQuick(QTabVideo);

    private void AllSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_anon) OpenAnonymousSettings();
        else ShowSettings(null);
    }

    private void ShowSettings(string? page)
    {
        if (_anon || _modalOpen) return;
        _modalOpen = true;
        try { new SettingsWindow(page) { Owner = this }.ShowDialog(); }
        finally { _modalOpen = false; }
        RootGrid.Focus();
    }

    // ------------------------------------------------------------------ quick settings panel

    private DateTime _quickClosedAt = DateTime.MinValue;

    private void QuickPopup_Closed(object? sender, EventArgs e) => _quickClosedAt = DateTime.UtcNow;

    private void OpenQuick(RadioButton tab, bool forceOpen = false)
    {
        if (_libraryVisible) return;

        if (QuickPopup.IsOpen)
        {
            if (!forceOpen && tab.IsChecked == true) { ClosePopup(QuickPopup); return; }
        }
        else if (!forceOpen && (DateTime.UtcNow - _quickClosedAt).TotalMilliseconds < 250)
        {
            return; // this very click just closed the popup
        }

        tab.IsChecked = true;
        QuickPopup.IsOpen = true;
        RefreshQuick();
    }

    private void QuickClose_Click(object sender, RoutedEventArgs e) => ClosePopup(QuickPopup);

    private void QTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        QPageVideo.Visibility = QTabVideo.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        QPageAudio.Visibility = QTabAudio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        QPageSubs.Visibility = QTabSubs.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RefreshQuick();
    }

    private void RefreshQuick()
    {
        if (!IsLoaded) return;
        _loadingQuick = true;
        try
        {
            QHue.Value = _cfg.Hue;
            QBrightness.Value = _cfg.Brightness;
            QContrast.Value = _cfg.Contrast;
            QSaturation.Value = _cfg.Saturation;
            QGamma.Value = _cfg.Gamma;
            UpdateQuickLabels();

            if (QAspectCombo.Items.Count == 0)
                foreach (var a in AspectNames) QAspectCombo.Items.Add(a);
            QAspectCombo.SelectedIndex = Math.Max(0, Array.IndexOf(AspectNames, _cfg.AspectRatio));

            if (QSpeedCombo.Items.Count == 0)
                foreach (var s in SpeedNames) QSpeedCombo.Items.Add(s);
            int speedIdx = 2;
            for (int i = 0; i < SpeedValues.Length; i++)
                if (Math.Abs(SpeedValues[i] - _cfg.DefaultSpeed) < 0.001) speedIdx = i;
            QSpeedCombo.SelectedIndex = speedIdx;

            if (QStereoCombo.Items.Count == 0)
                foreach (var m in StereoModes.Names) QStereoCombo.Items.Add(m);
            QStereoCombo.SelectedIndex = Math.Clamp(_cfg.StereoMode, 0, StereoModes.Names.Length - 1);

            FillTrackCombo(QAudioCombo, _quickAudioIds, audio: true);
            FillTrackCombo(QSubCombo, _quickSubIds, audio: false);
            QAudioHint.Text = QAudioCombo.Items.Count == 0 ? "Nothing is playing yet." : "";
            QAddSubButton.IsEnabled = _hasMedia && !_anon;
        }
        finally
        {
            _loadingQuick = false;
        }
    }

    private void FillTrackCombo(ComboBox combo, List<int> ids, bool audio)
    {
        combo.Items.Clear();
        ids.Clear();
        if (_player is null || !_hasMedia) return;

        var tracks = audio ? _player.AudioTrackDescription : _player.SpuDescription;
        if (tracks == null) return;
        int current = audio ? _player.AudioTrack : _player.Spu;

        int selected = -1;
        foreach (var t in tracks)
        {
            if (t.Id == current) selected = ids.Count;
            ids.Add(t.Id);
            combo.Items.Add(string.IsNullOrEmpty(t.Name) ? "Track " + t.Id : t.Name);
        }
        combo.SelectedIndex = selected;
    }

    private void UpdateQuickLabels()
    {
        QHueValue.Text = QHue.Value.ToString("0", CultureInfo.CurrentCulture);
        QBrightnessValue.Text = QBrightness.Value.ToString("0.00", CultureInfo.CurrentCulture);
        QContrastValue.Text = QContrast.Value.ToString("0.00", CultureInfo.CurrentCulture);
        QSaturationValue.Text = QSaturation.Value.ToString("0.00", CultureInfo.CurrentCulture);
        QGammaValue.Text = QGamma.Value.ToString("0.00", CultureInfo.CurrentCulture);
    }

    private void QuickSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingQuick || !IsLoaded || sender is not Slider slider) return;

        switch (slider.Name)
        {
            case nameof(QHue): _cfg.Hue = slider.Value; break;
            case nameof(QBrightness): _cfg.Brightness = slider.Value; break;
            case nameof(QContrast): _cfg.Contrast = slider.Value; break;
            case nameof(QSaturation): _cfg.Saturation = slider.Value; break;
            case nameof(QGamma): _cfg.Gamma = slider.Value; break;
            default: return;
        }
        UpdateQuickLabels();
        CommitPlayerSetting();
    }

    private void QuickReset_Click(object sender, RoutedEventArgs e)
    {
        _cfg.Hue = 0; _cfg.Brightness = 1; _cfg.Contrast = 1; _cfg.Saturation = 1; _cfg.Gamma = 1;
        CommitPlayerSetting();
        RefreshQuick();
    }

    private void QuickCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingQuick || !IsLoaded || sender is not ComboBox combo || combo.SelectedIndex < 0 || _player is null) return;
        int i = combo.SelectedIndex;

        switch (combo.Name)
        {
            case nameof(QStereoCombo):
                _cfg.StereoMode = i;
                if (!_anon) _cfg.Save(notify: false);
                ApplySoundSettings();   // puts the channel mode on the playing song at once (and says so if the device refuses it)
                ShowOsd("Channels: " + StereoModes.Names[i]);
                return;
            case nameof(QAudioCombo):
                if (i < _quickAudioIds.Count) _player.SetAudioTrack(_quickAudioIds[i]);
                return;
            case nameof(QSubCombo):
                if (i < _quickSubIds.Count) _player.SetSpu(_quickSubIds[i]);
                return;
            case nameof(QAspectCombo):
                _cfg.AspectRatio = AspectNames[i];
                break;
            case nameof(QSpeedCombo):
                _cfg.DefaultSpeed = SpeedValues[i];
                _player.SetRate((float)SpeedValues[i]);
                break;
            default:
                return;
        }
        CommitPlayerSetting();
    }

    /// <summary>Applies picture / aspect settings to the player; saved to disk only outside Anonymous Mode.</summary>
    private void CommitPlayerSetting()
    {
        ApplyVideoAdjust();
        if (!_anon) _cfg.Save(notify: false);
    }

    private void QuickAddSub_Click(object sender, RoutedEventArgs e) => AddSubtitleFile();

    private void AddSubtitleFile()
    {
        if (_player is null || !_hasMedia || _anon) return;
        var dlg = new OpenFileDialog
        {
            Title = "Add subtitle file",
            Filter = "Subtitles|*.srt;*.ass;*.ssa;*.sub;*.vtt|All files|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;
        _player.AddSlave(MediaSlaveType.Subtitle, new Uri(dlg.FileName).AbsoluteUri, true);
        Dispatcher.InvokeAsync(RefreshQuick, DispatcherPriority.Background);
    }
}
