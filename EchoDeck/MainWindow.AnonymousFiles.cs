using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;

namespace EchoDeck;

public partial class MainWindow
{
    // ------------------------------------------------------------------ import

    private void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        if (!_anon || _importBusy) return;

        string[] names;
        _modalOpen = true;
        try
        {
            var dlg = new OpenFileDialog { Title = "Import into Anonymous Mode", Filter = MediaTypes.DialogFilter, Multiselect = true };
            if (dlg.ShowDialog(this) != true) return;
            names = dlg.FileNames;
        }
        finally { _modalOpen = false; }

        _ = ImportPathsAsync(names.Where(MediaTypes.IsSupportedFile).ToList());
    }

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!_anon || _importBusy) return;

        string folder;
        _modalOpen = true;
        try
        {
            var dlg = new OpenFolderDialog { Title = "Import a folder into Anonymous Mode" };
            if (dlg.ShowDialog(this) != true) return;
            folder = dlg.FolderName;
        }
        finally { _modalOpen = false; }

        var files = await Task.Run(() => VaultImporter.CollectFolder(folder));
        await ImportPathsAsync(files);
    }

    private async Task ImportDroppedAsync(string[] items)
    {
        if (!_anon || _importBusy) return;

        var files = new List<string>();
        foreach (var path in items)
        {
            if (Directory.Exists(path)) files.AddRange(await Task.Run(() => VaultImporter.CollectFolder(path)));
            else if (MediaTypes.IsSupportedFile(path)) files.Add(path);
        }
        await ImportPathsAsync(files);
    }

    private async Task ImportPathsAsync(List<string> files)
    {
        if (!_anon || files.Count == 0 || _importBusy) return;
        _importBusy = true;
        int total = files.Count;

        try
        {
            ShowLoading("Encrypting…", "0 / " + total.ToString("N0"), 0);
            var text = new Progress<string>(t => LoadText.Text = t);
            var fraction = new Progress<double>(f =>
                ShowLoading(LoadText.Text, ((int)Math.Round(f * total)).ToString("N0") + " / " + total.ToString("N0"), f));

            var result = await VaultImporter.ImportAsync(_vaultService, files, _cfg.AnonymousDeleteOriginals,
                text, () => !_anon, fraction);

            if (_anon)
            {
                Notify(VaultImporter.Summary(result));
                await ReloadAnonAsync();
            }
        }
        catch (Exception ex)
        {
            Notify("Import failed: " + ex.Message);
        }
        finally
        {
            _importBusy = false;
            HideLoading();
        }
    }

    // ------------------------------------------------------------------ restore / delete

    private List<VaultItem> ItemsOf(IEnumerable<BrowserEntry> entries) =>
        entries.Select(e => _anonItems.TryGetValue(e.FullPath, out var it) ? it : null)
               .Where(it => it != null).Select(it => it!).ToList();

    /// <summary>The ticked items when the clicked tile is one of them, otherwise just the clicked tile.</summary>
    private List<VaultItem> RestoreTargets(BrowserEntry entry)
    {
        var ticked = CheckedFiles().ToList();
        return ItemsOf(entry.IsChecked && ticked.Count > 0 ? ticked : new List<BrowserEntry> { entry });
    }

    private void RestoreSelected_Click(object sender, RoutedEventArgs e)
    {
        var targets = ItemsOf(CheckedFiles());
        if (targets.Count == 0)
        {
            Notify("Tick the items you want to restore first");
            return;
        }
        _ = RestoreEntriesAsync(targets);
    }

    private async Task RestoreEntriesAsync(List<VaultItem> items)
    {
        if (!_anon || items.Count == 0 || _importBusy) return;
        _importBusy = true;
        _modalOpen = true;

        try
        {
            var fraction = new Progress<double>(f =>
                ShowLoading("Restoring…", ((int)Math.Round(f * items.Count)).ToString("N0") + " / " + items.Count.ToString("N0"), f));
            var message = await VaultRestore.RunManyAsync(this, _vaultService, items, DeleteVaultItemAsync, fraction);
            if (!string.IsNullOrEmpty(message)) Notify(message);
        }
        catch (Exception ex)
        {
            Notify("Restore failed: " + ex.Message);
        }
        finally
        {
            _modalOpen = false;
            _importBusy = false;
            HideLoading();
        }

        await ReloadAnonAsync();
    }

    private async Task DeleteEntriesAsync(List<VaultItem> items)
    {
        if (!_anon || items.Count == 0 || _importBusy) return;

        _modalOpen = true;
        MessageBoxResult answer;
        try
        {
            answer = TempoDialog.Show(this,
                (items.Count == 1 ? "Permanently delete “" + DisplayTitle(items[0]) + "”" : $"Permanently delete {items.Count} items") +
                " from Anonymous Mode?\n\nThe encrypted file is overwritten and removed. This can't be undone.",
                "Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        }
        finally { _modalOpen = false; }
        if (answer != MessageBoxResult.Yes) return;

        int failed = 0;
        string? lastError = null;
        foreach (var item in items)
        {
            var error = await DeleteVaultItemAsync(item);
            if (error != null) { failed++; lastError = error; }
        }

        Notify(failed == 0 ? (items.Count == 1 ? "Deleted" : $"Deleted {items.Count} items") : $"{failed} couldn't be deleted. {lastError}");
        await ReloadAnonAsync();
    }

    private void RemoveFromQueue(string path)
    {
        int idx = _queue.FindIndex(q => string.Equals(q.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;
        _queue.RemoveAt(idx);
        if (idx < _queueIndex) _queueIndex--;
        else if (idx == _queueIndex) _queueIndex = -1;
    }
}
