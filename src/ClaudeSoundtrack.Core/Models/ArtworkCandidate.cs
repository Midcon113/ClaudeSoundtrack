namespace ClaudeSoundtrack.Core.Models;

/// <summary>
/// One piece of cover art found by a search, before the user has accepted it.
///
/// Candidates are lazy: <see cref="ImageData"/> is only populated once the bytes
/// are actually fetched, so a search can list twenty results without downloading
/// twenty full-size images.
/// </summary>
public sealed class ArtworkCandidate
{
    /// <summary>Human-readable origin shown in the picker: "iTunes", "Cover Art Archive", "Local file".</summary>
    public required string Source { get; init; }

    /// <summary>URL the full-size image is fetched from. Null for a local file.</summary>
    public string? ImageUrl { get; init; }

    /// <summary>Local path, when the user supplied their own file.</summary>
    public string? LocalPath { get; init; }

    /// <summary>Release title this art was matched to, so the user can spot a wrong edition.</summary>
    public string? ReleaseTitle { get; init; }

    /// <summary>Year of the matched release, again to help spot a wrong edition.</summary>
    public string? ReleaseYear { get; init; }

    /// <summary>Downloaded image bytes. Null until fetched.</summary>
    public byte[]? ImageData { get; set; }

    /// <summary>Pixel width, known only after the bytes are decoded.</summary>
    public int Width { get; set; }

    /// <summary>Pixel height, known only after the bytes are decoded.</summary>
    public int Height { get; set; }

    /// <summary>True when the image is at least 1000x1000, the bar for a good upload.</summary>
    public bool IsHighResolution => Width >= 1000 && Height >= 1000;

    /// <summary>"1400 x 1400" for display, or "unknown" before the image is fetched.</summary>
    public string ResolutionText => Width > 0 ? $"{Width} x {Height}" : "unknown";
}
