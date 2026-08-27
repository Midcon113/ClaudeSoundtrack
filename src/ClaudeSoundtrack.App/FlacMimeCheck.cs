using System.IO;
using Microsoft.Win32;

namespace ClaudeSoundtrack.App;

/// <summary>
/// Checks that Windows reports the right MIME type for <c>.flac</c>, and can
/// correct it.
///
/// This exists because of a real failure that took a long time to find. VLC
/// registers <c>.flac</c> with <c>Content Type = audio/wav</c>. Firefox asks
/// Windows for a file's MIME type, so every FLAC arrives at a web page claiming
/// to be a WAV; YouTube Music validates the type in JavaScript and rejects the
/// lot. The symptoms are maddening - every track fails, no network request fails,
/// and it survives disabling every extension - because nothing is ever sent.
///
/// Chrome carries its own MIME table and ignores the registry, which is why the
/// same upload works there and makes the browser look like the culprit.
///
/// Only HKEY_CURRENT_USER is written. That takes precedence for this user, needs
/// no administrator rights, and leaves the machine-wide association alone.
/// </summary>
public static class FlacMimeCheck
{
    private const string SubKey = @"Software\Classes\.flac";
    private const string ValueName = "Content Type";
    private const string Correct = "audio/flac";

    /// <summary>What the registry currently says.</summary>
    public enum State
    {
        /// <summary>Reported as audio/flac. Nothing to do.</summary>
        Correct,

        /// <summary>Reported as something else - the case that breaks uploads.</summary>
        Wrong,

        /// <summary>No MIME type registered at all. Firefox then reports an empty type.</summary>
        Missing,

        /// <summary>The registry could not be read.</summary>
        Unknown
    }

    /// <summary>The outcome of a check.</summary>
    /// <param name="State">What was found.</param>
    /// <param name="CurrentValue">The value currently registered, if any.</param>
    /// <param name="Culprit">The application that appears to have set it, when identifiable.</param>
    public readonly record struct Report(State State, string? CurrentValue, string? Culprit)
    {
        /// <summary>True when this would break a browser upload.</summary>
        public bool NeedsFixing => State is State.Wrong or State.Missing;
    }

    /// <summary>
    /// Reads the effective MIME type for .flac, preferring the per-user value
    /// because that is what actually wins for this user.
    /// </summary>
    public static Report Check()
    {
        try
        {
            var value = ReadValue(Registry.CurrentUser) ?? ReadValue(Registry.ClassesRoot, root: true);

            if (value is null) return new Report(State.Missing, null, IdentifyCulprit());

            return string.Equals(value, Correct, StringComparison.OrdinalIgnoreCase)
                ? new Report(State.Correct, value, null)
                : new Report(State.Wrong, value, IdentifyCulprit());
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return new Report(State.Unknown, null, null);
        }
    }

    private static string? ReadValue(RegistryKey hive, bool root = false)
    {
        // HKCR is already rooted at the class names, HKCU needs the Software\Classes prefix.
        using var key = hive.OpenSubKey(root ? ".flac" : SubKey);
        return key?.GetValue(ValueName) as string;
    }

    /// <summary>
    /// Names the program that registered the association, so the warning can say
    /// what did this rather than blaming Windows.
    /// </summary>
    private static string? IdentifyCulprit()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(".flac");
            var handler = key?.GetValue(null) as string;

            if (string.IsNullOrWhiteSpace(handler)) return null;

            // Handlers look like "VLC.flac", "WMP11.AssocFile.FLAC", "foobar2000.FLAC".
            var name = handler.Split('.')[0];
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Registers <c>audio/flac</c> for the current user.
    ///
    /// Only ever called from an explicit click - the app does not quietly rewrite
    /// file associations on the user's behalf.
    /// </summary>
    /// <returns>Null on success, or a message explaining why it failed.</returns>
    public static string? Fix()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SubKey, writable: true);
            if (key is null) return "Windows would not let the key be created.";

            key.SetValue(ValueName, Correct, RegistryValueKind.String);

            // Confirm rather than assume: a policy can silently discard the write.
            var written = key.GetValue(ValueName) as string;
            return string.Equals(written, Correct, StringComparison.OrdinalIgnoreCase)
                ? null
                : "The value did not stick; it may be locked by policy.";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return ex.Message;
        }
    }
}
