using System.Net.Http.Headers;
using ClaudeSoundtrack.Core.Models;
using FoxRedbook;

namespace ClaudeSoundtrack.Core.Services;

/// <summary>
/// Finds the release that matches the disc currently in the drive.
///
/// Lookup order is MusicBrainz disc ID, then a MusicBrainz TOC lookup, then
/// gnudb, then Discogs, then CD-Text. A disc ID hit is an exact TOC fingerprint.
/// Discogs covers the limited-run soundtrack pressings MusicBrainz lacks, but
/// only through text search, so every Discogs candidate is checked against the
/// disc's own lengths before it is trusted. A shared track count is not a match.
///
/// That check is the important part. An expanded or complete edition shares its
/// title with the original album release, and without a TOC comparison the
/// search happily returns the 12-track 1979 LP for a 3-disc 2012 reissue.
/// </summary>
public sealed class MetadataLookupService : IDisposable
{
    /// <summary>
    /// Discogs rejects requests without a descriptive User-Agent, and asks that
    /// it identify the application and a contact point.
    /// </summary>
    private const string UserAgent =
        "ClaudeSoundtrack/1.0 (+https://github.com/Midcon113/ClaudeSoundtrack)";

    private readonly HttpClient _http;
    private readonly MusicBrainzClient _musicBrainz;
    private readonly DiscogsClient _discogs;
    private readonly GnudbClient _gnudb;

    private readonly bool _ownsHttp;

    public MetadataLookupService()
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, ownsHttp: true)
    {
    }

    /// <summary>Test hook. The client is not disposed by this service when supplied.</summary>
    internal MetadataLookupService(HttpClient http, bool ownsHttp = false)
    {
        _ownsHttp = ownsHttp;
        _http = http;
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        if (!_http.DefaultRequestHeaders.Accept.Any())
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _musicBrainz = new MusicBrainzClient(_http);
        _discogs = new DiscogsClient(_http);
        _gnudb = new GnudbClient(_http);
    }

    /// <summary>The Discogs client, exposed so artwork search can reuse its rate-limited connection.</summary>
    public DiscogsClient Discogs => _discogs;

    /// <summary>
    /// Looks the disc up across every source and returns candidates, best first.
    ///
    /// Verified matches always sort above unverified ones, so the UI's default
    /// selection is the one confirmed against the physical disc.
    /// </summary>
    /// <param name="disc">Disc info read from the drive, including the TOC.</param>
    /// <param name="titleHint">
    /// Optional title to search Discogs with. Without it, only CD-Text and the
    /// disc barcode can seed the search.
    /// </param>
    public async Task<IReadOnlyList<ReleaseMatch>> LookupAsync(
        DiscInfo disc,
        string? titleHint = null,
        string? preferredReleaseId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disc);

        var results = new List<ReleaseMatch>();
        var snapshot = DiscSnapshotFrom(disc);

        // 1. MusicBrainz disc ID: exact by construction when it hits.
        if (!string.IsNullOrWhiteSpace(disc.MusicBrainzDiscId))
        {
            var byDiscId = await _musicBrainz
                .LookupByDiscIdAsync(disc.MusicBrainzDiscId, cancellationToken)
                .ConfigureAwait(false);

            results.AddRange(byDiscId.Select(r => ApplyToc(r, snapshot)));
        }

        // 2. MusicBrainz TOC lookup, when the exact id was never submitted.
        if (!results.Any(r => r.MatchKind == DiscMatchKind.DiscId) && disc.Toc is { } toc)
        {
            var audioStarts = toc.Tracks
                .Where(t => t.Type != TrackType.Data)
                .OrderBy(t => t.Number)
                .Select(t => t.StartLba)
                .ToList();
            if (audioStarts.Count > 0)
            {
                var query = MusicBrainzClient.FormatToc(
                    toc.FirstTrackNumber, toc.LastTrackNumber, toc.LeadOutLba, audioStarts);
                var byToc = await _musicBrainz.LookupByTocAsync(query, cancellationToken).ConfigureAwait(false);
                results.AddRange(byToc.Select(r => ApplyToc(r, snapshot)));
            }
        }

        // 3. gnudb, only when MusicBrainz has nothing verified. It knows one disc.
        if (!results.Any(r => r.IsTocVerified) && disc.Toc is { } freedbToc && disc.FreedbDiscId != 0)
        {
            var offsets = freedbToc.Tracks
                .Where(t => t.Type != TrackType.Data)
                .OrderBy(t => t.Number)
                .Select(t => (int)(t.StartLba + 150))
                .ToList();
            var seconds = (int)((freedbToc.LeadOutLba + 150) / 75);
            var gnudb = await _gnudb.LookupAsync(disc.FreedbDiscId, offsets, seconds, cancellationToken)
                .ConfigureAwait(false);
            if (gnudb is not null) results.Add(ApplyToc(gnudb, snapshot));
        }

        // 4. Discogs, seeded from whatever identifying text we have. The barcode
        //    is the strongest seed because it identifies the exact pressing.
        var barcode = disc.CdText?.UpcEan;
        var searchTitle = titleHint;
        if (string.IsNullOrWhiteSpace(searchTitle)) searchTitle = disc.CdText?.AlbumTitle;

        if (!string.IsNullOrWhiteSpace(barcode) || !string.IsNullOrWhiteSpace(searchTitle))
        {
            var discogsResults = await _discogs
                .SearchAsync(searchTitle, catalogNumber: null, barcode: barcode, cancellationToken)
                .ConfigureAwait(false);

            foreach (var candidate in discogsResults)
                results.Add(ApplyToc(candidate, snapshot));
        }

        // 5. CD-Text, as a last resort. Rare on commercial discs, but when it is
        //    present it came off this exact disc, so its track count is right.
        var cdTextMatch = BuildFromCdText(disc);
        if (cdTextMatch is not null) results.Add(ApplyToc(cdTextMatch, snapshot));

        return results
            .OrderByDescending(r => !string.IsNullOrEmpty(preferredReleaseId) && r.ReleaseId == preferredReleaseId)
            .ThenByDescending(r => r.MatchKind == DiscMatchKind.DiscId)
            .ThenByDescending(r => r.MatchKind == DiscMatchKind.Duration)
            .ThenByDescending(r => r.IsTocVerified)
            .ThenByDescending(r => r.DiscCount)
            .ThenByDescending(r => r.Year is > 0)
            .ToList();
    }

    /// <summary>The audio TOC as a snapshot the disc identifier can compare.</summary>
    public static DiscSnapshot DiscSnapshotFrom(DiscInfo disc)
    {
        var sectors = disc.Toc?.Tracks
            .Where(t => t.Type != TrackType.Data)
            .OrderBy(t => t.Number)
            .Select(t => t.SectorCount)
            .ToList() ?? [];
        return new DiscSnapshot(disc.MusicBrainzDiscId, sectors);
    }

    /// <summary>
    /// Searches Discogs directly, for when the user types a title themselves
    /// because automatic lookup found nothing.
    /// </summary>
    public async Task<IReadOnlyList<ReleaseMatch>> SearchAsync(
        string query,
        int discTrackCount,
        CancellationToken cancellationToken = default)
    {
        var results = await _discogs.SearchAsync(query, cancellationToken: cancellationToken).ConfigureAwait(false);
        var snapshot = new DiscSnapshot(null, Enumerable.Repeat(1, Math.Max(discTrackCount, 0)).ToList());
        // A manual search has no sector lengths. Track count is all there is,
        // and an ambiguous count stays unverified rather than picking disc 1.
        if (discTrackCount <= 0)
        {
            return results;
        }

        return results
            .Select(r => ApplyToc(r, snapshot))
            .OrderByDescending(r => r.IsTocVerified)
            .ToList();
    }

    /// <summary>
    /// Records which disc of <paramref name="candidate"/> is in the drive.
    ///
    /// A MusicBrainz disc-id hit is already exact. Everything else is compared
    /// by length, then by a unique track count. Two discs with the same count
    /// and no lengths stay unverified: that used to be reported as disc 1.
    /// </summary>
    internal static ReleaseMatch ApplyToc(ReleaseMatch candidate, DiscSnapshot snapshot)
    {
        if (candidate.MatchKind == DiscMatchKind.DiscId && candidate.IsTocVerified)
            return candidate;

        var media = candidate.Media;
        if (media.Count == 0 && candidate.TracksByDisc.Count > 0)
        {
            media = candidate.TracksByDisc
                .Select((titles, i) => new DiscMedium(i + 1, titles, Enumerable.Repeat(0, titles.Count).ToList(), null))
                .ToList();
        }
        else if (media.Count == 0 && candidate.TrackTitles.Count > 0)
        {
            media = [new DiscMedium(Math.Max(candidate.DiscNumber, 1), candidate.TrackTitles,
                Enumerable.Repeat(0, candidate.TrackTitles.Count).ToList(), null)];
        }

        // A manual search snapshot uses a dummy sector of 1 per track, which
        // must not be compared with real durations. Track count still works.
        var id = snapshot.SectorCounts.All(s => s <= 1)
            ? IdentifyByCountOnly(snapshot.TrackCount, media)
            : DiscIdentifier.Identify(snapshot, media);

        if (!id.IsConfident || id.DiscNumber is not int number)
        {
            return candidate with
            {
                IsTocVerified = false,
                DiscNumber = 0,
                MatchKind = id.Kind,
                Media = media
            };
        }

        return candidate with
        {
            IsTocVerified = true,
            DiscNumber = number,
            DiscCount = Math.Max(candidate.DiscCount, id.DiscCount),
            MatchKind = id.Kind,
            Media = media
        };
    }

    /// <summary>Unique track count only. Shared counts are ambiguous on purpose.</summary>
    private static DiscIdentification IdentifyByCountOnly(int trackCount, IReadOnlyList<DiscMedium> media)
    {
        if (trackCount <= 0 || media.Count == 0) return DiscIdentification.Unknown;
        var hits = media.Where(m => m.Titles.Count == trackCount).ToList();
        if (hits.Count == 1)
        {
            return new DiscIdentification(hits[0].Number, media.Max(m => m.Number), DiscMatchKind.TrackCount,
                $"Only disc {hits[0].Number} has {trackCount} tracks.");
        }

        if (hits.Count > 1)
        {
            return new DiscIdentification(null, media.Max(m => m.Number), DiscMatchKind.Ambiguous,
                "More than one disc has this many tracks.");
        }

        return new DiscIdentification(null, media.Max(m => m.Number), DiscMatchKind.None, "Track count does not match.");
    }

    /// <summary>
    /// Builds a match from CD-Text burned onto the disc, when it has any.
    /// </summary>
    private static ReleaseMatch? BuildFromCdText(DiscInfo disc)
    {
        var cdText = disc.CdText;
        if (cdText is null) return null;
        if (string.IsNullOrWhiteSpace(cdText.AlbumTitle)) return null;

        var titles = cdText.Tracks?
            .OrderBy(t => t.Number)
            .Select(t => t.Title)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .ToList() ?? [];

        if (titles.Count == 0) return null;

        return new ReleaseMatch(
            Source: "CD-Text",
            Title: cdText.AlbumTitle,
            Artist: string.IsNullOrWhiteSpace(cdText.AlbumPerformer) ? "Unknown Artist" : cdText.AlbumPerformer,
            Year: null,
            TrackTitles: titles,
            DiscNumber: 1,
            DiscCount: 1,
            Label: null,
            CatalogNumber: null,
            ReleaseId: null,
            // The text is physically on this disc, so it describes this disc.
            IsTocVerified: titles.Count == CountAudioTracks(disc.Toc));
    }

    /// <summary>
    /// Counts audio tracks, ignoring the data track on an enhanced CD.
    ///
    /// Counting the data track would put every candidate one track out and make
    /// verification fail on exactly the CD-Extra discs it is needed for.
    /// </summary>
    public static int CountAudioTracks(TableOfContents? toc)
    {
        if (toc?.Tracks is null) return 0;
        return toc.Tracks.Count(t => t.Type != TrackType.Data);
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
