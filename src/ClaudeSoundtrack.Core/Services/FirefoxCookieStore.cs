using Microsoft.Data.Sqlite;

namespace ClaudeSoundtrack.Core.Services;

/// <summary>
/// Reads the YouTube session cookies out of the local Firefox profile.
///
/// Uploading to YouTube Music needs the same credentials the browser uses; there
/// is no API key or OAuth flow for it. Rather than asking the user to paste
/// header values out of developer tools, the cookies are read from the profile on
/// this machine.
///
/// Handling rules, because these values are as good as the account password:
///   - they are never written to disk, logged, or included in error messages;
///   - they are only ever sent to Google's own upload endpoint over HTTPS;
///   - they are held in memory for the duration of an upload and no longer.
///
/// Firefox keeps cookies.sqlite open in WAL mode, so it is copied before reading
/// rather than opened in place.
/// </summary>
public sealed class FirefoxCookieStore
{
    /// <summary>Cookies that must be present for an authenticated upload.</summary>
    private static readonly string[] RequiredCookies = ["SAPISID", "__Secure-3PAPISID", "__Secure-3PSID"];

    /// <summary>What a cookie lookup produced.</summary>
    /// <param name="Cookies">Cookie name/value pairs for youtube.com. Empty on failure.</param>
    /// <param name="ProfilePath">Which profile they came from, for display.</param>
    /// <param name="Problem">Human-readable reason when the lookup failed, else null.</param>
    public sealed record Result(
        IReadOnlyDictionary<string, string> Cookies,
        string? ProfilePath,
        string? Problem)
    {
        /// <summary>True when every cookie needed to authenticate is present.</summary>
        public bool IsUsable =>
            Problem is null && RequiredCookies.All(Cookies.ContainsKey);

        /// <summary>Names of the required cookies that were not found.</summary>
        public IReadOnlyList<string> Missing =>
            RequiredCookies.Where(c => !Cookies.ContainsKey(c)).ToList();
    }

    /// <summary>
    /// Finds the Firefox profile in use and reads its youtube.com cookies.
    /// </summary>
    /// <param name="profileOverride">Explicit profile folder, or null to auto-detect.</param>
    public Result Read(string? profileOverride = null)
    {
        var profile = profileOverride ?? FindDefaultProfile();

        if (profile is null || !Directory.Exists(profile))
            return new Result(new Dictionary<string, string>(), null,
                "No Firefox profile was found on this machine.");

        var source = Path.Combine(profile, "cookies.sqlite");
        if (!File.Exists(source))
            return new Result(new Dictionary<string, string>(), profile,
                "That Firefox profile has no cookie database yet.");

        string? tempBase = null;

        try
        {
            tempBase = CopyDatabase(source);

            var cookies = new Dictionary<string, string>(StringComparer.Ordinal);

            using (var connection = new SqliteConnection($"Data Source={tempBase};Mode=ReadOnly"))
            {
                connection.Open();

                using var command = connection.CreateCommand();
                // Host-only and domain cookies both matter; YouTube sets some on
                // .youtube.com and some on the bare host.
                command.CommandText =
                    """
                    SELECT name, value FROM moz_cookies
                    WHERE host = '.youtube.com' OR host = 'youtube.com'
                       OR host = '.music.youtube.com' OR host = 'music.youtube.com'
                    """;

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var name = reader.GetString(0);
                    var value = reader.GetString(1);

                    // Later rows win, which favours the more specific host.
                    cookies[name] = value;
                }
            }

            // Pooling keeps the file handle alive past Dispose, which then blocks
            // the cleanup below.
            SqliteConnection.ClearAllPools();

            return new Result(cookies, profile, null);
        }
        catch (Exception ex) when (ex is IOException or SqliteException or UnauthorizedAccessException)
        {
            return new Result(new Dictionary<string, string>(), profile,
                $"Could not read the Firefox cookie database: {ex.Message}");
        }
        finally
        {
            if (tempBase is not null) DeleteCopy(tempBase);
        }
    }

    /// <summary>
    /// Copies the cookie database and its write-ahead log to a temporary file.
    ///
    /// The -wal file has to come too: a cookie set in this browser session may
    /// exist only in the log and not yet in the main database, which would make a
    /// freshly signed-in account look signed out.
    /// </summary>
    private static string CopyDatabase(string source)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"cs-cookies-{Guid.NewGuid():N}.sqlite");

        File.Copy(source, temp, overwrite: true);

        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = source + suffix;
            if (File.Exists(sidecar)) File.Copy(sidecar, temp + suffix, overwrite: true);
        }

        return temp;
    }

    /// <summary>Removes the temporary copy. Cookies must not be left lying around.</summary>
    private static void DeleteCopy(string tempBase)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = tempBase + suffix;
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
                // Still held briefly; it is in the temp folder and will be cleaned
                // up by Windows. Not worth failing an upload over.
            }
        }
    }

    /// <summary>
    /// Locates the profile Firefox actually uses, preferring the one named in
    /// installs.ini - the launched profile - over anything merely present.
    /// </summary>
    public static string? FindDefaultProfile()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Mozilla", "Firefox");

        if (!Directory.Exists(root)) return null;

        var fromInstalls = ReadProfileFromIni(Path.Combine(root, "installs.ini"), root)
                           ?? ReadProfileFromIni(Path.Combine(root, "profiles.ini"), root);

        if (fromInstalls is not null && Directory.Exists(fromInstalls)) return fromInstalls;

        // Nothing declared: fall back to the most recently used profile, which is
        // a better guess than the first one alphabetically.
        var profiles = Path.Combine(root, "Profiles");
        if (!Directory.Exists(profiles)) return null;

        return Directory.GetDirectories(profiles)
            .Where(d => File.Exists(Path.Combine(d, "cookies.sqlite")))
            .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "cookies.sqlite")))
            .FirstOrDefault();
    }

    /// <summary>Pulls the first Default= entry out of a Firefox ini file.</summary>
    private static string? ReadProfileFromIni(string iniPath, string root)
    {
        if (!File.Exists(iniPath)) return null;

        try
        {
            foreach (var line in File.ReadLines(iniPath))
            {
                if (!line.StartsWith("Default=", StringComparison.OrdinalIgnoreCase)) continue;

                var relative = line["Default=".Length..].Trim().Replace('/', Path.DirectorySeparatorChar);
                if (relative.Length == 0) continue;

                var full = Path.IsPathRooted(relative) ? relative : Path.Combine(root, relative);
                if (File.Exists(Path.Combine(full, "cookies.sqlite"))) return full;
            }
        }
        catch (IOException)
        {
            // Unreadable ini; the caller falls back to scanning.
        }

        return null;
    }
}
