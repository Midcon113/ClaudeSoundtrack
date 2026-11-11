using System.Globalization;
using ClaudeSoundtrack.Core.Models;

namespace ClaudeSoundtrack.Core.Services;

/// <summary>
/// The tag values written so a multi-disc score survives a YouTube Music upload
/// as one album, in the order the discs were composed.
///
/// Flat track numbers are the order. YouTube Music has been reported to ignore
/// a disc number written as "1/2" and to sort plain "1" and "2" correctly, and
/// the track order cannot be edited after the upload. Numbering 1..N is correct
/// in both cases: if the disc number is ignored, the numbers do not collide; if
/// it is honoured, the numbers still do not restart, so disc-then-track and
/// track-only produce the same sequence.
///
/// The disc itself stays in DISCNUMBER as a plain integer, with the total in its
/// own DISCTOTAL field. It is not folded into the title.
/// </summary>
public static class YouTubeMusicTags
{
    /// <summary>
    /// Collapses whitespace so "Jerry Goldsmith" and "Jerry Goldsmith " are the
    /// same album artist. A trailing space is enough for YouTube Music to file
    /// the track under a second album.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>A disc or track number with no "/total" suffix.</summary>
    public static string PlainNumber(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>How many discs to declare. At least the discs we actually have audio for.</summary>
    public static int DiscTotal(AlbumProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var fromTracks = project.Tracks.Count == 0 ? 1 : project.Tracks.Max(t => t.SourceDiscNumber);
        return Math.Max(1, Math.Max(project.DiscCount, fromTracks));
    }

    /// <summary>True when a disc-number field is the combined "2/3" form.</summary>
    public static bool IsCombinedPosition(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains('/');
}
