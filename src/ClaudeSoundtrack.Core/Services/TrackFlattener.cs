using ClaudeSoundtrack.Core.Models;

namespace ClaudeSoundtrack.Core.Services;

/// <summary>
/// Collapses a multi-disc set into one continuous album.
///
/// YouTube Music has no real concept of a disc within an album: uploading a
/// 3-disc score with three "track 1"s produces a jumbled album. Flattening
/// renumbers so disc 2 track 1 becomes track 11 when disc 1 held ten tracks,
/// and every file then claims to be disc 1 of 1.
/// </summary>
public static class TrackFlattener
{
    /// <summary>
    /// Renumbers every track in the project into a single 1..N sequence.
    ///
    /// Ordering is by source disc, then by source track, so the album plays in
    /// the order the composer laid it out. The project's track list is sorted in
    /// place to match, so index order and flat number agree afterwards.
    /// </summary>
    /// <returns>The number of tracks renumbered.</returns>
    public static int Flatten(AlbumProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var ordered = project.Tracks
            .OrderBy(t => t.SourceDiscNumber)
            .ThenBy(t => t.SourceTrackNumber)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].FlatTrackNumber = i + 1;
        }

        // Rewrite the list in flattened order so the UI and the writer agree.
        project.Tracks.Clear();
        project.Tracks.AddRange(ordered);

        return ordered.Count;
    }

    /// <summary>
    /// Describes what flattening did, for the confirmation shown before tags are
    /// written: "Disc 2: tracks 1-10 become 11-20".
    /// </summary>
    public static IReadOnlyList<string> DescribeMapping(AlbumProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        return project.Tracks
            .GroupBy(t => t.SourceDiscNumber)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var sourceLow = g.Min(t => t.SourceTrackNumber);
                var sourceHigh = g.Max(t => t.SourceTrackNumber);
                var flatLow = g.Min(t => t.FlatTrackNumber);
                var flatHigh = g.Max(t => t.FlatTrackNumber);

                return sourceLow == flatLow && sourceHigh == flatHigh
                    ? $"Disc {g.Key}: tracks {sourceLow}-{sourceHigh} unchanged"
                    : $"Disc {g.Key}: tracks {sourceLow}-{sourceHigh} become {flatLow}-{flatHigh}";
            })
            .ToList();
    }
}
