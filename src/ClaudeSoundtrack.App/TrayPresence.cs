using System.IO;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace ClaudeSoundtrack.App;

/// <summary>
/// The notification-area icon, and the app's ability to sit quietly waiting for
/// a disc.
///
/// WPF has no tray icon of its own, so this wraps the WinForms
/// <see cref="NotifyIcon"/>. That is the standard arrangement and costs only the
/// WinForms reference; nothing else in the app uses it.
///
/// When the app is running from the tray the main window still exists - hidden -
/// because the disc watcher needs a window handle to receive WM_DEVICECHANGE on.
/// </summary>
public sealed class TrayPresence : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Window _window;
    private bool _disposed;

    /// <summary>Raised when the user asks to rip the disc that was just found.</summary>
    public event EventHandler? RipRequested;

    public TrayPresence(Window window)
    {
        _window = window;

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "ClaudeSoundtrack - watching for discs",
            Visible = true
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open ClaudeSoundtrack", null, (_, _) => ShowWindow());
        menu.Items.Add("Rip the disc in the drive", null, (_, _) => RipRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Application.Current.Shutdown());

        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ShowWindow();

        // A balloon that does nothing when clicked is a dead end; clicking any of
        // ours brings the app up ready to act.
        _icon.BalloonTipClicked += (_, _) =>
        {
            ShowWindow();
            RipRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>
    /// Loads the app icon out of its own resources, so the tray and the taskbar
    /// show the same disc.
    /// </summary>
    private static Icon LoadIcon()
    {
        try
        {
            var stream = Application.GetResourceStream(
                new Uri("pack://application:,,,/ClaudeSoundtrack;component/ClaudeSoundtrack.ico"))?.Stream;

            if (stream is not null) return new Icon(stream);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            // Fall through to the system default rather than failing to start.
        }

        return SystemIcons.Application;
    }

    /// <summary>Tells the user a disc has turned up, without stealing focus.</summary>
    public void NotifyDiscInserted(string devicePath, string label)
    {
        _icon.BalloonTipTitle = "Audio CD detected";
        _icon.BalloonTipText = $"{label} in {devicePath}. Click to read and rip it.";
        _icon.BalloonTipIcon = ToolTipIcon.Info;
        _icon.ShowBalloonTip(8000);
    }

    /// <summary>Brings the main window up from the tray.</summary>
    public void ShowWindow()
    {
        // Started from the tray the window was created hidden and off the taskbar;
        // both have to be undone or it comes back with no taskbar button.
        _window.Visibility = Visibility.Visible;
        _window.ShowInTaskbar = true;

        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;   // nudge past whatever had focus
        _window.Topmost = false;
    }

    /// <summary>Hides the window, leaving the app alive in the notification area.</summary>
    public void HideWindow() => _window.Hide();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _icon.Visible = false;
        _icon.Dispose();
    }
}
