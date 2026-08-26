using System.Net.Http.Json;
using System.Text.Json;
using ClaudeSoundtrack.Core.Models;

namespace ClaudeSoundtrack.Core.Services;

/// <summary>
/// A release matched to the disc in the drive.
/// </summary>
/// <param name="Source">Which service supplied it, for display.</param>
/// <param name="Title">Album title.</param>
/// <param name="Artist">Album artist / composer.</param>
/// <param name="Year">Release year, when known.</param>
/// <param name="TrackTitles">Track titles in disc order.</param>
/// <param name="DiscNumber">Which disc of the set this matched, when the release says.</param>
/// <param name="DiscCount">How many discs the release has.</param>
/// <param name="Label">Record label.</param>
/// <param name="CatalogNumber">Catalogue number.</param>
/// <param name="ReleaseId">Service-specific id, used to look up artwork later.</param>
/// <param name="IsTocVerified">
/// True when the match was confirmed against the disc's own TOC rather than
/// just a text search.
/// </param>
public sealed record ReleaseMatch(
    string Source,
    string Title,
    string Artist,
    int? Year,
    IReadOnlyList<string> TrackTitles,
    int DiscNumber,
    int DiscCount,
    string? Label,
    string? CatalogNumber,
    string? ReleaseId,
    bool IsTocVerified)
{
    /// <summary>MusicBrainz release group id, when known. Used for Cover Art Archive lookups.</summary>
    public string? ReleaseGroupId { get; init; }

    /// <summary>
    /// Track titles split by disc, disc 1 first.
    ///
    /// Verification compares one physical disc against one entry here. Comparing
    /// against the flattened <see cref="TrackTitles"/> would reject every
    /// multi-disc release, because a 20-track disc can never match a 60-track set.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> TracksByDisc { get; init; } = [];

    /// <summary>Titles for a given 1-based disc, falling back to the flat list.</summary>
    public IReadOnlyList<string> TitlesForDisc(int discNumber)
    {
        if (TracksByDisc.Count == 0) return TrackTitles;
        var index = discNumber - 1;
        return index >= 0 && index < TracksByDisc.Count ? TracksByDisc[index] : [];
    }
}

/// <summary>
/// Looks releases up in MusicBrainz.
///
/// The disc ID path is the valuable one: FoxRedbook computes a MusicBrainz disc
/// ID from the TOC, and a hit on that is an exact match to this pressing, not a
/// guess from a title. When it hits, it is trustworthy without further checking.
/// MusicBrainz is thin on limited-run soundtracks though, so a miss here is
/// normal and Discogs takes over.
/// </summary>
public sealed class MusicBrainzClient
{
    private const string BaseUrl = "https://musicbrainz.org/ws/2";
    private readonly HttpClient _http;

    public MusicBrainzClient(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// Looks up the exact disc by its MusicBrainz disc ID.
    ///
    /// Returns every release that claims this disc; a disc pressed for several
    /// territories can legitimately match more than one.
    /// </summary>
    public async Task<IReadOnlyList<ReleaseMatch>> LookupByDiscIdAsync(
        string discId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(discId)) return [];

        var url = $"{BaseUrl}/discid/{Uri.EscapeDataString(discId)}?inc=artist-credits+recordings+release-groups+labels&fmt=json";

        try
        {
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);

            // 404 simply means nobody has submitted this pressing. Not an error.
            if (!response.IsSuccessStatusCode) return [];

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("releases", out var releases)) return [];

            var matches = new List<ReleaseMatch>();
            foreach (var release in releases.EnumerateArray())
            {
                var match = ParseRelease(release, discId);
                if (match is not null) matches.Add(match);
            }

            return matches;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Turns one MusicBrainz release into a <see cref="ReleaseMatch"/>.
    ///
    /// Only the medium carrying our disc ID is used for the track list; taking
    /// the first medium of a 4-disc set would give the wrong titles entirely.
    /// </summary>
    private static ReleaseMatch? ParseRelease(JsonElement release, string discId)
    {
        var title = GetString(release, "title");
        if (string.IsNullOrWhiteSpace(title)) return null;

        var artist = "Unknown Artist";
        if (release.TryGetProperty("artist-credit", out var credits) && credits.ValueKind == JsonValueKind.Array)
        {
            var names = credits.EnumerateArray()
                .Select(c => GetString(c, "name"))
                .Where(n => !string.IsNullOrWhiteSpace(n));
            var joined = string.Join(", ", names);
            if (!string.IsNullOrWhiteSpace(joined)) artist = joined;
        }

        int? year = null;
        var date = GetString(release, "date");
        if (!string.IsNullOrWhiteSpace(date) && date.Length >= 4 && int.TryParse(date[..4], out var y)) year = y;

        string? label = null, catalogNumber = null;
        if (release.TryGetProperty("label-info", out var labelInfo) && labelInfo.ValueKind == JsonValueKind.Array)
        {
            foreach (var info in labelInfo.EnumerateArray())
            {
                catalogNumber ??= GetString(info, "catalog-number");
                if (label is null && info.TryGetProperty("label", out var labelObj))
                    label = GetString(labelObj, "name");
            }
        }

        var titles = new List<string>();
        var discNumber = 1;
        var discCount = 1;

        if (release.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Array)
        {
            var mediaList = media.EnumerateArray().ToList();
            discCount = mediaList.Count == 0 ? 1 : mediaList.Count;

            // Find the medium whose disc list contains our disc ID.
            var index = mediaList.FindIndex(m =>
                m.TryGetProperty("discs", out var discs) &&
                discs.ValueKind == JsonValueKind.Array &&
                discs.EnumerateArray().Any(d => GetString(d, "id") == discId));

            if (index < 0) index = 0;
            discNumber = index + 1;

            if (mediaList.Count > index &&
                mediaList[index].TryGetProperty("tracks", out var tracks) &&
                tracks.ValueKind == JsonValueKind.Array)
            {
                titles.AddRange(tracks.EnumerateArray()
                    .Select(t => GetString(t, "title"))
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t!));
            }
        }

        string? releaseGroupId = null;
        if (release.TryGetProperty("release-group", out var rg)) releaseGroupId = GetString(rg, "id");

        return new ReleaseMatch(
            Source: "MusicBrainz",
            Title: title,
            Artist: artist,
            Year: year,
            TrackTitles: titles,
            DiscNumber: discNumber,
            DiscCount: discCount,
            Label: label,
            CatalogNumber: catalogNumber,
            ReleaseId: GetString(release, "id"),
            // A disc ID hit is an exact TOC fingerprint match by construction.
            IsTocVerified: true)
        {
            ReleaseGroupId = releaseGroupId
        };
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
