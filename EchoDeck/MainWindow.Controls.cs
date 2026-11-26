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
    // ------------------------------------------------------------------ toolbar handlers

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_anon) { ImportFiles_Click(sender, e); return; }
        var dlg = new OpenFileDialog { Title = "Open media", Filter = MediaTypes.DialogFilter, Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;

        var files = dlg.FileNames.Where(MediaTypes.IsSupportedFile).ToList();
        if (files.Count == 0) return;

        var dir = Path.GetDirectoryName(files[0]);
        if (dir != null) NavigateTo(dir);

        if (files.Count == 1) { PlayOpenedFile(files[0]); return; }

        foreach (var f in files.Skip(1))
            if (!_queue.Any(q => string.Equals(q.FullPath, f, StringComparison.OrdinalIgnoreCase)))
                _queue.Add(new QueueItem(f));
        PlayNormal(files[0]);
    }

    /// <summary>
    /// A single file opened from outside (Explorer, "Open with", a drop). With the smart queue on, the other files of its series in the
    /// same folder are queued in order around it (Episode 1 to 7), and the opened one plays.
    /// </summary>
    private void PlayOpenedFile(string path)
    {
        if (_cfg.SmartQueue && !_anon && File.Exists(path))
        {
            var series = SmartQueue.SeriesOf(path);
            if (series.Count > 1)
            {
                _queue.Clear();
                foreach (var p in series) _queue.Add(new QueueItem(p));
                PlayNormal(path);
                ShowOsd("Queued " + series.Count + " in order");
                return;
            }
        }
        PlayNormal(path);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_anon) { ImportFolder_Click(sender, e); return; }
        var dlg = new OpenFolderDialog { Title = "Choose a media folder" };
        if (dlg.ShowDialog(this) != true) return;

        NavHome.IsChecked = true;
        ShowLibrary(true);
        NavigateTo(dlg.FolderName);
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_anon)
        {
            Notify("Use the ··· menu on an item and choose Add to");
            return;
        }
        var dlg = new OpenFileDialog { Title = "Add to playlist", Filter = MediaTypes.DialogFilter, Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;

        foreach (var f in dlg.FileNames.Where(MediaTypes.IsSupportedFile))
            if (!_queue.Any(q => string.Equals(q.FullPath, f, StringComparison.OrdinalIgnoreCase)))
                _queue.Add(new QueueItem(f));

        if (!_hasMedia && _queue.Count > 0) PlayNormal(_queue[0].FullPath);
        RefreshSide();
        UpdateTransportEnabled();
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => PlayAdjacent(-1);

    private void Next_Click(object sender, RoutedEventArgs e) => PlayAdjacent(+1);

    private void SkipBack_Click(object sender, RoutedEventArgs e) => SeekBy(-_cfg.SkipSeconds);

    private void SkipFwd_Click(object sender, RoutedEventArgs e) => SeekBy(_cfg.SkipSeconds);

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_isFullscreen) { ToggleFullscreen(); return; }
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private DateTime _volumePopupClosedAt = DateTime.MinValue;

    /// <summary>The speaker opens the volume slider (a popup), a second click closes it.</summary>
    private void Volume_Click(object sender, RoutedEventArgs e)
    {
        if (VolumePopup.IsOpen) { ClosePopup(VolumePopup); return; }
        if ((DateTime.UtcNow - _volumePopupClosedAt).TotalMilliseconds < 250) return; // the click that just closed it
        VolumePopup.IsOpen = true;
    }

    private void VolumePopup_Closed(object? sender, EventArgs e) => _volumePopupClosedAt = DateTime.UtcNow;

    private void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();

    private void Audio_Click(object sender, RoutedEventArgs e) => OpenQuick(QTabAudio);

    private void Subtitle_Click(object sender, RoutedEventArgs e) => OpenQuick(QTabSubs);

    private void CycleTrack(bool audio)
    {
        if (_player is null) return;
        var tracks = audio ? _player.AudioTrackDescription : _player.SpuDescription;
        if (tracks == null || tracks.Length == 0) return;

        int current = audio ? _player.AudioTrack : _player.Spu;
        int idx = Array.FindIndex(tracks, t => t.Id == current);
        var next = tracks[(idx + 1) % tracks.Length];
        if (audio) _player.SetAudioTrack(next.Id); else _player.SetSpu(next.Id);
    }

    private void TakeSnapshot()
    {
        // Never in Anonymous Mode: a screenshot of decrypted video would be written to disk in the clear.
        if (_player is null || !_hasMedia || _anon || _currentIsAudio) return;
        try
        {
            var dir = !string.IsNullOrWhiteSpace(_cfg.SnapshotFolder)
                ? _cfg.SnapshotFolder
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), AppName);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "snapshot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
            _player.TakeSnapshot(0, file, 0, 0);
        }
        catch { /* best effort */ }
    }

    private void OnWindowStateChanged()
    {
        bool maximized = WindowState == WindowState.Maximized && !_isFullscreen;
        RootGrid.Margin = maximized ? _maxMargin : new Thickness(0);
        MaxIcon.Data = (System.Windows.Media.Geometry)FindResource(
            WindowState == WindowState.Maximized ? "IcoRestore" : "IcoMaximize");
    }

    /// <summary>Fullscreen shows only the video: title bar, nav, panels and control bar are all hidden.</summary>
    private void SetChromeVisible(bool visible)
    {
        visible &= !_uiHidden;   // the interface stays hidden after leaving fullscreen if it was hidden before
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        TopBar.Visibility = v;
        ApplyRail(animate: false);
        if (visible) ControlBar.Visibility = _libraryVisible ? Visibility.Collapsed : Visibility.Visible;
        else if (!_isFullscreen) ControlBar.Visibility = Visibility.Collapsed;
        ApplySidebar(animate: false);
        if (!visible) ClosePopup(QuickPopup, animate: false);
        ContentGrid.Margin = visible ? new Thickness(8, 0, 8, 8) : new Thickness(0);

        if (_chrome != null)
        {
            // hidden interface in a window (not fullscreen): a thin invisible strip at the top still drags the window, the edges still resize
            bool windowed = _uiHidden && !_isFullscreen;
            _chrome.CaptionHeight = visible ? 40 : (windowed ? 14 : 0);
            _chrome.ResizeBorderThickness = visible || windowed ? new Thickness(6) : new Thickness(0);
        }
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] items || items.Length == 0) return;
        if (_anon) { _ = ImportDroppedAsync(items); return; }

        var folder = items.FirstOrDefault(Directory.Exists);
        if (folder != null)
        {
            ShowLibrary(true);
            NavigateTo(folder);
            return;
        }

        var files = items.Where(MediaTypes.IsSupportedFile).ToList();
        if (files.Count == 0) return;

        foreach (var f in files.Skip(1))
            if (!_queue.Any(q => string.Equals(q.FullPath, f, StringComparison.OrdinalIgnoreCase)))
                _queue.Add(new QueueItem(f));
        PlayNormal(files[0]);
    }

    /// <summary>Speaker icons (bar and popup): waves follow the level, a cross shows when muted, a pulse on every change.</summary>
    private void UpdateVolumeGlyphs(bool animate)
    {
        if (VolumeIcon is null || PopupVolumeIcon is null || VolumeSlider is null) return;
        double level = Math.Min(1.0, VolumeSlider.Value / 100.0);   // the speaker icon is full at 100 %
        bool muted = _player?.Mute ?? false;
        VolumeIcon.SetState(level, muted, animate);
        PopupVolumeIcon.SetState(level, muted, animate);
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        Volatile.Write(ref _volumeTarget, (int)e.NewValue);
        if (_player != null) _player.Volume = (int)e.NewValue;
        VolumeText.Text = (int)e.NewValue + "%";
        UpdateVolumeGlyphs(IsLoaded);
        if (IsLoaded && !_anon && !_transition)
        {
            _cfg.Volume = (int)e.NewValue;
            _volumeSaveTimer ??= CreateVolumeSaveTimer();
            _volumeSaveTimer.Stop();
            _volumeSaveTimer.Start();
        }
    }

    private DispatcherTimer? _volumeSaveTimer;

    private DispatcherTimer CreateVolumeSaveTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) => { t.Stop(); _cfg.Save(notify: false); };
        return t;
    }

    /// <summary>Double-click the picture for fullscreen (a single click does nothing).</summary>
    private void VideoSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_transition || !_hasMedia) return;
        if (e.ClickCount >= 2) ToggleFullscreen();
    }

    private void VideoSurface_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_libraryVisible) { e.Handled = true; return; }   // no context menu on the small player
        ShowContextMenu();
    }

    private Rect _prevBounds;

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            if (_libraryVisible)
            {
                if (!_hasMedia) return;
                ShowLibrary(false);
            }

            _prevState = WindowState;
            _prevStyle = WindowStyle;
            _prevResize = ResizeMode;
            _prevBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;

            // Cover the WHOLE monitor (taskbar included), not just the work area.
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null &&
                NativeMethods.TryGetMonitorBounds(new WindowInteropHelper(this).Handle, out var r))
            {
                var tl = source.CompositionTarget.TransformFromDevice.Transform(new Point(r.Left, r.Top));
                var br = source.CompositionTarget.TransformFromDevice.Transform(new Point(r.Right, r.Bottom));
                Left = tl.X;
                Top = tl.Y;
                Width = br.X - tl.X;
                Height = br.Y - tl.Y;
                Topmost = true;
            }
            else
            {
                WindowState = WindowState.Maximized;
            }

            _isFullscreen = true;
            SetChromeVisible(false);
            AttachFloatingBar();
            OnWindowStateChanged();
        }
        else
        {
            Topmost = false;
            WindowStyle = _prevStyle;
            ResizeMode = _prevResize;
            WindowState = WindowState.Normal;
            if (_prevBounds.Width > 0 && _prevBounds.Height > 0)
            {
                Left = _prevBounds.Left;
                Top = _prevBounds.Top;
                Width = _prevBounds.Width;
                Height = _prevBounds.Height;
            }
            if (_prevState == WindowState.Maximized) WindowState = WindowState.Maximized;

            _isFullscreen = false;
            DetachFloatingBar();
            SetChromeVisible(true);
            OnWindowStateChanged();
        }
    }

    // ------------------------------------------------------------------ keyboard (hotkeys, stealth trigger, panic)

    private void OnKeyInput(int vk, bool ctrl, bool appActive)
    {
        // Esc is the panic button. (With the optional global hook it also works while another window
        // is in front; with the default WPF input it works whenever this app is active.)
        if (vk == NativeMethods.VK_ESCAPE)
        {
            if (!_anon && _codeBox != null)
            {
                _pinDetector.Reset();   // Esc closes the passcode box
                HidePasscodeBox();
                return;
            }
            if (_anon) ExitAnonymous();
            else if (appActive && _isFullscreen && !_modalOpen) ToggleFullscreen();
            return;
        }

        if (_modalOpen || _transition || TempoDialog.IsOpen) return; // typing in a dialog must not trigger hotkeys

        if (!appActive)
        {
            _pinDetector.Reset();
            HidePasscodeBox();
            return;
        }

        // The passcode box is open: it works like a small input. Backspace erases the last digit, Delete clears everything, and the
        // digits are typed with or without Ctrl (Esc, above, closes it).
        bool codeBoxOpen = !_anon && _codeBox != null && PinStore.IsSet;
        if (codeBoxOpen && vk is 0x08 or 0x2E)
        {
            if (vk == 0x08) _pinDetector.Backspace(); else _pinDetector.Reset();
            ShowPasscodeProgress();
            return;
        }

        // Ctrl + passcode digits
        if ((ctrl || codeBoxOpen) && PinDetector.TryGetDigit(vk, out char digit))
        {
            if (!_anon && PinStore.IsSet)
            {
                _pinDetector.IdleSeconds = _cfg.AnonymousPasscodeBox ? 8 : 3;   // with the box there is time to correct a mistake
                if (_pinDetector.Push(digit))
                {
                    HidePasscodeBox();   // the box goes before the "going dark" animation starts
                    EnterAnonymous();
                }
                else ShowPasscodeProgress();
            }
            return;
        }

        // Modifier presses shouldn't break a passcode in progress.
        if (vk is NativeMethods.VK_CONTROL or NativeMethods.VK_LCONTROL or NativeMethods.VK_RCONTROL
            or 0x10 or 0xA0 or 0xA1 or 0x12 or 0xA4 or 0xA5)
            return;

        _pinDetector.Reset();
        HidePasscodeBox();

        if (Keyboard.FocusedElement is TextBox or PasswordBox) return; // typing in the search box

        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrl)
        {
            if (vk == NativeMethods.VK_O) Open_Click(this, new RoutedEventArgs());
            else if (vk == NativeMethods.VK_OEM_COMMA) AllSettings_Click(this, new RoutedEventArgs());
            return;
        }

        if (_libraryVisible)
        {
            // Library: only a few keys make sense.
            if (vk == NativeMethods.VK_SPACE) TogglePlayPause();
            return;
        }

        switch (vk)
        {
            case NativeMethods.VK_SPACE: TogglePlayPause(); break;
            case NativeMethods.VK_LEFT: SeekBy(-_cfg.SkipSeconds); break;
            case NativeMethods.VK_RIGHT: SeekBy(_cfg.SkipSeconds); break;
            case NativeMethods.VK_UP: ChangeVolume(+5); break;
            case NativeMethods.VK_DOWN: ChangeVolume(-5); break;
            case NativeMethods.VK_F11:
            case NativeMethods.VK_F: ToggleFullscreen(); break;
            case NativeMethods.VK_M: ToggleMute(); break;
            case NativeMethods.VK_V: CycleTrack(audio: false); break;
            case NativeMethods.VK_C: CycleCrop(); break;
            case NativeMethods.VK_H: ToggleHideUi(); break;
            case NativeMethods.VK_OEM_4: NudgeLyricOffset(-100); break;
            case NativeMethods.VK_OEM_6: NudgeLyricOffset(+100); break;
            case NativeMethods.VK_A: CycleTrack(audio: true); break;
            case NativeMethods.VK_B: ToggleRail(); break;
            case NativeMethods.VK_Q:
                ShowSide(SideMode.Queue);
                break;
            case NativeMethods.VK_P:
                ShowSide(SideMode.Playlists);
                break;
            case NativeMethods.VK_BACK:
                ShowLibrary(true);
                break;
            case NativeMethods.VK_OEM_COMMA: if (shift) PlayAdjacent(-1); break;
            case NativeMethods.VK_OEM_PERIOD:
                if (shift) PlayAdjacent(+1);
                else if (_player != null && _hasMedia) { _player.SetPause(true); _player.NextFrame(); }
                break;
            case NativeMethods.VK_S: if (shift) TakeSnapshot(); break;
        }
    }
}
