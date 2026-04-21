using System.Windows;
using System.Windows.Interop;

namespace RunJargon.App.Utilities;

internal static class WindowCaptureProtection
{
    internal static void TryExcludeFromCapture(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);
    }
}
