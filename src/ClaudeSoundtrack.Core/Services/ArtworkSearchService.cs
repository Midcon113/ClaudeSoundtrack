using System.Net.Http.Headers;
using System.Text.Json;
using ClaudeSoundtrack.Core.Models;

namespace ClaudeSoundtrack.Core.Services;

/// <summary>
/// Hunts for high-resolution cover art across several sources.
///
/// This is the hardest part of the pipeline to do well. Expanded and complete
/// soundtracks are limited pressings, so the obvious sources frequently have
/// nothing, or have the original album's cover rather than the reissue's. Three
/// sources are tried because each fails differently:
///
///   - iTunes covers commercial releases and serves genuinely large art. Its
///     URLs embed the requested size, which can be raised well past what the
///     search response advertises.
///   - Cover Art Archive is keyed off a MusicBrainz release, so when the disc ID
///     lookup succeeded the art is guaranteed to belong to this exact pressing.
///   - Discogs holds collector-submitted scans, which is often the only place a
///     limited run's cover exists at all.
///
/// Nothing is written from here. Candidates are returned for the user to confirm.
/// </summary>
public sealed class ArtworkSearchService : IDisposable
{
    private const string UserAgent =
        "ClaudeSoundtrack/1.0 (+https://github.com/Midcon113/ClaudeSoundtrack)";

    private readonly HttpClient _http;
    private readonly DiscogsClient? _discogs;
    private readonly bool _ownsHttp;

    /// <param name="discogs">
    /// Optional shared Discogs client. Reusing the lookup service's client keeps
    /// both callers under the same unauthenticated rate limit.
    /// </param>
    public ArtworkSearchService(DiscogsClient? discogs = null)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _ownsHttp = true;
        _discogs = discogs;
    }

    /// <summary>
    /// Searches every source and returns candidates, largest first.
    /// </summary>
    /// <param name="albumTitle">Album title to search for.</param>
    /// <param name="artist">Album artist, used to narrow the iTunes search.</param>
    /// <param name="musicBrainzReleaseId">Release id from a disc ID hit, when there was one.</param>
    /// <param name="musicBrainzReleaseGroupId">Release group id, as a fallback for Cover Art Archive.</param>
    /// <param name="discogsReleaseId">Discogs release id, when a Discogs match was chosen.</param>
    public async Task<IReadOnlyList<ArtworkCandidate>> SearchAsync(
        string albumTitle,
        string? artist = null,
        string? musicBrainzReleaseId = null,
        string? musicBrainzReleaseGroupId = null,
        string? discogsReleaseId = null,
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<ArtworkCandidate>();

        // Run the independent sources together; each already swallows its own
        // failures, so one dead service cannot stall the others.
        var itunesTask = SearchITunesAsync(albumTitle, artist, cancellationToken);
        var caaTask = SearchCoverArtArchiveAsync(musicBrainzReleaseId, musicBrainzReleaseGroupId, cancellationToken);
        var discogsTask = SearchDiscogsAsync(discogsReleaseId, cancellationToken);

        await Task.WhenAll(itunesTask, caaTask, discogsTask).ConfigureAwait(false);

        candidates.AddRange(await itunesTask.ConfigureAwait(false));
        candidates.AddRange(await caaTask.ConfigureAwait(false));
        candidates.AddRange(await discogsTask.ConfigureAwait(false));

        // Fetch the bytes so the picker can show real thumbnails and, more
        // importantly, real resolutions. Without this every candidate would
        // claim "unknown" and the user could not tell them apart.
        await Task.WhenAll(candidates.Select(c => FetchAsync(c, cancellationToken))).ConfigureAwait(false);

        return candidates
            .Where(c => c.ImageData is { Length: > 0 })
            .GroupBy(c => c.ImageData!.Length) // identical downloads from two sources
            .Select(g => g.First())
            .OrderByDescending(c => Math.Min(c.Width, c.Height))
            .ToList();
    }

    /// <summary>
    /// Searches the iTunes Store, which needs no API key.
    ///
    /// The artwork URL it returns is a 100x100 thumbnail, but the size is encoded
    /// in the path, so swapping it for a larger one yields the full-resolution
    /// master - typically 1400x1400 or better. That trick is the single most
    /// reliable way to get high-res art for a commercially released score.
    /// </summary>
    private async Task<List<ArtworkCandidate>> SearchITunesAsync(
        string albumTitle, string? artist, CancellationToken cancellationToken)
    {
        var results = new List<ArtworkCandidate>();
        if (string.IsNullOrWhiteSpace(albumTitle)) return results;

        var term = string.IsNullOrWhiteSpace(artist) ? albumTitle : $"{artist} {albumTitle}";
        var url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(term)}&entity=album&limit=8";

        try
        {
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return results;

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("results", out var items)) return results;

            foreach (var item in items.EnumerateArray())
            {
                var art = GetString(item, "artworkUrl100");
                if (string.IsNullOrWhiteSpace(art)) continue;

                results.Add(new ArtworkCandidate
                {
                    Source = "iTunes",
                    ImageUrl = UpscaleITunesUrl(art),
                    ReleaseTitle = GetString(item, "collectionName"),
                    ReleaseYear = GetString(item, "releaseDate") is { Length: >= 4 } d ? d[..4] : null
                });
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Source unavailable; the others still stand a chance.
        }

        return results;
    }

    /// <summary>
    /// Rewrites an iTunes artwork URL to request the full-resolution master.
    ///
    /// The path ends in the pixel size, e.g. ".../source/100x100bb.jpg". Asking
    /// for 100000x100000 returns the largest master Apple holds rather than an
    /// upscale, so there is no quality cost to asking high.
    /// </summary>
    private static string UpscaleITunesUrl(string url)
    {
        var slash = url.LastIndexOf('/');
        if (slash < 0) return url;

        var directory = url[..(slash + 1)];
        var file = url[(slash + 1)..];

        // Keep the original extension; Apple serves both .jpg and .png.
        var extension = Path.GetExtension(file);
        if (string.IsNullOrEmpty(extension)) extension = ".jpg";

        return $"{directory}100000x100000-999{extension}";
    }

    /// <summary>
    /// Fetches art from the Cover Art Archive for a MusicBrainz release.
    ///
    /// Only reached when the disc ID lookup matched, which means anything found
    /// here belongs to this exact pressing - the strongest guarantee available.
    /// </summary>
    private async Task<List<ArtworkCandidate>> SearchCoverArtArchiveAsync(
        string? releaseId, string? releaseGroupId, CancellationToken cancellationToken)
    {
        var results = new List<ArtworkCandidate>();

        foreach (var (kind, id) in new[] { ("release", releaseId), ("release-group", releaseGroupId) })
        {
            if (string.IsNullOrWhiteSpace(id)) continue;

            try
            {
                using var response = await _http
                    .GetAsync($"https://coverartarchive.org/{kind}/{id}", cancellationToken)
                    .ConfigureAwait(false);

                // 404 means no art was submitted for this release. Common.
                if (!response.IsSuccessStatusCode) continue;

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!doc.RootElement.TryGetProperty("images", out var images)) continue;

                foreach (var image in images.EnumerateArray())
                {
                    // Prefer the image explicitly flagged as the front cover.
                    var isFront = image.TryGetProperty("front", out var f) && f.ValueKind == JsonValueKind.True;
                    if (!isFront) continue;

                    var imageUrl = GetString(image, "image");
                    if (string.IsNullOrWhiteSpace(imageUrl)) continue;

                    results.Add(new ArtworkCandidate
                    {
                        Source = kind == "release" ? "Cover Art Archive (this pressing)" : "Cover Art Archive",
                        ImageUrl = imageUrl,
                        ReleaseTitle = null
                    });
                }

                // A hit on the exact release makes the release-group fallback redundant.
                if (results.Count > 0) break;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // Try the next identifier.
            }
        }

        return results;
    }

    /// <summary>Pulls the primary scans Discogs holds for the matched release.</summary>
    private async Task<List<ArtworkCandidate>> SearchDiscogsAsync(
        string? discogsReleaseId, CancellationToken cancellationToken)
    {
        var results = new List<ArtworkCandidate>();
        if (_discogs is null || string.IsNullOrWhiteSpace(discogsReleaseId)) return results;

        try
        {
            var urls = await _discogs.GetImageUrlsAsync(discogsReleaseId, cancellationToken).ConfigureAwait(false);
            results.AddRange(urls.Take(4).Select(u => new ArtworkCandidate
            {
                Source = "Discogs",
                ImageUrl = u
            }));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Discogs unavailable or rate-limited.
        }

        return results;
    }

    /// <summary>
    /// Downloads a candidate's bytes and reads its real dimensions.
    ///
    /// Safe to call on a candidate that has already been fetched, and safe to
    /// call concurrently - failures leave <see cref="ArtworkCandidate.ImageData"/>
    /// null and the candidate is filtered out.
    /// </summary>
    public async Task FetchAsync(ArtworkCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.ImageData is { Length: > 0 }) return;

        try
        {
            byte[] bytes;

            if (!string.IsNullOrWhiteSpace(candidate.LocalPath))
            {
                bytes = await File.ReadAllBytesAsync(candidate.LocalPath, cancellationToken).ConfigureAwait(false);
            }
            else if (!string.IsNullOrWhiteSpace(candidate.ImageUrl))
            {
                bytes = await _http.GetByteArrayAsync(candidate.ImageUrl, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                return;
            }

            // A 404 page or an error blob would otherwise be embedded as "art".
            if (bytes.Length < 512) return;

            var size = ImageDimensions.TryGet(bytes);
            if (size is null) return;

            candidate.ImageData = bytes;
            candidate.Width = size.Value.Width;
            candidate.Height = size.Value.Height;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // Leave the candidate unfetched; the caller filters it out.
        }
    }

    /// <summary>
    /// Wraps a file the user picked themselves as a candidate.
    ///
    /// This is the escape hatch for the discs nothing online covers: the user
    /// scans or downloads the cover, saves it locally, and points the app at it.
    /// </summary>
    public static ArtworkCandidate FromLocalFile(string path)
    {
        var candidate = new ArtworkCandidate
        {
            Source = "Local file",
            LocalPath = path,
            ReleaseTitle = Path.GetFileName(path)
        };

        try
        {
            var bytes = File.ReadAllBytes(path);
            var size = ImageDimensions.TryGet(bytes);
            if (size is not null)
            {
                candidate.ImageData = bytes;
                candidate.Width = size.Value.Width;
                candidate.Height = size.Value.Height;
            }
        }
        catch (IOException)
        {
            // Unreadable file; the caller sees no image data and reports it.
        }

        return candidate;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
