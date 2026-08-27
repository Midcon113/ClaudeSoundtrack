using ATL;
using ClaudeSoundtrack.Core.Models;

namespace ClaudeSoundtrack.Core.Services;

/// <summary>How badly a readiness problem affects the upload.</summary>
public enum ReadinessSeverity
{
    /// <summary>Worth knowing, but the upload will still work.</summary>
    Info,

    /// <summary>The upload will succeed but the album will look wrong in some way.</summary>
    Warning,

    /// <summary>The upload will be broken or rejected. Must be fixed first.</summary>
    Error
}

/// <summary>One problem found by the readiness check.</summary>
/// <param name="Severity">How serious it is.</param>
/// <param name="Message">What is wrong, phrased for the user.</param>
/// <param name="TrackNumber">Flattened track number, or null for an album-wide problem.</param>
public readonly record struct ReadinessIssue(ReadinessSeverity Severity, string Message, int? TrackNumber);

/// <summary>The outcome of a full readiness check.</summary>
/// <param name="Issues">Everything found, worst first.</param>
/// <param name="FilesChecked">How many FLAC files were inspected.</param>
public sealed record ReadinessReport(IReadOnlyList<ReadinessIssue> Issues, int FilesChecked)
{
    /// <summary>True when nothing would break the upload.</summary>
    public bool IsReady => !Issues.Any(i => i.Severity == ReadinessSeverity.Error);

    /// <summary>True when the album is not just uploadable but clean.</summary>
    public bool IsPerfect => Issues.Count == 0;

    public int ErrorCount => Issues.Count(i => i.Severity == ReadinessSeverity.Error);
    public int WarningCount => Issues.Count(i => i.Severity == ReadinessSeverity.Warning);
}

/// <summary>
/// Verifies an album is genuinely ready to upload, by reading the files on disk
/// rather than trusting what the app thinks it wrote.
///
/// The checks encode what YouTube Music actually needs to group an upload into a
/// single coherent album: one album title, one album artist, contiguous track
/// numbers from 1, a single disc, and embedded art on every file.
/// </summary>
public sealed class ReadinessChecker
{
    /// <summary>Below this, YouTube Music shows a visibly soft cover.</summary>
    private const int MinimumArtworkEdge = 600;

    /// <summary>The size worth holding out for on a limited-run soundtrack.</summary>
    private const int PreferredArtworkEdge = 1000;

    /// <summary>
    /// Runs every check against the files in the project's output folder.
    /// </summary>
    public ReadinessReport Check(AlbumProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var issues = new List<ReadinessIssue>();

        CheckAlbumLevel(project, issues);
        var checkedCount = CheckFiles(project, issues);

        var ordered = issues
            .OrderByDescending(i => i.Severity)
            .ThenBy(i => i.TrackNumber ?? 0)
            .ToList();

        return new ReadinessReport(ordered, checkedCount);
    }

    /// <summary>Checks the things that apply to the album as a whole.</summary>
    private static void CheckAlbumLevel(AlbumProject project, List<ReadinessIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(project.AlbumTitle))
            issues.Add(new(ReadinessSeverity.Error, "The album has no title.", null));

        if (string.IsNullOrWhiteSpace(project.AlbumArtist))
            issues.Add(new(ReadinessSeverity.Error,
                "The album has no album artist. Without it YouTube Music will split the upload into separate albums.", null));

        if (project.Year is null or <= 0)
            issues.Add(new(ReadinessSeverity.Warning, "The album has no release year.", null));

        if (project.Tracks.Count == 0)
            issues.Add(new(ReadinessSeverity.Error, "The album has no tracks.", null));

        if (project.CoverArt is not { Length: > 0 })
        {
            issues.Add(new(ReadinessSeverity.Error, "No cover art has been chosen.", null));
        }
        else
        {
            var edge = Math.Min(project.CoverArtWidth, project.CoverArtHeight);
            if (edge > 0 && edge < MinimumArtworkEdge)
                issues.Add(new(ReadinessSeverity.Error,
                    $"Cover art is only {project.CoverArtWidth}x{project.CoverArtHeight}. " +
                    $"Below {MinimumArtworkEdge}x{MinimumArtworkEdge} it will look blurry.", null));
            else if (edge > 0 && edge < PreferredArtworkEdge)
                issues.Add(new(ReadinessSeverity.Warning,
                    $"Cover art is {project.CoverArtWidth}x{project.CoverArtHeight}. " +
                    $"{PreferredArtworkEdge}x{PreferredArtworkEdge} or larger is preferred.", null));

            if (project.CoverArtWidth > 0 && project.CoverArtHeight > 0)
            {
                var ratio = (double)project.CoverArtWidth / project.CoverArtHeight;
                if (ratio is < 0.95 or > 1.05)
                    issues.Add(new(ReadinessSeverity.Warning,
                        $"Cover art is not square ({project.CoverArtWidth}x{project.CoverArtHeight}); it will be cropped.", null));
            }
        }

        // Flattening must have produced a contiguous 1..N run. A gap or a repeat
        // means renumbering did not run, or ran against a stale track list.
        var numbers = project.Tracks.Select(t => t.FlatTrackNumber).OrderBy(n => n).ToList();
        if (numbers.Count > 0)
        {
            var duplicates = numbers.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            foreach (var dup in duplicates)
                issues.Add(new(ReadinessSeverity.Error, $"Track number {dup} is used by more than one track.", dup));

            if (numbers[0] != 1)
                issues.Add(new(ReadinessSeverity.Error, $"Track numbering starts at {numbers[0]} instead of 1.", null));

            for (var i = 1; i < numbers.Count; i++)
            {
                if (numbers[i] != numbers[i - 1] + 1 && numbers[i] != numbers[i - 1])
                {
                    issues.Add(new(ReadinessSeverity.Error,
                        $"Track numbering jumps from {numbers[i - 1]} to {numbers[i]}.", numbers[i]));
                    break;
                }
            }
        }
    }

    /// <summary>Inspects each FLAC on disk. Returns how many were readable.</summary>
    private static int CheckFiles(AlbumProject project, List<ReadinessIssue> issues)
    {
        var checkedCount = 0;

        foreach (var track in project.Tracks.OrderBy(t => t.FlatTrackNumber))
        {
            var n = track.FlatTrackNumber;

            if (!track.IsRipped)
            {
                issues.Add(new(ReadinessSeverity.Error, $"Track {n} \"{track.Title}\" has not been ripped.", n));
                continue;
            }

            if (string.IsNullOrEmpty(track.FilePath) || !File.Exists(track.FilePath))
            {
                issues.Add(new(ReadinessSeverity.Error, $"Track {n} \"{track.Title}\" is missing from disk.", n));
                continue;
            }

            if (track.HadReadErrors)
                issues.Add(new(ReadinessSeverity.Warning,
                    $"Track {n} \"{track.Title}\" had read errors during ripping and may contain audible glitches.", n));

            Track file;
            try
            {
                file = new Track(track.FilePath);
            }
            catch (Exception ex)
            {
                issues.Add(new(ReadinessSeverity.Error, $"Track {n} could not be read: {ex.Message}", n));
                continue;
            }

            checkedCount++;

            if (string.IsNullOrWhiteSpace(file.Title))
                issues.Add(new(ReadinessSeverity.Error, $"Track {n} has no title tag.", n));
            else if (file.Title != FileNaming.StripLeadingTrackNumber(file.Title))
                issues.Add(new(ReadinessSeverity.Warning,
                    $"Track {n} title still begins with a track number: \"{file.Title}\".", n));

            if (string.IsNullOrWhiteSpace(file.Album))
                issues.Add(new(ReadinessSeverity.Error, $"Track {n} has no album tag.", n));
            else if (!string.Equals(file.Album, project.AlbumTitle, StringComparison.Ordinal))
                issues.Add(new(ReadinessSeverity.Error,
                    $"Track {n} has a different album tag (\"{file.Album}\") from the rest of the set.", n));

            if (string.IsNullOrWhiteSpace(file.AlbumArtist))
                issues.Add(new(ReadinessSeverity.Error, $"Track {n} has no album artist tag.", n));
            else if (!string.Equals(file.AlbumArtist, project.AlbumArtist, StringComparison.Ordinal))
                issues.Add(new(ReadinessSeverity.Error,
                    $"Track {n} has a different album artist (\"{file.AlbumArtist}\") from the rest of the set.", n));

            if (string.IsNullOrWhiteSpace(file.Artist))
                issues.Add(new(ReadinessSeverity.Warning, $"Track {n} has no artist tag.", n));

            if (file.TrackNumber is null or <= 0)
                issues.Add(new(ReadinessSeverity.Error, $"Track {n} has no track number tag.", n));
            else if (file.TrackNumber != n)
                issues.Add(new(ReadinessSeverity.Error,
                    $"Track {n} is tagged as track {file.TrackNumber}; flattening did not reach this file.", n));

            // A disc number above 1 means the file still claims to be part of a
            // multi-disc set, which is exactly what flattening exists to remove.
            if (file.DiscNumber is > 1)
                issues.Add(new(ReadinessSeverity.Error,
                    $"Track {n} is still tagged as disc {file.DiscNumber}; it should be disc 1 of 1.", n));

            if (file.DiscTotal is > 1)
                issues.Add(new(ReadinessSeverity.Error,
                    $"Track {n} still reports a disc total of {file.DiscTotal}; it should be 1.", n));

            if (file.EmbeddedPictures.Count == 0)
                issues.Add(new(ReadinessSeverity.Error, $"Track {n} has no embedded cover art.", n));

            // A FLAC that decodes to nothing is a truncated or failed rip.
            //
            // DurationMs, not Duration: the latter is whole seconds truncated to
            // an int, so a short stinger cue - which scores are full of - would
            // read as zero and be reported as an empty file.
            if (file.DurationMs <= 0)
            {
                issues.Add(new(ReadinessSeverity.Error, $"Track {n} appears to contain no audio.", n));
            }
            else if (track.IsSilent || IsProbablySilent(file, track.FilePath!))
            {
                // The dangerous failure: a full-length file with nothing in it.
                // The duration is right, the tags are right, and it plays as
                // silence. Caught here because it cannot be heard from the UI.
                issues.Add(new(ReadinessSeverity.Error,
                    $"Track {n} \"{track.Title}\" is silent - it has the right length but no audio. Re-rip this disc.", n));
            }

            var extension = Path.GetExtension(track.FilePath);
            if (!string.Equals(extension, ".flac", StringComparison.OrdinalIgnoreCase))
                issues.Add(new(ReadinessSeverity.Error, $"Track {n} is not a FLAC file ({extension}).", n));

            // Requirement: the file name must not carry a track number.
            var stem = Path.GetFileNameWithoutExtension(track.FilePath);
            if (stem != FileNaming.StripLeadingTrackNumber(stem))
                issues.Add(new(ReadinessSeverity.Warning,
                    $"Track {n} file name still begins with a track number: \"{stem}\".", n));
        }

        return checkedCount;
    }

    /// <summary>
    /// Detects a FLAC that is the right length but contains (near) silence.
    ///
    /// FLAC compresses digital silence to almost nothing, so the file's own
    /// bitrate gives it away: real CD audio lands somewhere around 400-1000 kbps,
    /// while a track of zeros comes out under 10. The threshold sits well below
    /// anything genuine - even a very quiet ambient cue does not approach it -
    /// so this does not fire on legitimately soft music.
    ///
    /// The file is checked rather than only the rip-time flag, so an album
    /// re-opened in a later session is judged on what is actually on disk.
    /// </summary>
    private static bool IsProbablySilent(Track file, string path)
    {
        const int silentBitrateCeiling = 30; // kbps

        var bitrate = (double)file.Bitrate;
        if (bitrate is > 0 and < silentBitrateCeiling) return true;

        // Bitrate is unavailable on some files; fall back to bytes per second,
        // which measures the same thing directly.
        if (bitrate <= 0 && file.DurationMs > 0)
        {
            try
            {
                var bytesPerSecond = new FileInfo(path).Length / (file.DurationMs / 1000.0);
                return bytesPerSecond < silentBitrateCeiling * 125; // kbps -> bytes/s
            }
            catch (IOException)
            {
                return false;
            }
        }

        return false;
    }
}
