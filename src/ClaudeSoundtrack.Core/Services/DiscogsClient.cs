using System.Text.Json;

namespace ClaudeSoundtrack.Core.Services;

/// <summary>
/// Searches Discogs for a release.
///
/// Discogs is the source that actually covers this collection. Expanded and
/// complete-score pressings from La-La Land, Intrada, Varese Sarabande and
/// Quartet are limited runs that MusicBrainz frequently lacks entirely, but
/// Discogs is marketplace-driven so collectors catalogue them thoroughly.
///
/// It has no disc-ID equivalent, so a match here is a text search and must be
/// verified against the disc TOC before it is trusted - see
/// <see cref="MetadataLookupService"/>. That check is what distinguishes an
/// expanded edition from the original album of the same name.
///
/// Unauthenticated callers are rate-limited to 25 requests/minute and must send
/// a descriptive User-Agent; Discogs returns 403 without one.
/// </summary>
public sealed class DiscogsClient
{
    private const string BaseUrl = "https://api.discogs.com";
    private readonly HttpClient _http;

    public DiscogsClient(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// Searches releases by free text, e.g. an album title or a catalogue number.
    /// </summary>
    /// <param name="query">Search text.</param>
    /// <param name="catalogNumber">Catalogue number to narrow the search, when known.</param>
    /// <param name="barcode">UPC/EAN from the disc, which pins the exact pressing when present.</param>
    public async Task<IReadOnlyList<ReleaseMatch>> SearchAsync(
        string? query,
        string? catalogNumber = null,
        string? barcode = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<string> { "type=release", "format=CD", "per_page=25" };

        if (!string.IsNullOrWhiteSpace(barcode)) parameters.Add($"barcode={Uri.EscapeDataString(barcode)}");
        if (!string.IsNullOrWhiteSpace(catalogNumber)) parameters.Add($"catno={Uri.EscapeDataString(catalogNumber)}");
        if (!string.IsNullOrWhiteSpace(query)) parameters.Add($"q={Uri.EscapeDataString(query)}");

        // Every filter was empty; a bare search would return the whole database.
        if (parameters.Count == 3) return [];

        var url = $"{BaseUrl}/database/search?{string.Join("&", parameters)}";

        List<string> ids;
        try
        {
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return [];

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("results", out var results)) return [];

            ids = results.EnumerateArray()
                .Select(r => r.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number
                    ? id.GetInt32().ToString()
                    : null)
                .Where(id => id is not null)
                .Select(id => id!)
                // The search endpoint omits track lists, so each candidate needs
                // its own fetch. Cap it: 25 fetches would blow the rate limit and
                // the right release is essentially always near the top.
                .Take(6)
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return [];
        }

        var matches = new List<ReleaseMatch>();
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var release = await GetReleaseAsync(id, cancellationToken).ConfigureAwait(false);
            if (release is not null) matches.Add(release);
        }

        return matches;
    }

    /// <summary>
    /// Fetches one release in full, including its track list.
    /// </summary>
    public async Task<ReleaseMatch?> GetReleaseAsync(string releaseId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http
                .GetAsync($"{BaseUrl}/releases/{Uri.EscapeDataString(releaseId)}", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return null;

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            return ParseRelease(doc.RootElement, releaseId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the image URLs Discogs holds for a release, largest first.
    /// Discogs scans are often the only artwork that exists for a limited run.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetImageUrlsAsync(string releaseId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http
                .GetAsync($"{BaseUrl}/releases/{Uri.EscapeDataString(releaseId)}", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return [];

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
                return [];

            return images.EnumerateArray()
                // "primary" is the front cover; "secondary" images are booklet
                // pages and disc faces, which are not what we want as the cover.
                .OrderByDescending(i => GetString(i, "type") == "primary")
                .ThenByDescending(i => i.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : 0)
                .Select(i => GetString(i, "resource_url") ?? GetString(i, "uri"))
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Select(u => u!)
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Parses a Discogs release, flattening its multi-disc track list.
    ///
    /// Discogs positions are strings, and multi-disc sets use "1-1", "2-1" or
    /// "CD1-1" rather than plain numbers. Index tracks (headings with no
    /// position) are skipped - they are section titles, not audio.
    /// </summary>
    private static ReleaseMatch? ParseRelease(JsonElement root, string releaseId)
    {
        var title = GetString(root, "title");
        if (string.IsNullOrWhiteSpace(title)) return null;

        var artist = "Unknown Artist";
        if (root.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
        {
            var names = artists.EnumerateArray()
                .Select(a => GetString(a, "name"))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(CleanArtistName);
            var joined = string.Join(", ", names);
            if (!string.IsNullOrWhiteSpace(joined)) artist = joined;
        }

        int? year = null;
        if (root.TryGetProperty("year", out var yearEl) && yearEl.ValueKind == JsonValueKind.Number)
        {
            var y = yearEl.GetInt32();
            if (y > 0) year = y;
        }

        string? label = null, catalogNumber = null;
        if (root.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array)
        {
            var first = labels.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object)
            {
                label = GetString(first, "name");
                catalogNumber = GetString(first, "catno");
            }
        }

        var titles = new List<string>();

        // Keyed by the disc part of the position string, in first-seen order, so
        // a set listed as "CD1"/"CD2" groups the same way as one listed "1"/"2".
        var byDisc = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var discOrder = new List<string>();

        if (root.TryGetProperty("tracklist", out var tracklist) && tracklist.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in tracklist.EnumerateArray())
            {
                // Headings and sides carry type_ "index"/"heading" and no audio.
                var type = GetString(entry, "type_");
                if (type is not null && !string.Equals(type, "track", StringComparison.OrdinalIgnoreCase)) continue;

                var position = GetString(entry, "position");
                if (string.IsNullOrWhiteSpace(position)) continue;

                var trackTitle = GetString(entry, "title");
                if (string.IsNullOrWhiteSpace(trackTitle)) continue;

                titles.Add(trackTitle);

                // "2-14" and "CD2-14" both mean disc 2; a bare "14" means the
                // release is single-disc and everything lands in one bucket.
                var dash = position.IndexOf('-');
                var discKey = dash > 0 ? position[..dash].Trim() : "1";

                if (!byDisc.TryGetValue(discKey, out var list))
                {
                    list = new List<string>();
                    byDisc[discKey] = list;
                    discOrder.Add(discKey);
                }

                list.Add(trackTitle);
            }
        }

        // Sort discs numerically where the key permits it, so "CD10" does not
        // sort before "CD2"; fall back to first-seen order otherwise.
        var orderedKeys = discOrder
            .OrderBy(k =>
            {
                var digits = new string(k.Where(char.IsDigit).ToArray());
                return int.TryParse(digits, out var n) ? n : int.MaxValue;
            })
            .ThenBy(discOrder.IndexOf)
            .ToList();

        var tracksByDisc = orderedKeys.Select(k => (IReadOnlyList<string>)byDisc[k]).ToList();
        var discCount = tracksByDisc.Count == 0 ? 1 : tracksByDisc.Count;

        return new ReleaseMatch(
            Source: "Discogs",
            Title: title,
            Artist: artist,
            Year: year,
            TrackTitles: titles,
            DiscNumber: 1,
            DiscCount: discCount,
            Label: label,
            CatalogNumber: catalogNumber,
            ReleaseId: releaseId,
            // Text search only. The TOC check in MetadataLookupService decides.
            IsTocVerified: false)
        {
            TracksByDisc = tracksByDisc
        };
    }

    /// <summary>
    /// Strips the disambiguation suffix Discogs appends to duplicate artist
    /// names, so "John Williams (4)" is tagged as "John Williams".
    /// </summary>
    private static string CleanArtistName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var trimmed = name.Trim();
        var paren = trimmed.LastIndexOf(" (", StringComparison.Ordinal);
        if (paren > 0 && trimmed.EndsWith(')') &&
            int.TryParse(trimmed[(paren + 2)..^1], out _))
        {
            return trimmed[..paren];
        }
        return trimmed;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
