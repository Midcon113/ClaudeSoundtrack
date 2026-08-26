namespace ClaudeSoundtrack.Core.Models;

/// <summary>
/// The whole soundtrack being assembled: every track from every disc, the
/// album-level tags they share, and the chosen cover art.
///
/// One <see cref="AlbumProject"/> spans the entire multi-disc set. Discs are
/// ripped into it one at a time; flattening then renumbers across all of them.
/// </summary>
public sealed class AlbumProject
{
    /// <summary>Album title, e.g. "Star Trek: The Motion Picture - The Complete Motion Picture Soundtrack".</summary>
    public string AlbumTitle { get; set; } = string.Empty;

    /// <summary>
    /// Album artist. For a score this is the composer; it is what groups the
    /// album together in YouTube Music, so every track must share it.
    /// </summary>
    public string AlbumArtist { get; set; } = string.Empty;

    /// <summary>Release year of this particular edition.</summary>
    public int? Year { get; set; }

    /// <summary>Genre. Defaults to "Soundtrack".</summary>
    public string Genre { get; set; } = "Soundtrack";

    /// <summary>Record label, e.g. "La-La Land Records". Informational.</summary>
    public string? Label { get; set; }

    /// <summary>Catalogue number of the release, e.g. "LLLCD 1234".</summary>
    public string? CatalogNumber { get; set; }

    /// <summary>How many physical discs the set contains.</summary>
    public int DiscCount { get; set; } = 1;

    /// <summary>Every track across every disc, in flattened order once flattening has run.</summary>
    public List<SoundtrackTrack> Tracks { get; } = new();

    /// <summary>The cover art bytes the user confirmed. Null until artwork is chosen.</summary>
    public byte[]? CoverArt { get; set; }

    /// <summary>Pixel width of <see cref="CoverArt"/>, for the readiness check.</summary>
    public int CoverArtWidth { get; set; }

    /// <summary>Pixel height of <see cref="CoverArt"/>, for the readiness check.</summary>
    public int CoverArtHeight { get; set; }

    /// <summary>Where the artwork came from, e.g. "Cover Art Archive" or a local path.</summary>
    public string? CoverArtSource { get; set; }

    /// <summary>Folder the FLAC files are written to. Set once the output folder is created.</summary>
    public string? OutputFolder { get; set; }

    /// <summary>Which source discs have finished ripping, so the UI can prompt for the next one.</summary>
    public HashSet<int> RippedDiscs { get; } = new();

    /// <summary>Total track count across the whole set - the value written to TRACKTOTAL.</summary>
    public int TotalTrackCount => Tracks.Count;
}
