using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DeefxultMediaPlayer;

public partial class VaultOverlayWindow : Window
{
    private const double PillW = 46, PillH = 26, DeckW = 300, DeckH = 340;
    private static readonly Duration Dur = new(TimeSpan.FromMilliseconds(180));

    private readonly VaultService _service;
    private readonly ObservableCollection<VaultItem> _items = new();
    private readonly DispatcherTimer _collapseTimer;   // small delay so a fast mouse doesn't flicker the deck
    private readonly DispatcherTimer _confirmTimer;    // "click Delete again to confirm" window
    private VaultItem? _pendingDelete;
    private bool _dialogOpen;
    private bool _busy;
    private bool _wiped;

    /// <summary>Plays the item; returns an error message, or null on success.</summary>
    public Func<VaultItem, string?>? PlayHandler { get; set; }

    /// <summary>Releases any open handle and permanently deletes the item; returns an error message, or null.</summary>
    public Func<VaultItem, Task<string?>>? DeleteHandler { get; set; }

    /// <summary>Raised when Esc reaches this window (fallback; the main window also handles Esc).</summary>
    public event Action? EscapePressed;

    /// <summary>Raised when the gear button is clicked (the main window runs the in-app PIN change).</summary>
    public event Action? ChangePinRequested;

    /// <summary>Shows a message in the deck's status line.</summary>
    public void SetStatus(string text)
    {
        if (!_wiped) StatusText.Text = text;
    }

    public VaultOverlayWindow(VaultService service, string? startupError = null)
    {
        _service = service;
        InitializeComponent();
        VaultList.ItemsSource = _items;

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
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
            SetBusy(true); // vault is locked: nothing to do until it's reopened
        }
        else
        {
            Loaded += async (_, _) => await InitAsync();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Clicking the overlay must never steal focus from the player, and it stays out of Alt-Tab.
        NativeMethods.MakeNonActivating(new WindowInteropHelper(this).Handle);
    }

    private async Task InitAsync()
    {
        await RefreshAsync();
        try
        {
            int converted = await _service.MigrateLegacyAsync(); // encrypts files left by the old plaintext version
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

            _items.Clear();
            foreach (var item in items) _items.Add(item);
            CountText.Text = _items.Count == 1 ? "1 video" : $"{_items.Count} videos";
        }
        catch (Exception ex)
        {
            if (!_wiped) StatusText.Text = ex.Message;
        }
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
        ChangePinRequested = null;
        _pendingDelete = null;
        VaultList.ItemsSource = null;
        _items.Clear();
        StatusText.Text = string.Empty;
        CountText.Text = string.Empty;
    }

    // ------------------------------------------------------------------ pill <-> deck

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
        Shell.BeginAnimation(OpacityProperty, Anim(expanded ? 1.0 : 0.4));
        Deck.BeginAnimation(OpacityProperty, new DoubleAnimation(
            expanded ? 1.0 : 0.0,
            new Duration(TimeSpan.FromMilliseconds(expanded ? 140 : 80)))
        {
            BeginTime = TimeSpan.FromMilliseconds(expanded ? 70 : 0)   // content appears as the pill opens
        });
        PillIcon.BeginAnimation(OpacityProperty, new DoubleAnimation(
            expanded ? 0.0 : 1.0, new Duration(TimeSpan.FromMilliseconds(90))));
        Deck.IsHitTestVisible = expanded;

        if (!expanded) ResetDeleteConfirm();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ImportButton.IsEnabled = !busy;
        PlayButton.IsEnabled = !busy;
        DeleteButton.IsEnabled = !busy;
    }

    // ------------------------------------------------------------------ actions

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _dialogOpen = true; // keep the deck open while the file picker has the mouse
        try
        {
            var dlg = new OpenFileDialog
            {
                Title = "Import videos into the vault",
                Filter = MediaTypes.DialogFilter,
                Multiselect = true
            };
            if (dlg.ShowDialog(this) != true) return;

            SetBusy(true);
            foreach (var file in dlg.FileNames)
            {
                if (_wiped) return;
                StatusText.Text = "Encrypting " + Path.GetFileName(file) + "...";
                await _service.ImportAsync(file);
            }
            await RefreshAsync();
            if (!_wiped) StatusText.Text = "Import complete (encrypted)";
        }
        catch (Exception ex)
        {
            if (!_wiped) StatusText.Text = "Import failed: " + ex.Message;
        }
        finally
        {
            _dialogOpen = false;
            if (!_wiped)
            {
                SetBusy(false);
                if (!Shell.IsMouseOver) SetExpanded(false);
            }
        }
    }

    private void Play_Click(object sender, RoutedEventArgs e) => PlaySelected();

    private void VaultList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => PlaySelected();

    private void VaultList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ResetDeleteConfirm();

    private void PlaySelected()
    {
        if (_busy) return;
        if (VaultList.SelectedItem is not VaultItem item)
        {
            StatusText.Text = "Select a video first";
            return;
        }

        var error = PlayHandler?.Invoke(item); // vault stays open for further browsing
        StatusText.Text = error ?? "Playing: " + item.Name;
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (VaultList.SelectedItem is not VaultItem item)
        {
            StatusText.Text = "Select a video first";
            return;
        }

        // First click arms the button; a second click within 3 seconds confirms. No pop-up dialogs.
        if (_pendingDelete != item)
        {
            _pendingDelete = item;
            DeleteButton.Content = "Sure?";
            StatusText.Text = "Click again to permanently delete";
            _confirmTimer.Stop();
            _confirmTimer.Start();
            return;
        }

        ResetDeleteConfirm();
        var handler = DeleteHandler;
        if (handler == null) return;

        SetBusy(true);
        StatusText.Text = "Deleting...";
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

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ChangePinRequested?.Invoke();
    }

    private void ResetDeleteConfirm()
    {
        _confirmTimer.Stop();
        if (_pendingDelete == null) return;
        _pendingDelete = null;
        DeleteButton.Content = "Delete";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            EscapePressed?.Invoke();
        }
    }
}
