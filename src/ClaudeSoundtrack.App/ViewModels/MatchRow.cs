using ClaudeSoundtrack.Core.Services;

namespace ClaudeSoundtrack.App.ViewModels;

/// <summary>
/// One release candidate as shown in the identify list.
///
/// The status column is the point of this type. A soundtrack search returns the
/// original album alongside the expanded edition under nearly the same name, and
/// the only reliable way to tell them apart is whether the track count matches
/// the disc actually in the drive.
/// </summary>
public sealed class MatchRow
{
    private readonly int _discTrackCount;

    public MatchRow(ReleaseMatch match, int discTrackCount)
    {
        Match = match;
        _discTrackCount = discTrackCount;
    }

    public ReleaseMatch Match { get; }

    public string Title => Match.Title;
    public string Artist => Match.Artist;
    public string Source => Match.Source;
    public string YearText => Match.Year?.ToString() ?? "-";

    /// <summary>VERIFIED when this release has a disc matching the one in the drive.</summary>
    public string StatusText => Match.IsTocVerified ? "VERIFIED" : "unverified";

    /// <summary>
    /// Track count for the matched disc, with the disc's own count when they
    /// disagree, so a mismatch is visible at a glance rather than implied.
    /// </summary>
    public string TrackCountText
    {
        get
        {
            var count = Match.TitlesForDisc(Match.DiscNumber).Count;
            if (count == 0) return "-";
            return _discTrackCount > 0 && count != _discTrackCount
                ? $"{count} ≠ {_discTrackCount}"
                : count.ToString();
        }
    }
}
