using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using RunJargon.App.Utilities;

namespace RunJargon.App.Services;

public sealed class GlobalHotKeyService : IDisposable
{
    private readonly Window _window;
    private HwndSource? _hwndSource;
    private int _hotKeyId;
    private bool _isRegistered;

    public GlobalHotKeyService(Window window)
    {
        _window = window;
    }

    public event EventHandler? Pressed;

    public bool Register(ModifierKeys modifiers, Key key)
    {
        if (_isRegistered)
        {
            return true;
        }

        var windowInteropHelper = new WindowInteropHelper(_window);
        _hwndSource = HwndSource.FromHwnd(windowInteropHelper.Handle);
        if (_hwndSource is null)
        {
            return false;
        }

        _hwndSource.AddHook(WndProc);
        _hotKeyId = GetHashCode();

        var registered = NativeMethods.RegisterHotKey(
            windowInteropHelper.Handle,
            _hotKeyId,
            (uint)modifiers,
            (uint)KeyInterop.VirtualKeyFromKey(key));

        _isRegistered = registered;
        return registered;
    }

    public void Dispose()
    {
        if (_isRegistered)
        {
            var windowInteropHelper = new WindowInteropHelper(_window);
            NativeMethods.UnregisterHotKey(windowInteropHelper.Handle, _hotKeyId);
            _isRegistered = false;
        }

        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }
    }

    private IntPtr WndProc(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg == NativeMethods.WmHotKey && wParam.ToInt32() == _hotKeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }
}
