namespace ClaudeSoundtrack.Core.Models;

/// <summary>How a physical disc was matched to a release.</summary>
public enum DiscMatchKind
{
    /// <summary>Nothing matched, or the candidate was not checked.</summary>
    None = 0,

    /// <summary>More than one disc fits, and guessing would tag the wrong one.</summary>
    Ambiguous,

    /// <summary>Exactly one disc has this many tracks, and no durations were available to go further.</summary>
    TrackCount,

    /// <summary>Per-track lengths agree with one disc and disagree with the others.</summary>
    Duration,

    /// <summary>The MusicBrainz disc ID on the medium is this disc.</summary>
    DiscId
}

/// <summary>How sure we are that a ripped track is the audio that was on the disc.</summary>
public enum RipConfidence
{
    /// <summary>No second read and no AccurateRip hit.</summary>
    Unverified = 0,

    /// <summary>A second read of the disc produced the same audio.</summary>
    Repeatable,

    /// <summary>The checksum matches the AccurateRip database.</summary>
    AccurateRip,

    /// <summary>A second read disagreed with the first. Do not upload this track.</summary>
    Unstable
}

/// <summary>
/// One disc of a release, as a catalogue described it.
///
/// Lengths are milliseconds, 0 when the source did not know. The disc id is the
/// MusicBrainz disc ID when that medium has been submitted.
/// </summary>
public sealed record DiscMedium(
    int Number,
    IReadOnlyList<string> Titles,
    IReadOnlyList<int> LengthMs,
    string? DiscId);

/// <summary>
/// The audio table of contents of the disc in the drive, reduced to what
/// matching needs: the MusicBrainz disc ID and each audio track's sector count.
/// </summary>
public sealed record DiscSnapshot(string? MusicBrainzDiscId, IReadOnlyList<int> SectorCounts)
{
    public int TrackCount => SectorCounts.Count;
}

/// <summary>The result of comparing a disc in the drive with a release's media.</summary>
public sealed record DiscIdentification(int? DiscNumber, int DiscCount, DiscMatchKind Kind, string Detail)
{
    public static DiscIdentification Unknown { get; } = new(null, 0, DiscMatchKind.None, "No release to compare against.");

    /// <summary>True when it is safe to rip this as <see cref="DiscNumber"/> without asking.</summary>
    public bool IsConfident =>
        DiscNumber is > 0 && Kind is DiscMatchKind.DiscId or DiscMatchKind.Duration or DiscMatchKind.TrackCount;
}
