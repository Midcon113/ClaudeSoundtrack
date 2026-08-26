using ATL;
using ClaudeSoundtrack.Core.Models;

namespace ClaudeSoundtrack.Core.Services;

/// <summary>
/// Writes Vorbis comments and embedded cover art into the ripped FLAC files.
///
/// This is where flattening actually takes effect on disk. Every file is written
/// as disc 1 of 1 with its flattened track number, because that is what makes
/// YouTube Music treat a multi-disc set as one album.
/// </summary>
public sealed class TaggingService
{
    /// <summary>
    /// Writes tags for every ripped track in the project.
    ///
    /// Artwork is embedded here too when the project has any. Tracks that have
    /// not been ripped are skipped rather than treated as an error, so tagging
    /// can run after a partial rip.
    /// </summary>
    /// <returns>The number of files successfully written.</returns>
    public int WriteAllTags(AlbumProject project, IProgress<(int Done, int Total)>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(project);

        var targets = project.Tracks.Where(t => t.IsRipped && !string.IsNullOrEmpty(t.FilePath)).ToList();
        var written = 0;

        for (var i = 0; i < targets.Count; i++)
        {
            if (WriteTags(project, targets[i])) written++;
            progress?.Report((i + 1, targets.Count));
        }

        return written;
    }

    /// <summary>
    /// Writes tags and artwork for one track.
    /// </summary>
    /// <returns>True when the file was saved.</returns>
    public bool WriteTags(AlbumProject project, SoundtrackTrack track)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(track);

        if (string.IsNullOrEmpty(track.FilePath) || !File.Exists(track.FilePath)) return false;

        var file = new Track(track.FilePath);

        // Strip a track number out of the title even at this late stage. Some
        // sources hand back "01 - Main Titles" as the title, and letting that
        // reach the tag would show the number twice in every player.
        file.Title = FileNaming.StripLeadingTrackNumber(track.Title);

        file.Album = project.AlbumTitle;
        file.AlbumArtist = project.AlbumArtist;
        file.Artist = string.IsNullOrWhiteSpace(track.Artist) ? project.AlbumArtist : track.Artist;
        file.Genre = project.Genre;

        if (!string.IsNullOrWhiteSpace(track.Composer)) file.Composer = track.Composer;
        if (project.Year is > 0) file.Year = project.Year;
        if (!string.IsNullOrWhiteSpace(project.Label)) file.Publisher = project.Label;
        if (!string.IsNullOrWhiteSpace(project.CatalogNumber)) file.CatalogNumber = project.CatalogNumber;

        // The whole point of flattening: continuous numbering, single disc.
        file.TrackNumber = track.FlatTrackNumber;
        file.TrackTotal = project.TotalTrackCount;
        file.DiscNumber = 1;
        file.DiscTotal = 1;

        if (project.CoverArt is { Length: > 0 })
        {
            // Replace rather than append, so re-tagging cannot stack up duplicate
            // front covers inside the file.
            file.EmbeddedPictures.Clear();
            file.EmbeddedPictures.Add(PictureInfo.fromBinaryData(project.CoverArt, PictureInfo.PIC_TYPE.Front));
        }

        return file.Save();
    }

    /// <summary>
    /// Embeds artwork into every ripped file without touching any other tag.
    /// Used when the user changes their mind about the cover after tagging.
    /// </summary>
    /// <returns>The number of files updated.</returns>
    public int WriteArtworkOnly(AlbumProject project, IProgress<(int Done, int Total)>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.CoverArt is not { Length: > 0 }) return 0;

        var targets = project.Tracks.Where(t => t.IsRipped && !string.IsNullOrEmpty(t.FilePath)).ToList();
        var written = 0;

        for (var i = 0; i < targets.Count; i++)
        {
            var path = targets[i].FilePath!;
            if (File.Exists(path))
            {
                var file = new Track(path);
                file.EmbeddedPictures.Clear();
                file.EmbeddedPictures.Add(PictureInfo.fromBinaryData(project.CoverArt, PictureInfo.PIC_TYPE.Front));
                if (file.Save()) written++;
            }

            progress?.Report((i + 1, targets.Count));
        }

        return written;
    }

    /// <summary>
    /// Reads the tags currently on disk back into a track.
    ///
    /// The manual editor uses this so it edits what is actually in the file
    /// rather than what the app believes it wrote.
    /// </summary>
    public void ReadTagsInto(SoundtrackTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (string.IsNullOrEmpty(track.FilePath) || !File.Exists(track.FilePath)) return;

        var file = new Track(track.FilePath);

        track.Title = file.Title ?? string.Empty;
        track.Artist = file.Artist ?? string.Empty;
        track.Composer = file.Composer ?? string.Empty;
        if (file.TrackNumber is > 0) track.FlatTrackNumber = file.TrackNumber.Value;
        // DurationMs rather than Duration, which is truncated to whole seconds
        // and would round a short cue down to nothing.
        if (file.DurationMs > 0) track.Duration = TimeSpan.FromMilliseconds(file.DurationMs);
    }
}
