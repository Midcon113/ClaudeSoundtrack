using ClaudeSoundtrack.Core.Models;

namespace ClaudeSoundtrack.Core.Services;

/// <summary>
/// Decides which disc of a release is in the drive.
///
/// Track count alone is not enough. Expanded scores routinely put the same
/// number of cues on two discs, and the first disc with that count is then
/// "verified" — so disc 2 is ripped with disc 1's titles and stored as whatever
/// disc the user happened to insert. Lengths, and a MusicBrainz disc ID when
/// we have one, separate those discs. When they still do not, the result is
/// ambiguous and the caller must ask rather than guess.
/// </summary>
public static class DiscIdentifier
{
    /// <summary>
    /// A catalogue length and a CD sector count are the same duration when they
    /// are this close. MusicBrainz lengths are milliseconds and are often a
    /// second or so off the TOC; a different cue is off by much more.
    /// </summary>
    public const int DurationToleranceMs = 2000;

    /// <summary>Red Book sectors per second. One sector is 1/75 of a second.</summary>
    private const double SectorsPerSecond = 75.0;

    public static DiscIdentification Identify(DiscSnapshot disc, IReadOnlyList<DiscMedium>? media)
    {
        ArgumentNullException.ThrowIfNull(disc);
        if (media is null || media.Count == 0 || disc.TrackCount == 0)
            return DiscIdentification.Unknown;

        var ordered = media.Where(m => m.Number > 0).OrderBy(m => m.Number).ToList();
        if (ordered.Count == 0) return DiscIdentification.Unknown;

        var discCount = ordered.Max(m => m.Number);

        if (!string.IsNullOrWhiteSpace(disc.MusicBrainzDiscId))
        {
            var byId = ordered
                .Where(m => string.Equals(m.DiscId, disc.MusicBrainzDiscId, StringComparison.Ordinal))
                .ToList();
            if (byId.Count == 1)
            {
                return new DiscIdentification(
                    byId[0].Number,
                    discCount,
                    DiscMatchKind.DiscId,
                    $"MusicBrainz disc ID matches disc {byId[0].Number}.");
            }
        }

        var durationHits = ordered.Where(m => DurationsMatch(disc.SectorCounts, m)).ToList();
        if (durationHits.Count == 1)
        {
            return new DiscIdentification(
                durationHits[0].Number,
                discCount,
                DiscMatchKind.Duration,
                $"Track lengths match disc {durationHits[0].Number}.");
        }

        if (durationHits.Count > 1)
        {
            return Ambiguous(durationHits, discCount, "track lengths");
        }

        // No usable durations. A unique track count is still decisive; a shared
        // one is exactly the case that used to be guessed wrong.
        var countHits = ordered.Where(m => m.Titles.Count == disc.TrackCount || m.LengthMs.Count == disc.TrackCount).ToList();
        if (countHits.Count == 1 && !HasAnyDuration(countHits[0]))
        {
            return new DiscIdentification(
                countHits[0].Number,
                discCount,
                DiscMatchKind.TrackCount,
                $"Only disc {countHits[0].Number} has {disc.TrackCount} tracks.");
        }

        if (countHits.Count > 1)
            return Ambiguous(countHits, discCount, "track count");

        return new DiscIdentification(null, discCount, DiscMatchKind.None,
            "No disc in this release has this track list.");
    }

    private static DiscIdentification Ambiguous(IReadOnlyList<DiscMedium> hits, int discCount, string how) =>
        new(
            null,
            discCount,
            DiscMatchKind.Ambiguous,
            $"Discs {string.Join(" and ", hits.Select(h => h.Number))} both fit this disc by {how}. " +
            "The disc number has to be chosen, not guessed.");

    /// <summary>
    /// True when every catalogued length agrees with the disc, and enough
    /// lengths were actually present to mean something.
    /// </summary>
    public static bool DurationsMatch(IReadOnlyList<int> sectorCounts, DiscMedium medium)
    {
        if (medium.LengthMs.Count != sectorCounts.Count) return false;
        var known = medium.LengthMs.Count(l => l > 0);
        if (known == 0 || known < Math.Max(1, sectorCounts.Count / 2)) return false;

        for (var i = 0; i < sectorCounts.Count; i++)
        {
            if (medium.LengthMs[i] <= 0) continue;
            var actualMs = sectorCounts[i] * 1000.0 / SectorsPerSecond;
            if (Math.Abs(actualMs - medium.LengthMs[i]) > DurationToleranceMs) return false;
        }

        return true;
    }

    private static bool HasAnyDuration(DiscMedium medium) => medium.LengthMs.Any(l => l > 0);
}
