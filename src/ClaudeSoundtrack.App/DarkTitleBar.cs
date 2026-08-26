using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClaudeSoundtrack.App;

/// <summary>
/// Switches the window's title bar to the dark variant.
///
/// WPF does not style the non-client area, so a dark application gets a light
/// title bar by default. Against these panels that reads as a bright band across
/// the top of every window and breaks the illusion immediately.
///
/// This is a DWM attribute rather than anything WPF exposes, so it has to be set
/// on the native handle once the window has one.
/// </summary>
internal static class DarkTitleBar
{
    /// <summary>The documented attribute id, used by Windows 10 20H1 and later.</summary>
    private const int UseImmersiveDarkMode = 20;

    /// <summary>
    /// The id the same attribute had before 20H1. Older builds ignore 20 and
    /// respond to this instead, so both are attempted.
    /// </summary>
    private const int UseImmersiveDarkModeLegacy = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Applies the dark title bar to every window in the application as it opens,
    /// including dialogs, without each one having to remember to ask.
    /// </summary>
    public static void ApplyToAllWindows()
    {
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window) Apply(window);
            }));
    }

    /// <summary>Applies the dark title bar to one window. Safe to call more than once.</summary>
    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var enabled = 1;

        // A non-zero return means the attribute is not supported on this build,
        // which is not worth surfacing - the window simply keeps its light bar.
        if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(handle, UseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
        }

        // DWM only repaints the frame on the next non-client change. Without a
        // nudge the bar stays light until the window is moved or resized.
        RedrawFrame(handle);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>
    /// Forces DWM to redraw the frame, without moving, resizing or restacking
    /// the window.
    /// </summary>
    private static void RedrawFrame(IntPtr handle)
    {
        const uint NoMove = 0x0002;
        const uint NoSize = 0x0001;
        const uint NoZOrder = 0x0004;
        const uint NoActivate = 0x0010;
        const uint FrameChanged = 0x0020;

        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
            NoMove | NoSize | NoZOrder | NoActivate | FrameChanged);
    }
}
