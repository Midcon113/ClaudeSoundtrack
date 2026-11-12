using System.Text.RegularExpressions;

namespace ClaudeSoundtrack.Core.Services;

/// <summary>What a disc suffix contributed to an album title.</summary>
/// <param name="Title">Title with the suffix removed, or the original when there was nothing to remove.</param>
/// <param name="DiscNumber">Disc number parsed from the suffix, when there was one.</param>
/// <param name="DiscCount">"of M" from the suffix, when it was written that way.</param>
/// <param name="HadDiscSuffix">True when a suffix was removed.</param>
public readonly record struct CleanAlbumTitle(string Title, int? DiscNumber, int? DiscCount, bool HadDiscSuffix);

/// <summary>
/// Removes "Disc 2" style suffixes from album titles.
///
/// CD-Text on a multi-disc set is often "The Rock - Disc 1" and "The Rock - Disc 2".
/// Locking those strings as the album title makes YouTube Music file each disc as
/// its own album. A title that merely contains the word, such as a score called
/// "Disc", is left alone.
/// </summary>
public static partial class AlbumTitleCleaner
{
    /// <summary>
    /// " - Disc 2", "Disc 2 of 3", "CD2", with a space or a dash in front.
    /// The body has to be non-empty: a title that is only "Disc 2" is kept.
    /// </summary>
    [GeneratedRegex(
        @"^(?<body>.+?)\s*(?:[-–—−:|/]\s*|\s+)(?<kind>disc|cd|disk)\s*(?<n>\d{1,2})(?:\s*(?:of|/)\s*(?<m>\d{1,2}))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled)]
    private static partial Regex SeparatedSuffix();

    /// <summary>"(Disc 2)", "[CD 2 of 2]", "(Disc 2 of 3)".</summary>
    [GeneratedRegex(
        @"^(?<body>.+?)\s*[\[(]\s*(?<kind>disc|cd|disk)\s*(?<n>\d{1,2})(?:\s*(?:of|/)\s*(?<m>\d{1,2}))?\s*[\])]\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled)]
    private static partial Regex BracketSuffix();

    public static CleanAlbumTitle Parse(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new CleanAlbumTitle(string.Empty, null, null, false);

        var trimmed = title.Trim();
        var match = BracketSuffix().Match(trimmed);
        if (!match.Success) match = SeparatedSuffix().Match(trimmed);
        if (!match.Success)
            return new CleanAlbumTitle(trimmed, null, null, false);

        var body = match.Groups["body"].Value.Trim().TrimEnd('-', '–', '—', '−', ':', '|', '/').Trim();
        // "Disc" on its own, or a suffix that ate the whole name, stays as typed.
        if (body.Length == 0)
            return new CleanAlbumTitle(trimmed, null, null, false);

        int? number = int.TryParse(match.Groups["n"].Value, out var n) && n > 0 ? n : null;
        int? count = int.TryParse(match.Groups["m"].Value, out var m) && m > 0 ? m : null;
        return new CleanAlbumTitle(body, number, count, HadDiscSuffix: true);
    }
}
