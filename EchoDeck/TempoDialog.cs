using System.Windows;
using System.Windows.Media;

namespace EchoDeck;

/// <summary>
/// Tempo's own message box, in Tempo's look, with the window behind it dimmed. It replaces the Windows message box everywhere:
/// <c>TempoDialog.Show(owner, text, caption, buttons, image)</c> takes the same arguments and gives the same <see cref="MessageBoxResult"/>,
/// and waits for the answer like <c>MessageBox.Show</c> did. If the custom window cannot be shown (something is badly wrong) the
/// Windows one is used instead, so a message is never lost.
/// </summary>
internal static class TempoDialog
{
    private static int _open;

    /// <summary>True while a Tempo dialog is on screen (keyboard shortcuts must not react then).</summary>
    public static bool IsOpen => _open > 0;

    public static MessageBoxResult Show(string text, string caption = "Tempo", MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
        => Show(null, text, caption, buttons, image, defaultResult);

    public static MessageBoxResult Show(Window? owner, string text, string caption = "Tempo", MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            return dispatcher.Invoke(() => Show(owner, text, caption, buttons, image, defaultResult));

        DialogScrim? scrim = null;
        try
        {
            owner = UsableOwner(owner);
            if (owner != null)
            {
                try
                {
                    scrim = new DialogScrim(owner);
                    scrim.Show();
                }
                catch
                {
                    scrim?.Close();
                    scrim = null;   // no dimming rather than no dialog
                }
            }

            var dialog = new TempoDialogWindow(text, caption, buttons, image, defaultResult);
            if (owner != null) dialog.Owner = owner;
            else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            _open++;
            try { dialog.ShowDialog(); }
            finally { _open--; }
            return dialog.Result;
        }
        catch (Exception ex)
        {
            AppLog.Write("Dialog", ex);
            scrim?.Close();
            scrim = null;
            return MessageBox.Show(text, caption, buttons, image, defaultResult);
        }
        finally
        {
            scrim?.Close();
            if (owner is { IsLoaded: true, IsVisible: true }) owner.Activate();
        }
    }

    /// <summary>The window to put the dialog over: the one given, else the active one, else the main window - if it is on screen.</summary>
    private static Window? UsableOwner(Window? owner)
    {
        owner ??= Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        return owner is { IsLoaded: true, IsVisible: true } && owner.WindowState != WindowState.Minimized ? owner : null;
    }
}

/// <summary>
/// A dark, see-through window laid exactly over another window, to dim it while a dialog is in front. It never takes focus and lets
/// clicks fall through to the window under it (which a modal dialog has switched off anyway).
/// </summary>
internal sealed class DialogScrim : Window
{
    public DialogScrim(Window owner, byte alpha = 150)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        Owner = owner;

        var bounds = WindowHelper.GetScreenBoundsDip(owner);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;

        Content = new System.Windows.Controls.Border
        {
            Background = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0)),
            CornerRadius = new CornerRadius(owner.WindowState == WindowState.Maximized ? 0 : 12),
        };
    }
}
