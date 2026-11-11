using ClaudeSoundtrack.Core.Models;

namespace ClaudeSoundtrack.Core.Services;

/// <summary>
/// Turns a disc in the drive into tracks on the album, and keeps the album's
/// identity stable across discs.
/// </summary>
public static class AlbumAssembler
{
    /// <summary>
    /// The sentence shown when it is time to swap discs.
    ///
    /// Uses the release's disc count when we know it ("Insert disc 2 of 3").
    /// When the catalogue did not say how many discs there are, it does not
    /// invent a total.
    /// </summary>
    public static string DescribeNextDisc(AlbumProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var next = NextUnrippedDisc(project);
        if (project.DiscCount > project.RippedDiscs.Count && project.DiscCount >= next)
            return $"Insert disc {next} of {project.DiscCount}";

        return next <= 1
            ? "Insert the first disc"
            : "Insert the next disc of this set";
    }

    /// <summary>The lowest disc number that has not been ripped yet, starting at 1.</summary>
    public static int NextUnrippedDisc(AlbumProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var horizon = Math.Max(project.DiscCount, project.RippedDiscs.Count + 1);
        for (var n = 1; n <= horizon; n++)
        {
            if (!project.RippedDiscs.Contains(n)) return n;
        }

        return project.RippedDiscs.Count + 1;
    }

    /// <summary>
    /// Commits the album title, artist and year. A later call does not change
    /// them. The release's disc list is kept, and replaced only when a later
    /// lookup is the same release or the album did not have one yet.
    /// </summary>
    public static void LockIdentity(AlbumProject project, string title, string artist, int? year, ReleaseMatch? match)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (!project.IdentityLocked)
        {
            project.AlbumTitle = YouTubeMusicTags.Normalize(title);
            project.AlbumArtist = YouTubeMusicTags.Normalize(artist);
            project.Year = year is > 0 ? year : null;
            project.IdentityLocked = true;
        }

        if (match is not null) AdoptStructure(project, match);
    }

    /// <summary>
    /// Copies disc structure (track lists, disc count, ids) from a match when
    /// it is safe to. Never copies the title or the artist.
    /// </summary>
    public static void AdoptStructure(AlbumProject project, ReleaseMatch match)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(match);

        var sameRelease = !string.IsNullOrEmpty(project.ReleaseId)
                          && string.Equals(project.ReleaseId, match.ReleaseId, StringComparison.Ordinal);
        var mayReplaceMedia = project.Media.Count == 0 || sameRelease;

        if (mayReplaceMedia && match.Media.Count > 0)
        {
            project.Media.Clear();
            project.Media.AddRange(match.Media);
        }

        if (match.DiscCount > project.DiscCount) project.DiscCount = match.DiscCount;
        if (project.Media.Count > project.DiscCount) project.DiscCount = project.Media.Max(m => m.Number);

        if (string.IsNullOrEmpty(project.ReleaseId) && !string.IsNullOrEmpty(match.ReleaseId))
        {
            project.ReleaseId = match.ReleaseId;
            project.ReleaseSource = match.Source;
        }

        if (string.IsNullOrEmpty(project.ReleaseGroupId) && !string.IsNullOrEmpty(match.ReleaseGroupId))
            project.ReleaseGroupId = match.ReleaseGroupId;

        project.Label ??= match.Label;
        project.CatalogNumber ??= match.CatalogNumber;
    }

    /// <summary>
    /// Builds the tracks for one physical disc.
    ///
    /// <paramref name="sourceDiscNumber"/> is the disc this audio actually is,
    /// from a disc id or a length match or an explicit choice — not "how many
    /// discs have been inserted so far". Titles are taken only when the list
    /// covers this disc exactly, so a short list cannot slide onto the wrong cues.
    /// </summary>
    public static List<SoundtrackTrack> BuildTracks(
        int sourceDiscNumber,
        IReadOnlyList<int> sectorCounts,
        IReadOnlyList<string>? titles,
        string albumArtist)
    {
        if (sourceDiscNumber <= 0) throw new ArgumentOutOfRangeException(nameof(sourceDiscNumber));
        ArgumentNullException.ThrowIfNull(sectorCounts);

        var usable = titles is not null && titles.Count == sectorCounts.Count;
        var artist = YouTubeMusicTags.Normalize(albumArtist);
        var tracks = new List<SoundtrackTrack>(sectorCounts.Count);

        for (var i = 0; i < sectorCounts.Count; i++)
        {
            var title = usable ? titles![i] : $"Track {i + 1}";
            if (string.IsNullOrWhiteSpace(title)) title = $"Track {i + 1}";

            tracks.Add(new SoundtrackTrack
            {
                SourceDiscNumber = sourceDiscNumber,
                SourceTrackNumber = i + 1,
                Title = FileNaming.StripLeadingTrackNumber(title),
                Artist = artist,
                Composer = artist,
                SectorCount = sectorCounts[i],
                Duration = TimeSpan.FromSeconds(sectorCounts[i] / 75.0)
            });
        }

        return tracks;
    }

    /// <summary>Titles for one disc of the project, or an empty list when that disc is unknown.</summary>
    public static IReadOnlyList<string> TitlesFor(AlbumProject project, int discNumber, ReleaseMatch? match)
    {
        var fromProject = project.Media.FirstOrDefault(m => m.Number == discNumber);
        if (fromProject is { Titles.Count: > 0 }) return fromProject.Titles;
        return match?.TitlesForDisc(discNumber) ?? [];
    }
}
