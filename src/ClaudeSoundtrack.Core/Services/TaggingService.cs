using ATL;
using ClaudeSoundtrack.Core.Models;

namespace ClaudeSoundtrack.Core.Services;

/// <summary>
/// Writes Vorbis comments and embedded cover art into the ripped FLAC files.
///
/// Flattening shows up here as a continuous TRACKNUMBER. DISCNUMBER is the real
/// disc, written as a plain integer — never "1/2" — and the album title, album
/// artist and artist are the same string on every file. The same picture bytes
/// are embedded in each one. See <see cref="YouTubeMusicTags"/>.
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

        if (written > 0) WriteFolderArt(project);
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

        var album = YouTubeMusicTags.Normalize(project.AlbumTitle);
        var albumArtist = YouTubeMusicTags.Normalize(project.AlbumArtist);
        var credit = YouTubeMusicTags.Normalize(track.Artist);

        // Strip a track number out of the title even at this late stage. Some
        // sources hand back "01 - Main Titles" as the title, and letting that
        // reach the tag would show the number twice in every player.
        file.Title = FileNaming.StripLeadingTrackNumber(track.Title);

        // ARTIST and ALBUMARTIST are the same value on every track. YouTube Music
        // groups on those strings; a per-cue composer credit splits the album.
        // The cue's own credit is kept as PERFORMER when it differs.
        file.Album = album;
        file.AlbumArtist = albumArtist;
        file.Artist = albumArtist;
        file.Genre = YouTubeMusicTags.Normalize(project.Genre);

        if (!string.IsNullOrWhiteSpace(track.Composer)) file.Composer = YouTubeMusicTags.Normalize(track.Composer);
        if (project.Year is > 0) file.Year = project.Year;
        if (!string.IsNullOrWhiteSpace(project.Label)) file.Publisher = project.Label;
        if (!string.IsNullOrWhiteSpace(project.CatalogNumber)) file.CatalogNumber = project.CatalogNumber;

        file.TrackNumber = track.FlatTrackNumber;
        file.TrackTotal = project.TotalTrackCount;
        file.DiscNumber = track.SourceDiscNumber;
        file.DiscTotal = YouTubeMusicTags.DiscTotal(project);

        // ATL writes these as TRACKTOTAL and DISCTOTAL — plain integers in their
        // own fields, which is the Vorbis shape. A second TOTALTRACKS tag for
        // the same number makes ATL read the total back as 0, so it is not written.
        if (credit.Length > 0 && !string.Equals(credit, albumArtist, StringComparison.Ordinal))
            file.AdditionalFields["PERFORMER"] = credit;
        else if (file.AdditionalFields.ContainsKey("PERFORMER"))
            file.AdditionalFields.Remove("PERFORMER");

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

        if (written > 0) WriteFolderArt(project);
        return written;
    }

    /// <summary>
    /// Writes folder.jpg from the same bytes embedded in the tracks.
    ///
    /// YouTube Music rejects a loose image at upload time, so this is for the
    /// local folder only. It is the same array the files were given, not a
    /// re-encode, so the album cannot be split by two different covers.
    /// </summary>
    public static void WriteFolderArt(AlbumProject project)
    {
        if (project.CoverArt is not { Length: > 0 }) return;
        if (string.IsNullOrEmpty(project.OutputFolder)) return;

        var extension = ImageDimensions.DetectFormat(project.CoverArt) switch
        {
            ImageDimensions.Format.Png => ".png",
            _ => ".jpg"
        };

        // Keep the name players look for, and drop a stale folder.jpg when the
        // new art is a PNG (or the reverse).
        foreach (var name in new[] { "folder.jpg", "folder.jpeg", "folder.png" })
        {
            var path = Path.Combine(project.OutputFolder, name);
            if (File.Exists(path)) File.Delete(path);
        }

        File.WriteAllBytes(Path.Combine(project.OutputFolder, "folder" + extension), project.CoverArt);
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
