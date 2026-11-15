using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EchoDeck;

/// <summary>
/// Finds songs that are in the library more than once: the same title (ignoring things like "(Official Audio)" or "[320 KBps]") and
/// a length within 3 seconds. In each group the biggest file is kept and the others are ticked; ticked copies go to the Recycle Bin
/// (so they can be restored) when the button is pressed. Nothing is deleted without that button and a confirmation.
/// </summary>
internal sealed class DuplicatesWindow : Window
{
    private readonly IReadOnlyList<string> _folders;
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _groups = new();
    private readonly Button _removeButton = new() { Content = "Move ticked copies to the Recycle Bin", Style = AppStyle("AccentButton"), IsEnabled = false };
    private readonly List<List<(CheckBox Box, string Path)>> _rows = new();

    private static Style AppStyle(string key) => (Style)Application.Current.FindResource(key);

    public DuplicatesWindow(IReadOnlyList<string> folders)
    {
        _folders = folders;
        Title = "Duplicate songs";
        Width = 780;
        Height = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = AppStyle("EchoWindow");   // the same title bar and look as the other windows

        var close = new Button { Content = "Close", Style = AppStyle("FlatButton"), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        _removeButton.Click += RemoveClick;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(_removeButton);
        buttons.Children.Add(close);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _groups };
        var root = new DockPanel { Margin = new Thickness(24, 0, 24, 20) };
        DockPanel.SetDock(_status, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(buttons);
        root.Children.Add(scroll);
        Content = root;

        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Loaded += async (_, _) => await ScanAsync();
    }

    private static string Key(string title)
    {
        string s = DisplayNames.Clean(title);
        s = Regex.Replace(s, @"[\(\[][^\)\]]*[\)\]]", "");
        return Regex.Replace(s, @"[^\p{L}\p{Nd}]", "").ToLowerInvariant();
    }

    private async Task ScanAsync()
    {
        _status.Text = "Looking through your music folders…";
        List<CachedTrack> tracks;
        try { tracks = await LibraryScanner.ScannedAsync(_folders); }
        catch (Exception ex)
        {
            AppLog.Write("Duplicates", ex);
            _status.Text = "Couldn't read the library: " + ex.Message;
            return;
        }

        var found = new List<List<CachedTrack>>();
        foreach (var byTitle in tracks
                     .Select(t => (Track: t, Key: Key(t.T.Length > 0 ? t.T : Path.GetFileNameWithoutExtension(t.P))))
                     .Where(x => x.Key.Length >= 4)
                     .GroupBy(x => x.Key))
        {
            // the same title and nearly the same length: one cluster per length
            List<CachedTrack>? cluster = null;
            foreach (var t in byTitle.Select(x => x.Track).OrderBy(t => t.D))
            {
                if (cluster == null || t.D - cluster[0].D > 3)
                {
                    if (cluster is { Count: > 1 }) found.Add(cluster);
                    cluster = new List<CachedTrack>();
                }
                cluster.Add(t);
            }
            if (cluster is { Count: > 1 }) found.Add(cluster);
        }

        if (found.Count == 0)
        {
            _status.Text = "No duplicates found.";
            return;
        }

        int extra = found.Sum(g => g.Count - 1);
        _status.Text = $"{found.Count} song{(found.Count == 1 ? "" : "s")} appear more than once ({extra} extra cop{(extra == 1 ? "y" : "ies")}). " +
                       "The biggest file of each is kept; the others are ticked. Untick anything you want to keep.";

        foreach (var group in found.OrderBy(g => g[0].T))
        {
            var keep = group.OrderByDescending(t => t.S).First();
            var box = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            var title = new TextBlock { Text = DisplayNames.Clean(keep.T.Length > 0 ? keep.T : Path.GetFileNameWithoutExtension(keep.P)), FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            box.Children.Add(title);

            var rows = new List<(CheckBox, string)>();
            foreach (var t in group.OrderByDescending(t => t.S))
            {
                var check = new CheckBox
                {
                    Style = AppStyle("TileCheck"),
                    IsChecked = !ReferenceEquals(t, keep),
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 10, 2),
                };
                check.Checked += (_, _) => UpdateButton();
                check.Unchecked += (_, _) => UpdateButton();

                var text = new TextBlock
                {
                    Text = $"{t.P}\n{t.S / 1048576.0:0.0} MB  ·  {t.D / 60}:{t.D % 60:00}{(ReferenceEquals(t, keep) ? "  ·  kept (the biggest file)" : "")}",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 0, 3),
                    Cursor = System.Windows.Input.Cursors.Hand,
                };
                text.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
                text.MouseLeftButtonUp += (_, _) => check.IsChecked = check.IsChecked != true;   // the text toggles it too

                var line = new Grid();
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.Children.Add(check);
                Grid.SetColumn(text, 1);
                line.Children.Add(text);
                box.Children.Add(line);
                rows.Add((check, t.P));
            }
            _rows.Add(rows);
            _groups.Children.Add(box);
        }
        UpdateButton();
    }

    private void UpdateButton() => _removeButton.IsEnabled = _rows.Any(g => g.Any(r => r.Box.IsChecked == true));

    private void RemoveClick(object sender, RoutedEventArgs e)
    {
        // never remove every copy of a song: if a group is fully ticked, its first copy is kept
        var doomed = new List<string>();
        foreach (var group in _rows)
        {
            var ticked = group.Where(r => r.Box.IsChecked == true).ToList();
            if (ticked.Count == group.Count) ticked.RemoveAt(0);
            doomed.AddRange(ticked.Select(r => r.Path));
        }
        if (doomed.Count == 0) return;

        if (MessageBox.Show(this, $"Move {doomed.Count} file{(doomed.Count == 1 ? "" : "s")} to the Recycle Bin?\n\nYou can restore them from there.",
                "Duplicate songs", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        int moved = 0;
        foreach (var path in doomed)
            if (ToRecycleBin(path)) moved++;

        _status.Text = $"{moved} of {doomed.Count} copies moved to the Recycle Bin" + (moved < doomed.Count ? " (the rest couldn't be moved: in use, or already gone)." : ".") +
                       " Reopen this window to check again.";
        _groups.Children.Clear();
        _rows.Clear();
        UpdateButton();
    }

    // ---- Recycle Bin (SHFileOperation with "allow undo")

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOp
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOp op);

    private static bool ToRecycleBin(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var op = new ShFileOp
            {
                wFunc = 3,                    // FO_DELETE
                pFrom = path + "\0\0",        // the list ends with a double null
                fFlags = 0x40 | 0x10 | 0x4,   // FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT
            };
            return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
        }
        catch (Exception ex)
        {
            AppLog.Write("Recycle", ex, path);
            return false;
        }
    }
}
