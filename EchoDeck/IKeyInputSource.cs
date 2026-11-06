namespace EchoDeck;

/// <summary>Where Ctrl+PIN / Esc / hotkeys come from. Default: WPF's own input pipeline (no OS hooks).</summary>
internal interface IKeyInputSource : IDisposable
{
    /// <summary>vkCode, ctrlHeld, appActive (this app is the active window).</summary>
    event Action<int, bool, bool>? KeyPressed;
    event Action? CtrlReleased;
    bool Start();
}
