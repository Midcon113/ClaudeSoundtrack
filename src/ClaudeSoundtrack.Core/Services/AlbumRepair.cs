using ATL;
using ClaudeSoundtrack.Core.Models;

namespace ClaudeSoundtrack.Core.Services;

/// <summary>What a repair would change, shown before anything is written.</summary>
public sealed record AlbumRepairPlan(
    string FromTitle,
    string ToTitle,
    string AlbumArtist,
    string? FromFolder,
    string? ToFolder,
    int DiscTotal,
    int FileCount,
    bool NeedsRetag,
    IReadOnlyList<string> Lines)
{
    public bool ChangesAnything =>
        NeedsRetag
        || (FromFolder is not null && ToFolder is not null
            && !string.Equals(FromFolder, ToFolder, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Retags an album that was ripped with a disc number baked into the title,
/// and renames its folder to match. Audio is not read and not rewritten.
/// Cover art already in the files is copied back unchanged, the same bytes
/// on every track.
/// </summary>
public static class AlbumRepair
{
    /// <summary>
    /// Builds a plan from the project's files. Null when there is nothing on
    /// disk to repair.
    /// </summary>
    public static AlbumRepairPlan? Plan(AlbumProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var ripped = project.Tracks.Where(t => t.IsRipped && !string.IsNullOrEmpty(t.FilePath) && File.Exists(t.FilePath)).ToList();
        if (ripped.Count == 0) return null;

        var rawTitle = FirstNonEmpty(ripped.Select(t => ReadAlbum(t.FilePath)), project.AlbumTitle);
        var cleaned = AlbumTitleCleaner.Parse(string.IsNullOrWhiteSpace(rawTitle) ? project.AlbumTitle : rawTitle);
        var toTitle = YouTubeMusicTags.Normalize(cleaned.HadDiscSuffix ? cleaned.Title : AlbumTitleCleaner.Parse(project.AlbumTitle).Title);
        if (string.IsNullOrEmpty(toTitle)) toTitle = YouTubeMusicTags.Normalize(project.AlbumTitle);
        if (string.IsNullOrEmpty(toTitle)) return null;

        var artist = YouTubeMusicTags.Normalize(
            string.IsNullOrWhiteSpace(project.AlbumArtist) ? ReadAlbumArtist(ripped[0].FilePath) : project.AlbumArtist);

        var discTotal = DiscTotalFor(project, cleaned.DiscCount, ripped);
        var fromFolder = project.OutputFolder;
        string? toFolder = null;
        if (!string.IsNullOrEmpty(fromFolder))
        {
            var parent = Path.GetDirectoryName(fromFolder);
            var desired = FileNaming.BuildAlbumFolderName(toTitle, artist, project.Year);
            if (!string.IsNullOrEmpty(parent))
                toFolder = Path.Combine(parent, desired);
        }

        var fromTitle = string.IsNullOrWhiteSpace(rawTitle) ? project.AlbumTitle : rawTitle;
        var needsRetag = !string.Equals(fromTitle, toTitle, StringComparison.Ordinal)
                         || ripped.Any(t => FileDiscTotal(t.FilePath) is int total && total != discTotal);
        var lines = new List<string>
        {
            $"Album title: \"{fromTitle}\" → \"{toTitle}\"",
            $"Album artist: \"{artist}\""
        };
        if (fromFolder is not null && toFolder is not null
            && !string.Equals(fromFolder, toFolder, StringComparison.OrdinalIgnoreCase))
            lines.Add($"Folder: {fromFolder} → {toFolder}");
        else if (fromFolder is not null)
            lines.Add($"Folder stays: {fromFolder}");

        lines.Add($"{ripped.Count} file(s): ALBUM, ALBUMARTIST, DISCNUMBER, DISCTOTAL and TRACKNUMBER rewritten.");
        lines.Add("Audio is not re-ripped. Embedded cover art is kept, the same bytes on every track.");

        return new AlbumRepairPlan(fromTitle, toTitle, artist, fromFolder, toFolder, discTotal, ripped.Count, needsRetag, lines);
    }

    /// <summary>Writes the plan. The project's in-memory title and paths follow the files.</summary>
    public static void Apply(AlbumProject project, AlbumRepairPlan plan)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(plan);

        project.AlbumTitle = plan.ToTitle;
        if (!string.IsNullOrWhiteSpace(plan.AlbumArtist))
            project.AlbumArtist = plan.AlbumArtist;
        if (plan.DiscTotal > project.DiscCount) project.DiscCount = plan.DiscTotal;
        project.IdentityLocked = true;

        MoveFolder(project, plan);

        // One picture, reused, so a repair cannot split the album by art.
        if (project.CoverArt is not { Length: > 0 })
            project.CoverArt = FirstPicture(project);

        TrackFlattener.Flatten(project);
        new TaggingService().WriteAllTags(project);
    }

    /// <summary>Loads FLAC tags from a folder into a new project. Used by the manual repair.</summary>
    public static AlbumProject LoadFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var project = new AlbumProject { OutputFolder = folder, IdentityLocked = true };
        LoadFolderInto(project);
        return project;
    }

    private static List<SoundtrackTrack> LoadFolderInto(AlbumProject project)
    {
        if (string.IsNullOrEmpty(project.OutputFolder) || !Directory.Exists(project.OutputFolder))
            return [];

        var added = new List<SoundtrackTrack>();
        foreach (var path in Directory.GetFiles(project.OutputFolder, "*.flac").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (project.Tracks.Any(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase)))
                continue;

            Track file;
            try { file = new Track(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            var track = new SoundtrackTrack
            {
                FilePath = path,
                IsRipped = true,
                Title = file.Title ?? Path.GetFileNameWithoutExtension(path),
                Artist = file.Artist ?? string.Empty,
                FlatTrackNumber = file.TrackNumber is > 0 ? file.TrackNumber.Value : added.Count + 1,
                SourceDiscNumber = file.DiscNumber is > 0 ? file.DiscNumber.Value : 1,
                SourceTrackNumber = file.TrackNumber is > 0 ? file.TrackNumber.Value : added.Count + 1,
                Duration = file.DurationMs > 0 ? TimeSpan.FromMilliseconds(file.DurationMs) : default
            };
            project.Tracks.Add(track);
            added.Add(track);

            if (string.IsNullOrWhiteSpace(project.AlbumTitle) && !string.IsNullOrWhiteSpace(file.Album))
                project.AlbumTitle = file.Album;
            if (string.IsNullOrWhiteSpace(project.AlbumArtist))
                project.AlbumArtist = string.IsNullOrWhiteSpace(file.AlbumArtist) ? file.Artist ?? "" : file.AlbumArtist;
            if (project.Year is null or <= 0 && file.Year is > 0) project.Year = file.Year;
            if (project.CoverArt is not { Length: > 0 } && file.EmbeddedPictures.Count > 0)
                project.CoverArt = file.EmbeddedPictures[0].PictureData.ToArray();
        }

        return added;
    }

    private static void MoveFolder(AlbumProject project, AlbumRepairPlan plan)
    {
        if (plan.FromFolder is null || plan.ToFolder is null) return;
        if (string.Equals(plan.FromFolder, plan.ToFolder, StringComparison.OrdinalIgnoreCase)) return;
        if (!Directory.Exists(plan.FromFolder)) return;

        if (Directory.Exists(plan.ToFolder))
            throw new IOException($"The cleaned folder already exists:\n{plan.ToFolder}");

        Directory.Move(plan.FromFolder, plan.ToFolder);
        foreach (var track in project.Tracks)
        {
            if (string.IsNullOrEmpty(track.FilePath)) continue;
            var name = Path.GetFileName(track.FilePath);
            track.FilePath = Path.Combine(plan.ToFolder, name);
        }

        project.OutputFolder = plan.ToFolder;
    }

    private static byte[]? FirstPicture(AlbumProject project)
    {
        foreach (var track in project.Tracks)
        {
            if (string.IsNullOrEmpty(track.FilePath) || !File.Exists(track.FilePath)) continue;
            try
            {
                var file = new Track(track.FilePath);
                if (file.EmbeddedPictures.Count > 0)
                    return file.EmbeddedPictures[0].PictureData.ToArray();
            }
            catch (IOException) { /* try the next file */ }
        }

        return null;
    }

    private static int DiscTotalFor(AlbumProject project, int? fromTitle, List<SoundtrackTrack> ripped)
    {
        var fromTracks = ripped.Count == 0 ? 1 : ripped.Max(t => t.SourceDiscNumber);
        var total = Math.Max(project.DiscCount, fromTracks);
        if (fromTitle is int fromSuffix && fromSuffix > total) total = fromSuffix;
        return Math.Max(1, total);
    }

    private static int? FileDiscTotal(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var total = new Track(path).DiscTotal;
            return total is > 0 ? total : null;
        }
        catch (IOException) { return null; }
    }

    private static string ReadAlbum(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return string.Empty;
        try { return new Track(path).Album ?? string.Empty; }
        catch (IOException) { return string.Empty; }
    }

    private static string ReadAlbumArtist(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return string.Empty;
        try
        {
            var file = new Track(path);
            return string.IsNullOrWhiteSpace(file.AlbumArtist) ? file.Artist ?? "" : file.AlbumArtist;
        }
        catch (IOException) { return string.Empty; }
    }

    private static string FirstNonEmpty(IEnumerable<string> values, string fallback)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return fallback;
    }
}
