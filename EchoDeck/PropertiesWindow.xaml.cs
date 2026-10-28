using System.Windows;

namespace EchoDeck;

/// <summary>Compact "Properties" card for a media file.</summary>
public partial class PropertiesWindow : Window
{
    private readonly string _path;

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

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "explorer.exe", "/select,\"" + _path + "\"") { UseShellExecute = true });
        }
        catch { /* nothing to open */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
