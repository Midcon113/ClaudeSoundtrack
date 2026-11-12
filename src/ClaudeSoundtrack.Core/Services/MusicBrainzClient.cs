using System.Globalization;
using System.Net;
using System.Text.Json;
using ClaudeSoundtrack.Core.Models;
using FoxRedbook;

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

    /// <summary>Per-disc titles, lengths and disc ids. Empty when the source had no disc split.</summary>
    public IReadOnlyList<DiscMedium> Media { get; init; } = [];

    /// <summary>Why <see cref="IsTocVerified"/> is set, when it is.</summary>
    public DiscMatchKind MatchKind { get; init; } = DiscMatchKind.None;

    /// <summary>
    /// Titles for one disc.
    ///
    /// A flat <see cref="TrackTitles"/> list belongs to <see cref="DiscNumber"/> only.
    /// Handing it out for every disc is how disc 2 used to be tagged with disc 1's cues.
    /// </summary>
    public IReadOnlyList<string> TitlesForDisc(int discNumber)
    {
        var medium = Media.FirstOrDefault(m => m.Number == discNumber);
        if (medium is { Titles.Count: > 0 }) return medium.Titles;

        if (TracksByDisc.Count == 0)
            return discNumber == DiscNumber ? TrackTitles : [];

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

    /// <summary>
    /// Includes the disc-id endpoint accepts. <c>discids</c> is deliberately absent.
    /// </summary>
    private const string Include = "artist-credits+recordings+release-groups+labels";

    /// <summary>MusicBrainz asks for at most one request a second.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTimeOffset _nextRequest = DateTimeOffset.MinValue;

    private readonly HttpClient _http;

    public MusicBrainzClient(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// Parses a disc-id (or TOC) response. Public so the multi-disc mapping can
    /// be tested from a saved response, without calling MusicBrainz.
    /// </summary>
    public static IReadOnlyList<ReleaseMatch> ParseDiscIdResponse(string json, string? discId)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("releases", out var releases)) return [];

        var matches = new List<ReleaseMatch>();
        foreach (var release in releases.EnumerateArray())
        {
            var match = ParseRelease(release, discId);
            if (match is not null) matches.Add(match);
        }

        return matches;
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

        return await GetMatchesAsync(DiscIdUrl(discId), discId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// URL for an exact disc-id lookup.
    ///
    /// <c>discids</c> is not a legal include on this resource. MusicBrainz answers
    /// 400, and a 400 used to be treated as "no such disc", so a release that
    /// was in the database never appeared. The disc ids are already in the
    /// response without asking for them.
    /// </summary>
    public static string DiscIdUrl(string discId) =>
        $"{BaseUrl}/discid/{Uri.EscapeDataString(discId)}?inc={Include}&fmt=json";

    /// <summary>
    /// Looks up a table of contents when the exact disc ID is not in MusicBrainz.
    ///
    /// The same pressing is sometimes submitted under a slightly different rip,
    /// and this is the lookup that still finds it. <paramref name="toc"/> is
    /// <c>first+last+leadout+offset+offset...</c> in sectors, the form MusicBrainz documents.
    /// </summary>
    public async Task<IReadOnlyList<ReleaseMatch>> LookupByTocAsync(
        string toc, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toc)) return [];

        return await GetMatchesAsync(TocUrl(toc), discId: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// URL for a TOC lookup. <c>cdstubs=no</c> asks for a real release rather than
    /// a stub. Offsets in <paramref name="toc"/> already include the 150-frame lead-in.
    /// </summary>
    public static string TocUrl(string toc) =>
        $"{BaseUrl}/discid/-?toc={Uri.EscapeDataString(toc)}&cdstubs=no&inc={Include}&fmt=json";

    /// <summary>The 150-frame pregap MusicBrainz counts and a drive LBA does not.</summary>
    public const int PregapFrames = 150;

    /// <summary>Sectors between the last audio track and a following data session.</summary>
    public const int MultiSessionGap = 11400;

    /// <summary>
    /// Lead-out to send to MusicBrainz. A data track on an enhanced CD sits after
    /// a session gap; the audio lead-out is the data track's start minus that gap.
    /// </summary>
    public static long AudioLeadOutLba(TableOfContents toc)
    {
        ArgumentNullException.ThrowIfNull(toc);
        long dataStart = long.MaxValue;
        var hasData = false;
        foreach (var track in toc.Tracks)
        {
            if (track.Type == TrackType.Data && track.StartLba < dataStart)
            {
                dataStart = track.StartLba;
                hasData = true;
            }
        }

        return hasData ? dataStart - MultiSessionGap : toc.LeadOutLba;
    }

    /// <summary>
    /// Builds the <c>toc</c> query from drive LBAs.
    ///
    /// The drive's first track starts at 0. MusicBrainz wants those positions
    /// plus the 150-frame lead-in, which is the same adjustment FoxRedbook makes
    /// when it computes a disc ID. Passing the raw LBAs looks up a different disc.
    /// </summary>
    public static string FormatToc(int firstTrack, int lastTrack, long leadOutLba, IReadOnlyList<long> trackStartLbas)
    {
        var parts = new List<string>
        {
            firstTrack.ToString(CultureInfo.InvariantCulture),
            lastTrack.ToString(CultureInfo.InvariantCulture),
            (leadOutLba + PregapFrames).ToString(CultureInfo.InvariantCulture)
        };
        parts.AddRange(trackStartLbas.Select(o => (o + PregapFrames).ToString(CultureInfo.InvariantCulture)));
        return string.Join('+', parts);
    }

    private async Task<IReadOnlyList<ReleaseMatch>> GetMatchesAsync(
        string url, string? discId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(url, cancellationToken).ConfigureAwait(false);

            // 404 simply means nobody has submitted this pressing. Not an error.
            if (response.StatusCode == HttpStatusCode.NotFound || !response.IsSuccessStatusCode) return [];

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseDiscIdResponse(json, discId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return [];
        }
    }

    /// <summary>One MusicBrainz request at a time, at most one a second, with a single retry on 503.</summary>
    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = _nextRequest - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);

            _nextRequest = DateTimeOffset.UtcNow.AddMilliseconds(1100);
            var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode == 503)
            {
                response.Dispose();
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                _nextRequest = DateTimeOffset.UtcNow.AddMilliseconds(1100);
                response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            }

            return response;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Turns one MusicBrainz release into a <see cref="ReleaseMatch"/>.
    ///
    /// Every medium is kept. The disc in the drive is the medium whose disc list
    /// contains <paramref name="discId"/>; the others stay available so the next
    /// disc can be recognised without another search. Using only the first
    /// medium of a box set is how disc 2 used to receive disc 1's titles.
    /// </summary>
    private static ReleaseMatch? ParseRelease(JsonElement release, string? discId)
    {
        var title = GetString(release, "title");
        if (string.IsNullOrWhiteSpace(title)) return null;

        var artist = JoinArtistCredit(release);

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

        var media = new List<DiscMedium>();
        var matchedNumber = 0;

        if (release.TryGetProperty("media", out var mediaElement) && mediaElement.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var medium in mediaElement.EnumerateArray())
            {
                index++;
                var number = medium.TryGetProperty("position", out var position) && position.TryGetInt32(out var parsed) && parsed > 0
                    ? parsed
                    : index;

                var titles = new List<string>();
                var lengths = new List<int>();
                if (medium.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
                {
                    foreach (var track in tracks.EnumerateArray())
                    {
                        var trackTitle = GetString(track, "title");
                        if (string.IsNullOrWhiteSpace(trackTitle)) continue;
                        titles.Add(trackTitle);
                        lengths.Add(track.TryGetProperty("length", out var length) && length.TryGetInt32(out var ms) ? ms : 0);
                    }
                }

                string? mediumDiscId = null;
                if (medium.TryGetProperty("discs", out var discs) && discs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var disc in discs.EnumerateArray())
                    {
                        var id = GetString(disc, "id");
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        mediumDiscId ??= id;
                        if (!string.IsNullOrEmpty(discId) && string.Equals(id, discId, StringComparison.Ordinal))
                            matchedNumber = number;
                    }
                }

                media.Add(new DiscMedium(number, titles, lengths, mediumDiscId));
            }
        }

        var discCount = media.Count == 0 ? 1 : media.Max(m => m.Number);
        if (matchedNumber == 0) matchedNumber = media.Count > 0 ? media[0].Number : 1;

        var matched = media.FirstOrDefault(m => m.Number == matchedNumber);
        var titlesForMatched = matched?.Titles ?? [];
        // A TOC lookup has no disc id to point at a medium. The caller verifies
        // by length. A disc-id hit is exact only when a medium actually carried it.
        var discIdHit = !string.IsNullOrEmpty(discId) && media.Any(m =>
            string.Equals(m.DiscId, discId, StringComparison.Ordinal));

        string? releaseGroupId = null;
        if (release.TryGetProperty("release-group", out var rg)) releaseGroupId = GetString(rg, "id");

        return new ReleaseMatch(
            Source: "MusicBrainz",
            Title: title,
            Artist: artist,
            Year: year,
            TrackTitles: titlesForMatched.ToList(),
            DiscNumber: discIdHit ? matchedNumber : 0,
            DiscCount: discCount,
            Label: label,
            CatalogNumber: catalogNumber,
            ReleaseId: GetString(release, "id"),
            IsTocVerified: discIdHit)
        {
            ReleaseGroupId = releaseGroupId,
            TracksByDisc = media.OrderBy(m => m.Number).Select(m => m.Titles).ToList(),
            Media = media,
            MatchKind = discIdHit ? DiscMatchKind.DiscId : DiscMatchKind.None
        };
    }

    /// <summary>
    /// Joins an artist-credit with MusicBrainz's own join phrases, so the album
    /// artist is the same string every disc of the release produces.
    /// </summary>
    private static string JoinArtistCredit(JsonElement release)
    {
        if (!release.TryGetProperty("artist-credit", out var credits) || credits.ValueKind != JsonValueKind.Array)
            return "Unknown Artist";

        var sb = new System.Text.StringBuilder();
        foreach (var credit in credits.EnumerateArray())
        {
            var name = GetString(credit, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            sb.Append(name);
            sb.Append(GetString(credit, "joinphrase") ?? "");
        }

        var joined = YouTubeMusicTags.Normalize(sb.ToString());
        return joined.Length == 0 ? "Unknown Artist" : joined;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
