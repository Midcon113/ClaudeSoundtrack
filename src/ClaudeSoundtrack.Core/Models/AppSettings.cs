using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeSoundtrack.Core.Models;

/// <summary>
/// User settings, stored as JSON beside the executable so the app stays portable:
/// copy the file, keep your settings. If that folder is read-only - Program Files,
/// a network share, or the single-file extraction directory - settings fall back
/// to %APPDATA%\ClaudeSoundtrack.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// How strongly the lamps glow, 0 to 1. Stored as a slider position where 0.5
    /// is the reference look; the renderer doubles it, so the range runs from no
    /// glow at all to twice the reference.
    /// </summary>
    public double BloomIntensity { get; set; } = 0.5;

    /// <summary>The multiplier the renderer actually uses.</summary>
    [JsonIgnore]
    public double BloomMultiplier => Math.Clamp(BloomIntensity, 0, 1) * 2;

    /// <summary>
    /// Animate the row of lamps across the header while work is running.
    ///
    /// Purely decorative, and some people find moving lights distracting during a
    /// forty-minute rip, so it can be switched off independently of the glow.
    /// </summary>
    public bool AnimateLamps { get; set; } = true;

    /// <summary>How fast the lamp animation runs, 0.25 to 3. 1 is the reference speed.</summary>
    public double LampSpeed { get; set; } = 1.0;

    /// <summary>
    /// Sit in the notification area watching for a disc instead of closing, and
    /// pop a notification when an audio CD appears.
    /// </summary>
    public bool WatchForDiscs { get; set; }

    /// <summary>
    /// Stop checking whether Windows reports the right MIME type for .flac.
    ///
    /// Set once the user has fixed it or said they do not want to be told; a
    /// warning that keeps reappearing on a machine the user has decided about is
    /// just noise.
    /// </summary>
    public bool SuppressFlacMimeCheck { get; set; }

    /// <summary>
    /// Where finished albums are written. Empty means "work it out", which prefers
    /// the local profile Music folder over a OneDrive-redirected one.
    /// </summary>
    public string? MusicFolderOverride { get; set; }

    /// <summary>Where the settings actually came from, for display. Null if defaults.</summary>
    [JsonIgnore]
    public string? FilePath { get; private set; }

    private static string PortablePath =>
        Path.Combine(AppContext.BaseDirectory, "ClaudeSoundtrack.settings.json");

    private static string RoamingPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClaudeSoundtrack",
            "settings.json");

    /// <summary>
    /// Loads settings, preferring a portable file beside the executable.
    /// Any failure yields defaults rather than stopping the app - settings are a
    /// convenience, not something worth refusing to start over.
    /// </summary>
    public static AppSettings Load()
    {
        foreach (var path in new[] { PortablePath, RoamingPath })
        {
            try
            {
                if (!File.Exists(path)) continue;

                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                if (loaded is null) continue;

                loaded.FilePath = path;
                return loaded;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Corrupt or unreadable; try the next location.
            }
        }

        return new AppSettings();
    }

    /// <summary>
    /// Saves settings, trying the portable location first and falling back to
    /// roaming when the executable's folder cannot be written to.
    /// </summary>
    /// <returns>True when the settings reached disk.</returns>
    public bool Save()
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

        foreach (var path in new[] { FilePath, PortablePath, RoamingPath })
        {
            if (string.IsNullOrEmpty(path)) continue;

            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                File.WriteAllText(path, json);
                FilePath = path;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Read-only location; fall through to the next candidate.
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves the folder albums are written into.
    ///
    /// Deliberately prefers the real profile folder over
    /// <see cref="Environment.SpecialFolder.MyMusic"/>. With OneDrive's Known
    /// Folder Move enabled, MyMusic resolves to OneDrive\Music and rips land in
    /// cloud storage, which then starts uploading gigabytes of FLAC.
    /// </summary>
    public string ResolveMusicFolder()
    {
        if (!string.IsNullOrWhiteSpace(MusicFolderOverride) && Directory.Exists(MusicFolderOverride))
            return MusicFolderOverride;

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            var local = Path.Combine(profile, "Music");
            if (Directory.Exists(local)) return local;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
    }
}
