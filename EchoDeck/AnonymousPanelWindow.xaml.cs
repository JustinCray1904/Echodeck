using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace EchoDeck;

/// <summary>The hover panel of Anonymous Mode: search, filter, play, import (buttons or drag-and-drop) and delete.</summary>
public partial class AnonymousPanelWindow : Window
{
    private const double PillW = 48, PillH = 28, DeckW = 402, DeckH = 558;
    private static readonly Duration Dur = new(TimeSpan.FromMilliseconds(190));

    private readonly VaultService _service;
    private readonly ObservableCollection<VaultItem> _items = new();
    private List<VaultItem> _all = new();
    private readonly DispatcherTimer _collapseTimer;   // small delay so a fast mouse doesn't flicker the panel
    private readonly DispatcherTimer _confirmTimer;    // "click the bin again to confirm" window
    private VaultItem? _pendingDelete;
    private bool _sortDesc;
    private bool _dialogOpen;
    private bool _busy;
    private bool _wiped;

    /// <summary>Plays the item; returns an error message, or null on success.</summary>
    public Func<VaultItem, string?>? PlayHandler { get; set; }

    /// <summary>Releases any open handle and permanently deletes the item; returns an error message, or null.</summary>
    public Func<VaultItem, Task<string?>>? DeleteHandler { get; set; }

    /// <summary>Raised when Esc / the close button asks to leave Anonymous Mode.</summary>
    public event Action? EscapePressed;

    /// <summary>Raised when the gear button is clicked (the main window opens the Anonymous Mode settings page).</summary>
    public event Action? SettingsRequested;

    public void SetStatus(string text)
    {
        if (!_wiped) StatusText.Text = text;
    }

    public AnonymousPanelWindow(VaultService service, string? startupError = null)
    {
        _service = service;
        InitializeComponent();
        VaultList.ItemsSource = _items;

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!Shell.IsMouseOver && !_dialogOpen) SetExpanded(false);
        };

        _confirmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _confirmTimer.Tick += (_, _) => ResetDeleteConfirm();

        if (startupError != null)
        {
            StatusText.Text = startupError;
            SetBusy(true);
        }
        else
        {
            Loaded += async (_, _) => await InitAsync();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Clicking the panel must never steal focus from the player, and it stays out of Alt-Tab.
        NativeMethods.MakeNonActivating(new WindowInteropHelper(this).Handle);
    }

    private async Task InitAsync()
    {
        await RefreshAsync();
        try
        {
            int converted = await _service.MigrateLegacyAsync(); // encrypts plaintext files left by very old versions
            if (_wiped) return;
            if (converted > 0)
            {
                await RefreshAsync();
                if (!_wiped) StatusText.Text = $"Encrypted {converted} existing file(s)";
            }
        }
        catch (Exception ex)
        {
            if (!_wiped) StatusText.Text = ex.Message;
        }
    }

    public async Task RefreshAsync()
    {
        try
        {
            var items = await _service.ListAsync();
            if (_wiped) return;
            _all = items.ToList();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            if (!_wiped) StatusText.Text = ex.Message;
        }
    }

    private void ApplyFilter()
    {
        if (_wiped) return;

        var text = SearchBox.Text.Trim();
        SearchHint.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        IEnumerable<VaultItem> q = _all;
        if (ChipVideo.IsChecked == true) q = q.Where(i => !i.IsAudio);
        else if (ChipAudio.IsChecked == true) q = q.Where(i => i.IsAudio);
        if (text.Length > 0) q = q.Where(i => i.Name.IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0);

        q = _sortDesc
            ? q.OrderByDescending(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            : q.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase);

        _items.Clear();
        foreach (var item in q) _items.Add(item);

        long total = _service.TotalBytes();
        CountText.Text = (_all.Count == 1 ? "1 item" : $"{_all.Count} items") + "  ·  " + VaultService.FormatSize(total);

        EmptyPanel.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _all.Count == 0
            ? "Nothing here yet. Drop files or a folder onto this panel, or use the buttons below."
            : "No matches.";
    }

    /// <summary>Drops every reference to vault content so nothing lingers after the window closes.</summary>
    public void Wipe()
    {
        _wiped = true;
        _collapseTimer.Stop();
        _confirmTimer.Stop();
        PlayHandler = null;
        DeleteHandler = null;
        EscapePressed = null;
        SettingsRequested = null;
        _pendingDelete = null;
        VaultList.ItemsSource = null;
        _items.Clear();
        _all = new List<VaultItem>();
        SearchBox.Text = "";
        StatusText.Text = string.Empty;
        CountText.Text = string.Empty;
    }

    // ------------------------------------------------------------------ pill <-> panel

    private void Shell_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        SetExpanded(true);
    }

    private void Shell_MouseLeave(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void SetExpanded(bool expanded)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        DoubleAnimation Anim(double to) => new(to, Dur) { EasingFunction = ease };

        Shell.BeginAnimation(WidthProperty, Anim(expanded ? DeckW : PillW));
        Shell.BeginAnimation(HeightProperty, Anim(expanded ? DeckH : PillH));
        Shell.BeginAnimation(OpacityProperty, Anim(expanded ? 1.0 : 0.45));
        Deck.BeginAnimation(OpacityProperty, new DoubleAnimation(
            expanded ? 1.0 : 0.0,
            new Duration(TimeSpan.FromMilliseconds(expanded ? 150 : 80)))
        {
            BeginTime = TimeSpan.FromMilliseconds(expanded ? 70 : 0)
        });
        PillIcon.BeginAnimation(OpacityProperty, new DoubleAnimation(
            expanded ? 0.0 : 1.0, new Duration(TimeSpan.FromMilliseconds(90))));
        Deck.IsHitTestVisible = expanded;

        if (!expanded) ResetDeleteConfirm();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ImportFilesButton.IsEnabled = !busy;
        ImportFolderButton.IsEnabled = !busy;
        PlayButton.IsEnabled = !busy;
        ImportTrack.Visibility = busy && _importing ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ filters

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyFilter();
    }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyFilter();
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        _sortDesc = !_sortDesc;
        ApplyFilter();
    }

    // ------------------------------------------------------------------ importing

    private bool _importing;

    private async void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _dialogOpen = true; // keep the panel open while the picker has the mouse
        try
        {
            var dlg = new OpenFileDialog
            {
                Title = "Import into Anonymous Mode",
                Filter = MediaTypes.DialogFilter,
                Multiselect = true
            };
            if (dlg.ShowDialog(this) != true) return;
            await RunImportAsync(dlg.FileNames.ToList());
        }
        catch (Exception ex)
        {
            if (!_wiped) StatusText.Text = "Import failed: " + ex.Message;
        }
        finally
        {
            _dialogOpen = false;
            if (!_wiped && !Shell.IsMouseOver) SetExpanded(false);
        }
    }

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _dialogOpen = true;
        try
        {
            var dlg = new OpenFolderDialog { Title = "Import a folder into Anonymous Mode" };
            if (dlg.ShowDialog(this) != true) return;
            await ImportFolderAsync(dlg.FolderName);
        }
        catch (Exception ex)
        {
            if (!_wiped) StatusText.Text = "Import failed: " + ex.Message;
        }
        finally
        {
            _dialogOpen = false;
            if (!_wiped && !Shell.IsMouseOver) SetExpanded(false);
        }
    }

    private async Task ImportFolderAsync(string folder)
    {
        StatusText.Text = "Scanning folder…";
        var files = await Task.Run(() => VaultImporter.CollectFolder(folder));
        if (files.Count == 0)
        {
            StatusText.Text = "No supported media files in that folder";
            return;
        }
        await RunImportAsync(files);
    }

    /// <summary>Drag files or a folder from Explorer onto the panel.</summary>
    private async void Shell_Drop(object sender, DragEventArgs e)
    {
        if (_busy || _wiped) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] items || items.Length == 0) return;

        try
        {
            var files = new List<string>();
            foreach (var item in items)
            {
                if (Directory.Exists(item)) files.AddRange(await Task.Run(() => VaultImporter.CollectFolder(item)));
                else if (MediaTypes.IsSupportedFile(item)) files.Add(item);
            }
            if (files.Count == 0)
            {
                StatusText.Text = "Nothing importable in what you dropped";
                return;
            }
            await RunImportAsync(files);
        }
        catch (Exception ex)
        {
            if (!_wiped) StatusText.Text = "Import failed: " + ex.Message;
        }
    }

    private async Task RunImportAsync(IReadOnlyList<string> files)
    {
        _importing = true;
        SetBusy(true);
        ImportFill.Width = 0;
        try
        {
            var progress = new Progress<string>(m => { if (!_wiped) StatusText.Text = m; });
            var fraction = new Progress<double>(f =>
            {
                if (_wiped) return;
                ImportFill.Width = ImportTrack.ActualWidth * Math.Clamp(f, 0, 1);
            });

            var result = await VaultImporter.ImportAsync(
                _service, files, AppSettings.Current.AnonymousDeleteOriginals, progress, () => _wiped, fraction);
            if (_wiped) return;

            await RefreshAsync();
            StatusText.Text = VaultImporter.Summary(result);
        }
        finally
        {
            _importing = false;
            if (!_wiped) SetBusy(false);
        }
    }

    // ------------------------------------------------------------------ play / delete

    private void Play_Click(object sender, RoutedEventArgs e) => PlaySelected();

    private void VaultList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => PlaySelected();

    private void VaultList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ResetDeleteConfirm();

    private void PlaySelected()
    {
        if (_busy) return;
        if (VaultList.SelectedItem is not VaultItem item)
        {
            StatusText.Text = "Select an item first";
            return;
        }

        var error = PlayHandler?.Invoke(item); // the panel stays open for further browsing
        StatusText.Text = error ?? "Playing: " + item.Name;
    }

    /// <summary>First click on the bin arms it; a second click within 3 seconds deletes. No pop-up dialogs.</summary>
    private async void RowDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || (sender as FrameworkElement)?.DataContext is not VaultItem item) return;

        if (_pendingDelete != item)
        {
            _pendingDelete = item;
            StatusText.Text = "Click the bin again to permanently delete “" + item.Name + "”";
            _confirmTimer.Stop();
            _confirmTimer.Start();
            return;
        }

        ResetDeleteConfirm();
        var handler = DeleteHandler;
        if (handler == null) return;

        SetBusy(true);
        StatusText.Text = "Deleting…";
        try
        {
            var error = await handler(item);
            await RefreshAsync();
            if (!_wiped) StatusText.Text = error ?? "Deleted permanently";
        }
        finally
        {
            if (!_wiped) SetBusy(false);
        }
    }

    private async void RowRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || (sender as FrameworkElement)?.DataContext is not VaultItem item) return;
        var delete = DeleteHandler;
        if (delete == null) return;

        _dialogOpen = true;
        SetBusy(true);
        try
        {
            var message = await VaultRestore.RunAsync(this, _service, item, delete);
            if (_wiped) return;
            if (message.Length > 0)
            {
                await RefreshAsync();
                StatusText.Text = message;
            }
        }
        catch (Exception ex)
        {
            if (!_wiped) StatusText.Text = "Restore failed: " + ex.Message;
        }
        finally
        {
            _dialogOpen = false;
            if (!_wiped) SetBusy(false);
        }
    }

    private void ResetDeleteConfirm()
    {
        _confirmTimer.Stop();
        _pendingDelete = null;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SettingsRequested?.Invoke();
    }

    private void Leave_Click(object sender, RoutedEventArgs e) => EscapePressed?.Invoke();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            EscapePressed?.Invoke();
        }
    }
}
