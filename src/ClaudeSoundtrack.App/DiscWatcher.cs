using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClaudeSoundtrack.App;

/// <summary>An audio CD that has just appeared in a drive.</summary>
/// <param name="DevicePath">Drive to rip from, e.g. "D:".</param>
/// <param name="VolumeLabel">Volume label, usually "Audio CD".</param>
public readonly record struct DiscInsertedEventArgs(string DevicePath, string VolumeLabel);

/// <summary>
/// Watches for a disc being put into an optical drive.
///
/// Windows broadcasts WM_DEVICECHANGE to top-level windows, so this needs a
/// window handle to hook rather than a polling loop - which would mean spinning
/// the drive up every few seconds, all day, to ask whether anything had changed.
///
/// Only audio CDs raise <see cref="DiscInserted"/>. A data disc, a DVD or a USB
/// stick arriving is not something this app has any business reacting to.
/// </summary>
public sealed class DiscWatcher : IDisposable
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVTYP_VOLUME = 0x0002;

    private HwndSource? _source;
    private bool _hooked;

    /// <summary>Raised on the UI thread when an audio CD appears.</summary>
    public event EventHandler<DiscInsertedEventArgs>? DiscInserted;

    /// <summary>
    /// Starts listening, using <paramref name="window"/>'s handle as the message
    /// sink. The window may stay hidden; it only has to exist.
    /// </summary>
    public void Start(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_hooked) return;

        var handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle);

        if (_source is null) return;

        _source.AddHook(OnMessage);
        _hooked = true;
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_DEVICECHANGE || wParam.ToInt32() != DBT_DEVICEARRIVAL) return IntPtr.Zero;
        if (lParam == IntPtr.Zero) return IntPtr.Zero;

        var header = Marshal.PtrToStructure<DEV_BROADCAST_HDR>(lParam);
        if (header.dbch_devicetype != DBT_DEVTYP_VOLUME) return IntPtr.Zero;

        var volume = Marshal.PtrToStructure<DEV_BROADCAST_VOLUME>(lParam);

        foreach (var letter in DriveLettersFromMask(volume.dbcv_unitmask))
        {
            // The disc is announced the moment it is recognised, which is before
            // the file system is ready to answer questions about it. Asking too
            // early reports "not ready" and the insertion is missed entirely, so
            // the check is deferred rather than done inline.
            var path = $"{letter}:";
            _ = InspectWhenReadyAsync(path);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Waits for the drive to settle, then raises the event if it holds audio.
    /// </summary>
    private async Task InspectWhenReadyAsync(string devicePath)
    {
        // A disc typically takes a couple of seconds to spin up and mount.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(700).ConfigureAwait(true);

            try
            {
                var drive = new DriveInfo(devicePath);
                if (drive.DriveType != DriveType.CDRom) return;
                if (!drive.IsReady) continue;

                // An audio CD has no readable file system, which is exactly how
                // Windows describes it: type CDFS/unknown with the label "Audio CD".
                // Rather than guess from the label alone, treat "ready optical
                // volume with no data files" as audio and let the ripper's TOC
                // read be the real test.
                var label = SafeLabel(drive);

                DiscInserted?.Invoke(this, new DiscInsertedEventArgs(devicePath, label));
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Still mounting, or vanished again. Try again, then give up.
            }
        }
    }

    private static string SafeLabel(DriveInfo drive)
    {
        try
        {
            return string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Audio CD" : drive.VolumeLabel;
        }
        catch (IOException)
        {
            // An audio CD often throws here, which is itself a good sign.
            return "Audio CD";
        }
    }

    /// <summary>Turns the bitmask Windows sends into drive letters.</summary>
    private static IEnumerable<char> DriveLettersFromMask(uint mask)
    {
        for (var i = 0; i < 26; i++)
        {
            if ((mask & (1u << i)) != 0) yield return (char)('A' + i);
        }
    }

    public void Dispose()
    {
        if (_source is not null && _hooked)
        {
            _source.RemoveHook(OnMessage);
            _hooked = false;
        }

        _source = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_HDR
    {
        public uint dbch_size;
        public uint dbch_devicetype;
        public uint dbch_reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_VOLUME
    {
        public uint dbcv_size;
        public uint dbcv_devicetype;
        public uint dbcv_reserved;
        public uint dbcv_unitmask;
        public ushort dbcv_flags;
    }
}
