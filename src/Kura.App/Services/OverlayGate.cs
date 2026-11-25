namespace Kura.App.Services;

/// <summary>
/// Tracks whether any dialog, dropdown, context menu or autocomplete popup is open. The video viewer listens and
/// pauses and detaches its LibVLC surface while anything is open, so the native HWND (WPF "airspace") can't bleed
/// over other UI. Use from the UI thread.
/// </summary>
public sealed class OverlayGate
{
    private int _count;

    public event Action<bool>? Changed;

    public bool IsOpen => _count > 0;

    public IDisposable Enter()
    {
        _count++;
        if (_count == 1) Changed?.Invoke(true);
        return new Token(this);
    }

    private void Exit()
    {
        if (_count == 0) return;
        _count--;
        if (_count == 0) Changed?.Invoke(false);
    }

    private sealed class Token(OverlayGate gate) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            gate.Exit();
        }
    }
}
