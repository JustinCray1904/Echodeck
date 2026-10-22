using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace EchoDeck;

/// <summary>Full settings page for Anonymous Mode: the encrypted library (import files / folders), passcode and security.</summary>
public partial class AnonymousSettingsWindow : Window
{
    private readonly VaultService _service;
    private readonly Func<VaultItem, string?> _play;
    private readonly Func<VaultItem, Task<string?>> _delete;
    private readonly ObservableCollection<VaultItem> _items = new();
    private readonly DispatcherTimer _confirmTimer;
    private readonly AppSettings _cfg = AppSettings.Current;
    private VaultItem? _pendingDelete;
    private bool _busy;
    private bool _closed;

    public AnonymousSettingsWindow(VaultService service, Func<VaultItem, string?> play, Func<VaultItem, Task<string?>> delete)
    {
        _service = service;
        _play = play;
        _delete = delete;

        InitializeComponent();
        WindowHelper.AttachChrome(this);
        LibList.ItemsSource = _items;

        DeleteOriginalsSwitch.IsChecked = _cfg.AnonymousDeleteOriginals;
        ExitMinimizeSwitch.IsChecked = _cfg.AnonymousExitOnMinimize;

        _confirmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _confirmTimer.Tick += (_, _) => ResetDeleteConfirm();

        Loaded += async (_, _) => await RefreshAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _confirmTimer.Stop();
        LibList.ItemsSource = null;
        _items.Clear();
        CurrentBox.Clear();
        NewBox.Clear();
        ConfirmBox.Clear();
        base.OnClosed(e);
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageLibrary == null || PagePasscode == null || PageSecurity == null) return; // still loading
        PageLibrary.Visibility = NavLibrary.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PagePasscode.Visibility = NavPasscode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageSecurity.Visibility = NavSecurity.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ library

    private async Task RefreshAsync()
    {
        try
        {
            var items = await _service.ListAsync();
            if (_closed) return;

            _items.Clear();
            foreach (var item in items) _items.Add(item);

            var size = VaultService.FormatSize(_service.TotalBytes());
            LibSummary.Text = (_items.Count == 1 ? "1 encrypted item" : $"{_items.Count} encrypted items") + " · " + size;
        }
        catch (Exception ex)
        {
            if (!_closed) LibStatus.Text = ex.Message;
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ImportFilesButton.IsEnabled = !busy;
        ImportFolderButton.IsEnabled = !busy;
        PlayButton.IsEnabled = !busy;
        DeleteButton.IsEnabled = !busy;
    }

    private async void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dlg = new OpenFileDialog { Title = "Import into Anonymous Mode", Filter = MediaTypes.DialogFilter, Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        await RunImportAsync(dlg.FileNames.ToList());
    }

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dlg = new OpenFolderDialog { Title = "Import a folder into Anonymous Mode" };
        if (dlg.ShowDialog(this) != true) return;

        SetBusy(true);
        LibStatus.Text = "Scanning folder…";
        List<string> files;
        try { files = await Task.Run(() => VaultImporter.CollectFolder(dlg.FolderName)); }
        catch (Exception ex)
        {
            LibStatus.Text = "Couldn't read the folder: " + ex.Message;
            SetBusy(false);
            return;
        }
        SetBusy(false);

        if (files.Count == 0)
        {
            LibStatus.Text = "No supported media files found in that folder.";
            return;
        }
        await RunImportAsync(files);
    }

    private async Task RunImportAsync(IReadOnlyList<string> files)
    {
        SetBusy(true);
        try
        {
            var progress = new Progress<string>(m => { if (!_closed) LibStatus.Text = m; });
            var result = await VaultImporter.ImportAsync(
                _service, files, _cfg.AnonymousDeleteOriginals, progress, () => _closed);
            if (_closed) return;

            await RefreshAsync();
            LibStatus.Text = VaultImporter.Summary(result);
        }
        catch (Exception ex)
        {
            if (!_closed) LibStatus.Text = "Import failed: " + ex.Message;
        }
        finally
        {
            if (!_closed) SetBusy(false);
        }
    }

    private void DeleteOriginals_Click(object sender, RoutedEventArgs e)
    {
        _cfg.AnonymousDeleteOriginals = DeleteOriginalsSwitch.IsChecked == true;
        _cfg.Save(notify: false);
    }

    private void Play_Click(object sender, RoutedEventArgs e) => PlaySelected();

    private void LibList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => PlaySelected();

    private void LibList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ResetDeleteConfirm();

    private void PlaySelected()
    {
        if (_busy) return;
        if (LibList.SelectedItem is not VaultItem item)
        {
            LibStatus.Text = "Select an item first.";
            return;
        }

        var error = _play(item);
        if (error == null) DialogResult = true; // back to the player
        else LibStatus.Text = error;
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (LibList.SelectedItem is not VaultItem item)
        {
            LibStatus.Text = "Select an item first.";
            return;
        }

        if (_pendingDelete != item)
        {
            _pendingDelete = item;
            DeleteButton.Content = "Sure?";
            LibStatus.Text = "Click again to permanently delete “" + item.Name + "”.";
            _confirmTimer.Stop();
            _confirmTimer.Start();
            return;
        }

        ResetDeleteConfirm();
        SetBusy(true);
        LibStatus.Text = "Deleting…";
        try
        {
            var error = await _delete(item);
            await RefreshAsync();
            if (!_closed) LibStatus.Text = error ?? "Deleted permanently.";
        }
        finally
        {
            if (!_closed) SetBusy(false);
        }
    }

    private void ResetDeleteConfirm()
    {
        _confirmTimer.Stop();
        if (_pendingDelete == null) return;
        _pendingDelete = null;
        DeleteButton.Content = "Delete";
    }

    // ------------------------------------------------------------------ passcode + security

    private void Digits_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(c => c is >= '0' and <= '9');

    private void SavePasscode_Click(object sender, RoutedEventArgs e)
    {
        var error = PasscodeForm.TrySet(CurrentBox.Password, NewBox.Password, ConfirmBox.Password);
        if (error != null)
        {
            PasscodeStatus.Text = error;
            return;
        }

        CurrentBox.Clear();
        NewBox.Clear();
        ConfirmBox.Clear();
        PasscodeStatus.Text = "Passcode updated. Use Ctrl + the new passcode next time.";
    }

    private void ExitMinimize_Click(object sender, RoutedEventArgs e)
    {
        _cfg.AnonymousExitOnMinimize = ExitMinimizeSwitch.IsChecked == true;
        _cfg.Save(notify: false);
    }
}
