using System.Windows;
using Microsoft.Win32;

namespace EchoDeck;

/// <summary>"Restore": take a file out of Anonymous Mode again, back to where it came from (or anywhere you choose).</summary>
internal static class VaultRestore
{
    /// <returns>A status message for the UI; empty when the user cancelled.</returns>
    public static async Task<string> RunAsync(Window owner, VaultService service, VaultItem item,
        Func<VaultItem, Task<string?>> delete)
    {
        var dlg = new OpenFolderDialog { Title = "Restore “" + item.Name + "” to…" };
        dlg.InitialDirectory = !string.IsNullOrEmpty(item.Origin) && Directory.Exists(item.Origin)
            ? item.Origin
            : Environment.GetFolderPath(item.IsAudio ? Environment.SpecialFolder.MyMusic : Environment.SpecialFolder.MyVideos);
        if (dlg.ShowDialog(owner) != true) return "";

        var answer = MessageBox.Show(owner,
            "Remove “" + item.Name + "” from Anonymous Mode after restoring it?\n\n" +
            "Yes  –  move it back (the encrypted copy is deleted)\nNo  –  copy it out and keep it here",
            "Restore", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return "";

        string saved = await service.RestoreAsync(item, dlg.FolderName);
        if (answer != MessageBoxResult.Yes) return "Copied to " + saved;

        var error = await delete(item);
        return error == null ? "Moved back to " + saved : "Restored to " + saved + ", but couldn't remove it: " + error;
    }
}
