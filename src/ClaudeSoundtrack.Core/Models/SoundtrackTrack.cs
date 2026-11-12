namespace ClaudeSoundtrack.Core.Models;

/// <summary>
/// One track as it will exist on disk after the rip.
///
/// A track carries two sets of numbers. <see cref="SourceDiscNumber"/> and
/// <see cref="SourceTrackNumber"/> are where it physically came from - disc 2,
/// track 3. <see cref="FlatTrackNumber"/> is where it lands after flattening,
/// and that is the number actually written to the TRACKNUMBER tag. Keeping both
/// means flattening can be recomputed or displayed without losing provenance.
/// </summary>
public sealed class SoundtrackTrack
{
    /// <summary>Which physical disc this track was ripped from (1-based).</summary>
    public int SourceDiscNumber { get; set; } = 1;

    /// <summary>Track number on that physical disc (1-based, as printed on the sleeve).</summary>
    public int SourceTrackNumber { get; set; }

    /// <summary>
    /// Continuous track number across every disc in the set. This is what goes
    /// into the tag, so a 3-disc set reads as one 1..N album to YouTube Music.
    /// </summary>
    public int FlatTrackNumber { get; set; }

    /// <summary>
    /// Track title with any leading track number already stripped, so
    /// "01 - Main Titles" is stored here as "Main Titles".
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Per-track artist. Usually the composer for a score.</summary>
    public string Artist { get; set; } = string.Empty;

    /// <summary>Composer, kept separate because soundtracks are catalogued by composer.</summary>
    public string Composer { get; set; } = string.Empty;

    /// <summary>Absolute path to the FLAC file, once it has been written.</summary>
    public string? FilePath { get; set; }

    /// <summary>Length of the track: from the TOC before the rip, from the file after.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>Sector count from the disc TOC, used to fingerprint the disc against a candidate release.</summary>
    public int SectorCount { get; set; }

    /// <summary>True once audio has been successfully ripped and encoded.</summary>
    public bool IsRipped { get; set; }

    /// <summary>Set when the ripper reported unrecoverable read errors for this track.</summary>
    public bool HadReadErrors { get; set; }

    /// <summary>
    /// True when the ripped audio contained no non-zero samples at all.
    ///
    /// Worth tracking separately from <see cref="HadReadErrors"/>: a drive or
    /// library fault can hand back a full track of zeros without reporting any
    /// error, producing a FLAC with the right duration and no sound in it. That
    /// failure is invisible unless it is looked for.
    /// </summary>
    public bool IsSilent { get; set; }

    /// <summary>AccurateRip v1 checksum of the offset-corrected audio, when computed.</summary>
    public uint? AccurateRipCrc { get; set; }

    /// <summary>AccurateRip v2 checksum of the same audio.</summary>
    public uint? AccurateRipV2Crc { get; set; }

    /// <summary>
    /// How far this track's audio was checked. The ripper sets
    /// <see cref="CheckedAgainstDisc"/> once a disc read actually happened;
    /// sample files leave it false so a tag-only dry run is not scolded for
    /// having no second read.
    /// </summary>
    public bool CheckedAgainstDisc { get; set; }

    /// <summary>Outcome of AccurateRip and the second read, when <see cref="CheckedAgainstDisc"/>.</summary>
    public RipConfidence Confidence { get; set; }

    /// <summary>
    /// True when the audio is a stable read but does not match the AccurateRip
    /// record for this pressing. Usually a drive-offset miss, not a random error.
    /// </summary>
    public bool AccurateRipDisagreed { get; set; }

    public override string ToString() => $"{FlatTrackNumber:D2}. {Title}";
}
