using System.IO;
using Microsoft.Win32;

namespace ClaudeSoundtrack.App;

/// <summary>
/// Registers ClaudeSoundtrack in the per-user Run key.
///
/// Per-user (HKCU) keeps the app installable by copying a single file - no
/// elevation, no installer, no service. It launches with <c>--tray</c> so it
/// starts as a notification-area icon watching for a disc, rather than throwing
/// a window in the user's face at every login.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClaudeSoundtrack";

    /// <summary>The command line the Run key points at.</summary>
    private static string CommandLine =>
        $"\"{Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ClaudeSoundtrack.exe")}\" --tray";

    /// <summary>Whether the app is set to start with Windows.</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                return key?.GetValue(ValueName) is string existing && existing.Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Turns start-with-Windows on or off.</summary>
    /// <returns>True when the registry was left in the requested state.</returns>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return false;

            if (enabled) key.SetValue(ValueName, CommandLine, RegistryValueKind.String);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Rewrites the stored path if the executable has been moved.
    ///
    /// This app ships as a single file people are told to put wherever they like,
    /// so a stale Run entry pointing at the old location is the normal case, not
    /// an edge case.
    /// </summary>
    public static void RefreshPathIfRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is not string existing) return;

            if (!string.Equals(existing, CommandLine, StringComparison.OrdinalIgnoreCase))
                key.SetValue(ValueName, CommandLine, RegistryValueKind.String);
        }
        catch
        {
            // Failing to self-heal the path is not worth interrupting the user over.
        }
    }
}
