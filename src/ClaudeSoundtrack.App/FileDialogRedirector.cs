using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeSoundtrack.App;

/// <summary>
/// Watches for the file picker a browser opens for "Upload music", and points it
/// straight at the album folder with every track already selected.
///
/// Without this the upload is a scavenger hunt: the browser opens a picker
/// sitting in some unrelated folder, the app opens a second Explorer window
/// showing the right one, and the user navigates the first to match the second by
/// hand. Since the picker is an ordinary Windows dialog, it can simply be told
/// where to go.
///
/// Nothing is submitted. The names are filled in and the dialog is left for the
/// user to confirm with Open - the upload is theirs to start, not ours.
/// </summary>
public sealed class FileDialogRedirector
{
    /// <summary>The window class Windows uses for common dialogs, file pickers included.</summary>
    private const string DialogClass = "#32770";

    /// <summary>How long to keep looking before giving up.</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

    /// <summary>How often to sweep for a new dialog.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(400);

    /// <summary>What happened, so the UI can say something useful either way.</summary>
    public enum Outcome
    {
        /// <summary>The dialog was found and filled in.</summary>
        Redirected,

        /// <summary>No file dialog appeared before the timeout.</summary>
        TimedOut,

        /// <summary>A dialog appeared but its filename field could not be found.</summary>
        DialogNotUnderstood,

        /// <summary>The dialog was sent to the album folder, but the tracks could not be pre-selected.</summary>
        NavigatedOnly,

        /// <summary>The caller cancelled the wait.</summary>
        Cancelled
    }

    /// <summary>
    /// Waits for a file picker to open in another process, then navigates it to
    /// <paramref name="folder"/> and fills in every file in
    /// <paramref name="filePaths"/>.
    /// </summary>
    /// <param name="folder">Folder the dialog should end up showing.</param>
    /// <param name="filePaths">Full paths to pre-select. May be empty to only navigate.</param>
    /// <param name="timeout">How long to wait for a dialog. Defaults to 90 seconds.</param>
    public async Task<Outcome> RedirectNextDialogAsync(
        string folder,
        IReadOnlyList<string> filePaths,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        var ownPid = Environment.ProcessId;

        // Dialogs already on screen before we started are not the one the user is
        // about to open, so they are recorded and skipped.
        var preexisting = FindDialogs(ownPid).ToHashSet();

        while (DateTime.UtcNow < deadline)
        {
            if (cancellationToken.IsCancellationRequested) return Outcome.Cancelled;

            foreach (var dialog in FindDialogs(ownPid))
            {
                if (preexisting.Contains(dialog)) continue;

                // Give the dialog a moment to finish creating its child controls;
                // a picker caught mid-construction has no filename field yet.
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);

                return TryFill(dialog, folder, filePaths);
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return Outcome.TimedOut;
    }

    /// <summary>
    /// Navigates the dialog and fills in the file names.
    ///
    /// Two steps on purpose. Typing the folder and pressing Enter makes the
    /// dialog visibly move to the album, so the user can see it worked. Only then
    /// are the file names filled in, quoted, which is how a Windows picker
    /// expresses a multiple selection.
    ///
    /// Both steps have to cope with the dialog fighting back. Navigation is
    /// asynchronous, and when it finishes the dialog clears the filename box - so
    /// a write that lands too early is silently wiped, which looked exactly like
    /// success from this side. The text is therefore written, read back, and
    /// rewritten until it sticks.
    ///
    /// Fully-qualified paths are used rather than bare names, so the selection is
    /// correct even if navigation did not take.
    /// </summary>
    private static Outcome TryFill(IntPtr dialog, string folder, IReadOnlyList<string> filePaths)
    {
        var edit = FindFileNameEdit(dialog);
        if (edit == IntPtr.Zero) return Outcome.DialogNotUnderstood;

        // Only a folder to offer: type it and navigate, which is all that can be
        // done without a file list.
        if (filePaths.Count == 0)
        {
            SetText(edit, folder);
            PressEnter(edit);
            return Outcome.Redirected;
        }

        // Deliberately no navigation.
        //
        // Typing the folder and pressing Enter looks tidier, but the dialog
        // navigates asynchronously and blanks the filename box when it arrives -
        // after the selection has been written and verified. The box would read
        // correct at the moment it was checked and be empty a second later, which
        // is precisely the kind of bug that reports success and delivers nothing.
        //
        // Fully-qualified quoted paths need no navigation: the picker resolves
        // them wherever it happens to be pointing, and pressing Open selects
        // exactly these files.
        var selection = string.Join(" ", filePaths.Select(p => $"\"{p}\""));
        var probe = Path.GetFileName(filePaths[0]);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            edit = FindFileNameEdit(dialog);
            if (edit == IntPtr.Zero) break;

            SetText(edit, selection);

            // Confirm it is still there a moment later, not just the instant it
            // was written - the dialog's own initialisation can clear it once.
            if (HoldsSelection(dialog, probe, TimeSpan.FromMilliseconds(1200)))
            {
                return Outcome.Redirected;
            }

            Thread.Sleep(300);
        }

        return Outcome.NavigatedOnly;
    }

    /// <summary>
    /// Checks the filename box still holds the selection throughout
    /// <paramref name="window"/>, rather than only at the moment it was written.
    /// </summary>
    private static bool HoldsSelection(IntPtr dialog, string probe, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(200);

            var edit = FindFileNameEdit(dialog);
            if (edit == IntPtr.Zero) return false;

            if (!GetText(edit).Contains(probe, StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    /// <summary>
    /// Locates the dialog's filename box.
    ///
    /// Modern pickers nest it as ComboBoxEx32 &gt; ComboBox &gt; Edit; older and
    /// simpler ones use a bare Edit. Both shapes are tried before falling back to
    /// a recursive hunt, because browsers do not all use the same dialog.
    /// </summary>
    private static IntPtr FindFileNameEdit(IntPtr dialog)
    {
        var combo = FindWindowEx(dialog, IntPtr.Zero, "ComboBoxEx32", null);
        if (combo != IntPtr.Zero)
        {
            var inner = FindWindowEx(combo, IntPtr.Zero, "ComboBox", null);
            if (inner != IntPtr.Zero)
            {
                var edit = FindWindowEx(inner, IntPtr.Zero, "Edit", null);
                if (edit != IntPtr.Zero) return edit;
            }
        }

        var direct = FindWindowEx(dialog, IntPtr.Zero, "Edit", null);
        if (direct != IntPtr.Zero) return direct;

        return FindDescendantEdit(dialog, depth: 0);
    }

    /// <summary>Depth-limited search for an Edit control anywhere under the dialog.</summary>
    private static IntPtr FindDescendantEdit(IntPtr parent, int depth)
    {
        if (depth > 4) return IntPtr.Zero;

        var found = IntPtr.Zero;

        EnumChildWindows(parent, (child, _) =>
        {
            if (GetClassName(child) == "Edit")
            {
                found = child;
                return false;
            }

            var nested = FindDescendantEdit(child, depth + 1);
            if (nested != IntPtr.Zero)
            {
                found = nested;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>Visible common dialogs belonging to any process other than this one.</summary>
    private static List<IntPtr> FindDialogs(int ownProcessId)
    {
        var dialogs = new List<IntPtr>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (GetClassName(hwnd) != DialogClass) return true;

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == ownProcessId) return true;

            dialogs.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        return dialogs;
    }

    private static void SetText(IntPtr control, string text)
    {
        SendMessage(control, WM_SETTEXT, IntPtr.Zero, text);
    }

    /// <summary>Reads a control's text, so a write can be confirmed rather than assumed.</summary>
    private static string GetText(IntPtr control)
    {
        var length = (int)SendMessage(control, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
        if (length <= 0) return string.Empty;

        var buffer = new StringBuilder(length + 1);
        SendMessage(control, WM_GETTEXT, new IntPtr(buffer.Capacity), buffer);
        return buffer.ToString();
    }

    /// <summary>
    /// Presses Enter in the filename box, which is what makes a picker navigate
    /// to a typed folder.
    /// </summary>
    private static void PressEnter(IntPtr control)
    {
        PostMessage(control, WM_KEYDOWN, VK_RETURN, IntPtr.Zero);
        PostMessage(control, WM_KEYUP, VK_RETURN, IntPtr.Zero);
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        var length = GetClassName(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : string.Empty;
    }

    // ------------------------------------------------------------ interop

    private const int WM_SETTEXT = 0x000C;
    private const int WM_GETTEXT = 0x000D;
    private const int WM_GETTEXTLENGTH = 0x000E;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private static readonly IntPtr VK_RETURN = new(0x0D);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, string lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, StringBuilder lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
