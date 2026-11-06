using System.Windows.Input;
using System.Windows.Threading;

namespace EchoDeck;

/// <summary>
/// Reads keys from WPF's InputManager. It only ever sees keys typed into this app's own windows
/// (including LibVLCSharp's overlay surface), installs no Windows hook, and uses no polling APIs,
/// so there is nothing for antivirus heuristics to mistake for a keylogger.
/// </summary>
internal sealed class WpfKeyInput : IKeyInputSource
{
    private readonly Dispatcher _dispatcher;
    private bool _started;

    public event Action<int, bool, bool>? KeyPressed;
    public event Action? CtrlReleased;

    public WpfKeyInput(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public bool Start()
    {
        if (!_started)
        {
            InputManager.Current.PreProcessInput += OnPreProcessInput;
            _started = true;
        }
        return true;
    }

    private void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is not KeyEventArgs ke) return;

        if (ke.RoutedEvent == Keyboard.PreviewKeyDownEvent)
        {
            if (ke.IsRepeat) return;

            var key = ke.Key == Key.System ? ke.SystemKey : ke.Key;
            int vk = KeyInterop.VirtualKeyFromKey(key);
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            _dispatcher.InvokeAsync(() => KeyPressed?.Invoke(vk, ctrl, true));
        }
        else if (ke.RoutedEvent == Keyboard.PreviewKeyUpEvent && (ke.Key is Key.LeftCtrl or Key.RightCtrl))
        {
            _dispatcher.InvokeAsync(() => CtrlReleased?.Invoke());
        }
    }

    public void Dispose()
    {
        if (_started)
        {
            InputManager.Current.PreProcessInput -= OnPreProcessInput;
            _started = false;
        }
    }
}
