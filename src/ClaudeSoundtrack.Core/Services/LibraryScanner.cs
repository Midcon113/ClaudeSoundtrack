using ATL;
using ClaudeSoundtrack.Core.Models;

namespace ClaudeSoundtrack.Core.Services;

/// <summary>
/// Finds the albums already sitting in the library folder.
///
/// Reads the files directly rather than maintaining an index. A collection of a
/// few hundred albums scans in well under a second, and there is no cache to go
/// stale when an album is ripped, renamed or deleted outside the app.
/// </summary>
public sealed class LibraryScanner
{
    /// <summary>Formats worth listing. FLAC is what this app produces; the rest may already be there.</summary>
    private static readonly string[] AudioExtensions =
        [".flac", ".mp3", ".m4a", ".wav", ".ogg", ".opus", ".wma"];

    /// <summary>
    /// Scans <paramref name="root"/> for album folders.
    ///
    /// One folder of audio files is one album. Folders are searched recursively,
    /// because a library organised as Artist\Album is at least as common as a flat
    /// one, but a folder that directly contains audio is treated as the album and
    /// not descended into further.
    /// </summary>
    public IReadOnlyList<LibraryAlbum> Scan(string root, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return [];

        var albums = new List<LibraryAlbum>();
        ScanFolder(root, albums, depth: 0, cancellationToken);

        return albums
            .OrderBy(a => a.Artist, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(a => a.Year ?? int.MaxValue)
            .ThenBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void ScanFolder(string folder, List<LibraryAlbum> albums, int depth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Deep enough for Artist\Album\Disc without wandering off into a whole drive.
        if (depth > 3) return;

        string[] files;
        string[] subFolders;

        try
        {
            files = Directory.GetFiles(folder)
                .Where(f => AudioExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .ToArray();
            subFolders = Directory.GetDirectories(folder);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // A folder we cannot read is not worth failing the whole scan over.
            return;
        }

        if (files.Length > 0)
        {
            var album = BuildAlbum(folder, files, cancellationToken);
            if (album is not null) albums.Add(album);

            // This folder is the album; its subfolders are artwork, scans, logs.
            return;
        }

        foreach (var sub in subFolders)
        {
            ScanFolder(sub, albums, depth + 1, cancellationToken);
        }
    }

    /// <summary>Reads the tags of every file in a folder and assembles an album from them.</summary>
    private static LibraryAlbum? BuildAlbum(string folder, string[] files, CancellationToken cancellationToken)
    {
        var tracks = new List<LibraryTrack>();
        byte[]? cover = null;
        string? albumTitle = null;
        string? albumArtist = null;
        int? year = null;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Track tag;
            try
            {
                tag = new Track(file);
            }
            catch
            {
                // Unreadable or not really audio; skip it rather than lose the album.
                continue;
            }

            var title = string.IsNullOrWhiteSpace(tag.Title)
                ? Path.GetFileNameWithoutExtension(file)
                : tag.Title;

            tracks.Add(new LibraryTrack(
                FilePath: file,
                TrackNumber: tag.TrackNumber ?? 0,
                Title: title,
                Artist: tag.Artist ?? string.Empty,
                Duration: TimeSpan.FromMilliseconds(tag.DurationMs)));

            albumTitle ??= string.IsNullOrWhiteSpace(tag.Album) ? null : tag.Album;
            albumArtist ??= string.IsNullOrWhiteSpace(tag.AlbumArtist)
                ? (string.IsNullOrWhiteSpace(tag.Artist) ? null : tag.Artist)
                : tag.AlbumArtist;
            year ??= tag.Year is > 0 ? tag.Year : null;

            // Art is taken from whichever track has it first; a properly tagged
            // album has the same cover on every track anyway.
            if (cover is null && tag.EmbeddedPictures.Count > 0)
            {
                var data = tag.EmbeddedPictures[0].PictureData;
                if (data is { Length: > 0 }) cover = data;
            }
        }

        if (tracks.Count == 0) return null;

        return new LibraryAlbum
        {
            Folder = folder,
            // An untagged folder still deserves to be listed, named after itself.
            Title = albumTitle ?? Path.GetFileName(folder),
            Artist = albumArtist ?? "Unknown Artist",
            Year = year,
            CoverArt = cover,
            Tracks = tracks
                .OrderBy(t => t.TrackNumber == 0 ? int.MaxValue : t.TrackNumber)
                .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList()
        };
    }
}
