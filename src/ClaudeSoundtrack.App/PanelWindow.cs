using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;

namespace ClaudeSoundtrack.App;

/// <summary>
/// Base window for the app: a borderless panel with no Windows title bar or
/// frame, so the chassis in the design runs right to the edge of the window.
///
/// WPF's <see cref="WindowChrome"/> does the work that a WinForms panel would do
/// by answering WM_NCHITTEST itself - the caption strip stays draggable, the
/// edges stay resizable, and Aero Snap keeps working, all of which are lost when
/// a window is simply set to WindowStyle.None and hand-rolled.
///
/// The one thing WindowChrome does not get right on its own is maximising: a
/// borderless window sized to the full monitor covers the taskbar. That is fixed
/// here in <see cref="OnGetMinMaxInfo"/>.
/// </summary>
public class PanelWindow : Window
{
    /// <summary>Height of the draggable caption strip. Matches the header plate in the layout.</summary>
    public static readonly DependencyProperty CaptionHeightProperty = DependencyProperty.Register(
        nameof(CaptionHeight), typeof(double), typeof(PanelWindow),
        new PropertyMetadata(46.0, (d, e) => ((PanelWindow)d).ApplyChrome()));

    public double CaptionHeight
    {
        get => (double)GetValue(CaptionHeightProperty);
        set => SetValue(CaptionHeightProperty, value);
    }

    public PanelWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;   // keeps hardware rendering and the system drop shadow
        ResizeMode = ResizeMode.CanResize;

        ApplyChrome();

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
            DarkTitleBar.Apply(this);
        };
    }

    private void ApplyChrome()
    {
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = CaptionHeight,
            // Wide enough to grab comfortably without swallowing clicks on
            // controls that sit near the edge.
            ResizeBorderThickness = new Thickness(6),
            // No glass border: the panel draws its own edge.
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        });
    }

    /// <summary>Minimises the window. Bound to the caption strip's minimise button.</summary>
    protected void Minimise() => WindowState = WindowState.Minimized;

    /// <summary>Toggles between maximised and restored.</summary>
    protected void ToggleMaximise() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>
    /// Lets the caption strip act as a title bar for double-click-to-maximise and
    /// drag-to-move, for hosts where the chrome's own caption handling is bypassed.
    /// </summary>
    protected void HandleCaptionMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximise();
            e.Handled = true;
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            // Dragging a maximised window should restore it under the cursor,
            // which is what the real title bar does.
            WindowState = WindowState.Normal;
        }

        DragMove();
    }

    private const int WM_GETMINMAXINFO = 0x0024;

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            OnGetMinMaxInfo(hwnd, lParam);
            handled = true;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Constrains a maximised borderless window to the monitor's working area.
    ///
    /// Without this Windows sizes it to the full monitor bounds, and because there
    /// is no frame to absorb the difference the bottom of the panel disappears
    /// behind the taskbar.
    /// </summary>
    private static void OnGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        const int MonitorDefaultToNearest = 0x00000002;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        // Positions are relative to the monitor, so the working area is offset by
        // the monitor's own origin rather than used directly.
        mmi.ptMaxPosition.x = info.rcWork.left - info.rcMonitor.left;
        mmi.ptMaxPosition.y = info.rcWork.top - info.rcMonitor.top;
        mmi.ptMaxSize.x = info.rcWork.right - info.rcWork.left;
        mmi.ptMaxSize.y = info.rcWork.bottom - info.rcWork.top;

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }
}
