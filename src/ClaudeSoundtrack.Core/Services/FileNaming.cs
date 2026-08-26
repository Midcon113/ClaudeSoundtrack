using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeSoundtrack.Core.Services;

/// <summary>
/// Turns track titles into filenames, and strips track numbers out of both.
///
/// Two separate jobs that are easy to conflate:
///   - <see cref="StripLeadingTrackNumber"/> cleans the *title*, because metadata
///     sources routinely hand back "01 - Main Titles" as the title itself.
///   - <see cref="SanitizeFileName"/> cleans the *filename*, because soundtrack
///     titles are full of characters Windows rejects.
///
/// The real title always keeps its punctuation in the TITLE tag; only the
/// filename is sanitised.
/// </summary>
public static class FileNaming
{
    /// <summary>
    /// Matches a track number prefix at the start of a title.
    ///
    /// Deliberately requires a separator (dot, dash, colon, underscore, paren or
    /// whitespace run) after the digits. Without that requirement this would eat
    /// the "2" from titles like "2001 Main Theme" or "13 Ghosts". The separator is
    /// what distinguishes a numbering prefix from a title that merely starts with
    /// a number.
    /// </summary>
    private static readonly Regex LeadingTrackNumber = new(
        @"^\s*\(?\d{1,3}\)?\s*(?:[-\u2013\u2014._:)\]]|\s{2,})\s*",
        RegexOptions.Compiled);

    /// <summary>
    /// Matches a disc-and-track prefix such as "1-01 " or "2.03 - ".
    /// Checked before the plain track number so the disc part is not left behind.
    /// </summary>
    private static readonly Regex LeadingDiscTrackNumber = new(
        @"^\s*\(?\d{1,2}[-.]\d{1,3}\)?\s*(?:[-\u2013\u2014._:)\]]\s*|\s+)",
        RegexOptions.Compiled);

    /// <summary>Characters Windows will not accept in a file name.</summary>
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Device names Windows reserves. A track legitimately called "Aux" or "Con"
    /// would otherwise produce a file that cannot be created.
    /// </summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Removes a leading track number from a title.
    ///
    /// "01 - Main Titles"  -> "Main Titles"
    /// "1-05. The Chase"   -> "The Chase"
    /// "2001: A Space Odyssey" -> unchanged, because "2001" is the title
    /// "13 Ghosts"         -> unchanged, single space is not a numbering separator
    /// </summary>
    public static string StripLeadingTrackNumber(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;

        // Disc-qualified prefixes first ("1-01"), otherwise the plain rule would
        // strip "1-" and leave "01 Main Titles" behind.
        var result = LeadingDiscTrackNumber.Replace(title, string.Empty, 1);
        if (result == title)
        {
            result = LeadingTrackNumber.Replace(title, string.Empty, 1);
        }

        result = result.Trim();

        // Never strip a title down to nothing. A track genuinely titled "01"
        // keeps its name rather than becoming an empty string.
        return result.Length == 0 ? title.Trim() : result;
    }

    /// <summary>
    /// Makes a title safe to use as a Windows file name.
    ///
    /// Invalid characters are replaced with a visually similar legal one where a
    /// sensible equivalent exists, so "Suite: Stingers And Act-Out Music" reads as
    /// "Suite - Stingers And Act-Out Music" rather than losing the punctuation.
    /// </summary>
    public static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Untitled";

        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (Array.IndexOf(InvalidFileNameChars, c) >= 0)
            {
                // Substitute rather than drop, so the name stays readable.
                sb.Append(c switch
                {
                    ':' => " -",
                    '/' or '\\' => "-",
                    '"' => "'",
                    '<' => "(",
                    '>' => ")",
                    '|' => "-",
                    '?' or '*' => string.Empty,
                    _ => string.Empty
                });
            }
            else
            {
                sb.Append(c);
            }
        }

        var cleaned = sb.ToString();

        // Collapse whitespace runs left behind by removed characters.
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim();

        // Windows silently strips trailing dots and spaces, which would make the
        // name we think we wrote differ from the one on disk.
        cleaned = cleaned.TrimEnd('.', ' ');

        if (cleaned.Length == 0) return "Untitled";

        // A reserved device name is only reserved as the whole stem.
        if (ReservedNames.Contains(cleaned)) cleaned = "_" + cleaned;

        // Leave room for the extension and the folder path within MAX_PATH.
        const int maxStemLength = 120;
        if (cleaned.Length > maxStemLength)
        {
            cleaned = cleaned[..maxStemLength].TrimEnd('.', ' ');
        }

        return cleaned;
    }

    /// <summary>
    /// Builds the final file names for a set of titles, guaranteeing uniqueness.
    ///
    /// Track numbers are deliberately absent from the name. That reintroduces a
    /// collision risk that numbering used to hide: soundtracks repeat titles all
    /// the time ("Source Music" three times on one disc). Repeats get " (2)",
    /// " (3)" appended so nothing is silently overwritten.
    /// </summary>
    /// <param name="titles">Track titles in flattened order.</param>
    /// <param name="extension">File extension including the dot, e.g. ".flac".</param>
    public static IReadOnlyList<string> BuildUniqueFileNames(IEnumerable<string> titles, string extension = ".flac")
        => BuildUniqueFileNames(titles, new HashSet<string>(StringComparer.OrdinalIgnoreCase), extension);

    /// <summary>
    /// Builds file names that avoid colliding with names already in use.
    ///
    /// Discs are ripped one at a time, so disc 2's names have to dodge the files
    /// disc 1 already wrote. Without this, a set that repeats "Main Title" across
    /// discs would silently overwrite the earlier track.
    /// </summary>
    /// <param name="titles">Track titles for the tracks being named now.</param>
    /// <param name="alreadyUsed">
    /// Stems (without extension) already taken. Updated in place with the new names.
    /// </param>
    /// <param name="extension">File extension including the dot.</param>
    public static IReadOnlyList<string> BuildUniqueFileNames(
        IEnumerable<string> titles, ISet<string> alreadyUsed, string extension = ".flac")
    {
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in alreadyUsed) used[name] = 1;

        var results = new List<string>();

        foreach (var title in titles)
        {
            var stem = SanitizeFileName(StripLeadingTrackNumber(title));

            if (used.TryGetValue(stem, out var seen))
            {
                var next = seen + 1;
                used[stem] = next;
                // Probe upward in case "Title (2)" is itself an existing title.
                string candidate;
                do
                {
                    candidate = $"{stem} ({next})";
                    next++;
                } while (used.ContainsKey(candidate));

                used[candidate] = 1;
                alreadyUsed.Add(candidate);
                results.Add(candidate + extension);
            }
            else
            {
                used[stem] = 1;
                alreadyUsed.Add(stem);
                results.Add(stem + extension);
            }
        }

        return results;
    }

    /// <summary>
    /// Builds the album folder name, e.g. "Jerry Goldsmith - Star Trek (1979)".
    ///
    /// The artist prefix keeps sibling folders in C:\Users\...\Music grouped by
    /// composer, which is how score collections are usually browsed.
    /// </summary>
    public static string BuildAlbumFolderName(string albumTitle, string? albumArtist, int? year)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(albumArtist)) parts.Add(albumArtist.Trim());
        parts.Add(string.IsNullOrWhiteSpace(albumTitle) ? "Unknown Album" : albumTitle.Trim());

        var name = string.Join(" - ", parts);
        if (year is > 0) name += $" ({year})";

        return SanitizeFileName(name);
    }
}
