namespace ClaudeSoundtrack.Core.Models;

/// <summary>One track already on disk, as found by a library scan.</summary>
/// <param name="FilePath">Absolute path to the audio file.</param>
/// <param name="TrackNumber">Track number from the tag, or 0 when untagged.</param>
/// <param name="Title">Track title, falling back to the file name.</param>
/// <param name="Artist">Track artist.</param>
/// <param name="Duration">Length of the track.</param>
public sealed record LibraryTrack(
    string FilePath,
    int TrackNumber,
    string Title,
    string Artist,
    TimeSpan Duration)
{
    /// <summary>"4:07", or "1:02:15" for anything over an hour.</summary>
    public string DurationText => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");
}

/// <summary>
/// An album found in the library folder.
///
/// Built by reading the files rather than any index, so an album shows up the
/// moment it is ripped and disappears when it is deleted, with nothing to keep
/// in sync.
/// </summary>
public sealed class LibraryAlbum
{
    public required string Folder { get; init; }
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public int? Year { get; init; }

    /// <summary>Tracks in tag order.</summary>
    public required IReadOnlyList<LibraryTrack> Tracks { get; init; }

    /// <summary>Cover art lifted from the first track that has any. Null if none do.</summary>
    public byte[]? CoverArt { get; init; }

    /// <summary>Total running time of the album.</summary>
    public TimeSpan TotalDuration => Tracks.Aggregate(TimeSpan.Zero, (sum, t) => sum + t.Duration);

    /// <summary>"28 tracks · 1:14:22", for the album list.</summary>
    public string SummaryText
    {
        get
        {
            var count = Tracks.Count == 1 ? "1 track" : $"{Tracks.Count} tracks";
            var total = TotalDuration;
            var length = total.TotalHours >= 1
                ? total.ToString(@"h\:mm\:ss")
                : total.ToString(@"m\:ss");
            return $"{count}  ·  {length}";
        }
    }

    /// <summary>"Jerry Goldsmith · 1979", or just the artist when the year is unknown.</summary>
    public string SubtitleText =>
        Year is > 0 ? $"{Artist}  ·  {Year}" : Artist;
}
