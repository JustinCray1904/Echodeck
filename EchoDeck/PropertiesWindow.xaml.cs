using System.Windows;

namespace EchoDeck;

/// <summary>Compact "Properties" card for a media file.</summary>
public partial class PropertiesWindow : Window
{
    private readonly string? _path;

    public PropertiesWindow(string path, string? length = null)
    {
        InitializeComponent();
        WindowHelper.AttachChrome(this);
        _path = path;

        try
        {
            var info = new FileInfo(path);
            TitleValue.Text = Path.GetFileNameWithoutExtension(path);
            LengthValue.Text = string.IsNullOrEmpty(length) ? "—" : length;
            SizeValue.Text = VaultService.FormatSize(info.Length);
            TypeValue.Text = (MediaTypes.IsAudio(path) ? "Audio" : "Video") + "  (" + info.Extension + ")";
            ModifiedValue.Text = info.LastWriteTime.ToString("g");
            LocationValue.Text = path;
        }
        catch (Exception ex)
        {
            TitleValue.Text = Path.GetFileName(path);
            LocationValue.Text = path + "\n" + ex.Message;
        }
    }

    /// <summary>Properties of an Anonymous Mode item: details come from the encrypted header, nothing from Explorer.</summary>
    public PropertiesWindow(VaultItem item)
    {
        InitializeComponent();
        WindowHelper.AttachChrome(this);
        _path = null;

        TitleValue.Text = string.IsNullOrWhiteSpace(item.Title) ? Path.GetFileNameWithoutExtension(item.Name) : item.Title;
        LengthValue.Text = item.DurationMs > 0 ? LibraryScanner.FormatDuration(item.DurationMs / 1000) : "—";
        SizeValue.Text = item.SizeText;
        TypeValue.Text = item.Kind + "  (" + Path.GetExtension(item.Name) + ")";
        ModifiedValue.Text = "—";
        LocationValue.Text = "Stored encrypted in Anonymous Mode." +
            (string.IsNullOrWhiteSpace(item.Origin) ? "" : "\nOriginally in: " + item.Origin);
        OpenLocationButton.Visibility = Visibility.Collapsed;
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (_path == null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "explorer.exe", "/select,\"" + _path + "\"") { UseShellExecute = true });
        }
        catch { /* nothing to open */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
