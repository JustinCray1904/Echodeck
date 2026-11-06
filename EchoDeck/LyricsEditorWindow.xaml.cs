using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace EchoDeck;

/// <summary>
/// Lyrics editor: paste text, stamp each line with the current playback time, nudge all timings, save as .lrc / .txt.
/// It stays open (not modal) while the song plays so lines can be timed by ear.
/// </summary>
public partial class LyricsEditorWindow : Window
{
    private readonly Func<long> _getTimeMs;
    private readonly Action _togglePlay;
    private readonly Action<long> _seekBy;
    private readonly Func<string, string?> _save;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(120) };

    /// <param name="save">Receives the text; returns null on success or a message to show.</param>
    public LyricsEditorWindow(string songName, string initialText, string saveHint,
        Func<long> getTimeMs, Action togglePlay, Action<long> seekBy, Func<string, string?> save)
    {
        InitializeComponent();
        WindowHelper.AttachChrome(this);

        _getTimeMs = getTimeMs;
        _togglePlay = togglePlay;
        _seekBy = seekBy;
        _save = save;

        SongText.Text = songName;
        Editor.Text = initialText;
        StatusText.Text = saveHint;

        _clock.Tick += (_, _) => TimeText.Text = LyricsParser.FormatTime(_getTimeMs()).Trim('[', ']');
        _clock.Start();
        Closed += (_, _) => _clock.Stop();
        Loaded += (_, _) => Editor.Focus();
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch { StatusText.Text = "The clipboard is busy. Try again."; return; }

        if (text.Length == 0) { StatusText.Text = "There is no text on the clipboard."; return; }

        if (Editor.Text.Trim().Length == 0)
            Editor.Text = text;
        else
            Editor.SelectedText = text;   // replaces the selection, or inserts at the caret
        Editor.Focus();
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) { _togglePlay(); Editor.Focus(); }
    private void SeekBack_Click(object sender, RoutedEventArgs e) { _seekBy(-5000); Editor.Focus(); }
    private void SeekForward_Click(object sender, RoutedEventArgs e) { _seekBy(5000); Editor.Focus(); }

    private void Stamp_Click(object sender, RoutedEventArgs e) { StampCurrentLine(); Editor.Focus(); }

    private void StampCurrentLine()
    {
        int lineIndex = Editor.GetLineIndexFromCharacterIndex(Editor.CaretIndex);
        if (lineIndex < 0) lineIndex = 0;

        var text = Editor.Text;
        int start = Editor.GetCharacterIndexFromLineIndex(lineIndex);
        if (start < 0) start = 0;
        int end = text.IndexOf('\n', start);
        if (end < 0) end = text.Length;

        string line = text.Substring(start, end - start);
        bool hadCr = line.EndsWith('\r');
        if (hadCr) line = line.Substring(0, line.Length - 1);

        string stamped = LyricsParser.StampLine(line, _getTimeMs());
        Editor.Select(start, line.Length);
        Editor.SelectedText = stamped;

        // move to the next line, ready for the next stamp
        int next = Editor.GetCharacterIndexFromLineIndex(lineIndex + 1);
        Editor.CaretIndex = next >= 0 ? next : Editor.Text.Length;
        Editor.ScrollToLine(Math.Min(lineIndex + 1, Math.Max(0, Editor.LineCount - 1)));
    }

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            StampCurrentLine();
            e.Handled = true;
        }
    }

    private void ShiftEarlier_Click(object sender, RoutedEventArgs e) { Editor.Text = LyricsParser.Shift(Editor.Text, -500); Editor.Focus(); }
    private void ShiftLater_Click(object sender, RoutedEventArgs e) { Editor.Text = LyricsParser.Shift(Editor.Text, +500); Editor.Focus(); }

    private void StripTimes_Click(object sender, RoutedEventArgs e)
    {
        if (!LyricsParser.HasTimestamps(Editor.Text)) return;
        if (MessageBox.Show(this, "Remove every timestamp and keep only the words?", "Edit lyrics",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Editor.Text = LyricsParser.StripTimes(Editor.Text);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var error = _save(Editor.Text);
        if (error == null) { Close(); return; }
        StatusText.Text = error;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
