using System.Text.RegularExpressions;
using ClaudeSoundtrack.Core.Models;

namespace ClaudeSoundtrack.Core.Services;

/// <summary>
/// Decides whether a lookup hit belongs to the album already being ripped.
///
/// Disc 2 of The Rock must not be offered next to Pink Floyd. A hit stays when
/// it is the same MusicBrainz release, or when the title (disc suffix removed)
/// and the artist are the same album under a longer catalogue name.
/// </summary>
public static partial class AlbumCandidateFilter
{
    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant | RegexOptions.Compiled)]
    private static partial Regex NotLetters();

    public static bool Belongs(AlbumProject project, ReleaseMatch match)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(match);
        if (!project.IdentityLocked) return true;

        if (Same(project.ReleaseId, match.ReleaseId)) return true;
        if (Same(project.ReleaseGroupId, match.ReleaseGroupId)) return true;

        if (!TitlesCompatible(project.AlbumTitle, match.Title)) return false;
        return ArtistsCompatible(project.AlbumArtist, match.Artist);
    }

    /// <summary>
    /// True when the two titles are the same album. "The Rock" matches
    /// "The Rock: Original Motion Picture Score". "The Rock" does not match
    /// "The Rocky Horror Picture Show".
    /// </summary>
    public static bool TitlesCompatible(string? locked, string? candidate)
    {
        var a = Canonical(locked);
        var b = Canonical(candidate);
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;

        var shorter = a.Length <= b.Length ? a : b;
        var longer = a.Length <= b.Length ? b : a;
        // A one-word stump such as "the" must not collect the whole catalogue.
        if (shorter.Length < 6) return false;
        return longer.StartsWith(shorter + " ", StringComparison.Ordinal);
    }

    /// <summary>
    /// True when the artists can be the same credit. An empty side does not
    /// reject the row. "Hans Zimmer" matches a credit that also names him.
    /// </summary>
    public static bool ArtistsCompatible(string? locked, string? candidate)
    {
        var a = Canonical(locked);
        var b = Canonical(candidate);
        if (a.Length == 0 || b.Length == 0) return true;
        if (a == b) return true;
        if (a.Length >= 4 && b.Contains(a, StringComparison.Ordinal)) return true;
        if (b.Length >= 4 && a.Contains(b, StringComparison.Ordinal)) return true;

        var left = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length >= 4).ToHashSet();
        var right = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return left.Any(right.Contains);
    }

    private static string Canonical(string? value)
    {
        var stripped = AlbumTitleCleaner.Parse(value).Title;
        var normalized = YouTubeMusicTags.Normalize(stripped).ToLowerInvariant();
        return NotLetters().Replace(normalized, " ").Trim();
    }

    private static bool Same(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.Ordinal);
}
