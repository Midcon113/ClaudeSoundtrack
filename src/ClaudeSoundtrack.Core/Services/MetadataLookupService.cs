using System.Net.Http.Headers;
using FoxRedbook;

namespace ClaudeSoundtrack.Core.Services;

/// <summary>
/// Finds the release that matches the disc currently in the drive.
///
/// Lookup order is MusicBrainz disc ID, then Discogs, then CD-Text off the disc
/// itself. The order is deliberate: a disc ID hit is an exact TOC fingerprint,
/// so when it lands it needs no further checking. Discogs covers the limited-run
/// soundtrack pressings MusicBrainz lacks, but only through text search, so
/// every Discogs candidate is checked against the disc's own TOC before it is
/// offered as verified.
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

    public MetadataLookupService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _musicBrainz = new MusicBrainzClient(_http);
        _discogs = new DiscogsClient(_http);
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disc);

        var results = new List<ReleaseMatch>();
        var trackCount = CountAudioTracks(disc.Toc);

        // 1. MusicBrainz disc ID: exact by construction when it hits.
        if (!string.IsNullOrWhiteSpace(disc.MusicBrainzDiscId))
        {
            var byDiscId = await _musicBrainz
                .LookupByDiscIdAsync(disc.MusicBrainzDiscId, cancellationToken)
                .ConfigureAwait(false);

            results.AddRange(byDiscId);
        }

        // 2. Discogs, seeded from whatever identifying text we have. The barcode
        //    is the strongest seed because it identifies the exact pressing.
        var barcode = disc.CdText?.UpcEan;
        var searchTitle = titleHint;
        if (string.IsNullOrWhiteSpace(searchTitle)) searchTitle = disc.CdText?.AlbumTitle;

        if (!string.IsNullOrWhiteSpace(barcode) || !string.IsNullOrWhiteSpace(searchTitle))
        {
            var discogsResults = await _discogs
                .SearchAsync(searchTitle, catalogNumber: null, barcode: barcode, cancellationToken)
                .ConfigureAwait(false);

            // Verify each candidate against the physical disc before trusting it.
            foreach (var candidate in discogsResults)
            {
                results.Add(VerifyAgainstToc(candidate, trackCount));
            }
        }

        // 3. CD-Text, as a last resort. Rare on commercial discs, but when it is
        //    present it came off this exact disc, so its track count is right.
        var cdTextMatch = BuildFromCdText(disc);
        if (cdTextMatch is not null) results.Add(cdTextMatch);

        return results
            .OrderByDescending(r => r.IsTocVerified)
            // Prefer the release whose disc count explains the set the user has.
            .ThenByDescending(r => r.TitlesForDisc(r.DiscNumber).Count == trackCount)
            .ThenByDescending(r => r.Year is > 0)
            .ToList();
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
        return results
            .Select(r => VerifyAgainstToc(r, discTrackCount))
            .OrderByDescending(r => r.IsTocVerified)
            .ToList();
    }

    /// <summary>
    /// Marks a candidate verified when one of its discs has exactly as many
    /// tracks as the disc in the drive.
    ///
    /// Track count is a coarse fingerprint but a decisive one here: the failure
    /// this guards against is an expanded edition matching its original release,
    /// and those differ in track count by a wide margin. When a candidate has a
    /// disc that fits, the match also records which disc it was, so a 3-disc set
    /// tags disc 2 with disc 2's titles.
    /// </summary>
    private static ReleaseMatch VerifyAgainstToc(ReleaseMatch candidate, int discTrackCount)
    {
        if (discTrackCount <= 0) return candidate;

        if (candidate.TracksByDisc.Count > 0)
        {
            for (var i = 0; i < candidate.TracksByDisc.Count; i++)
            {
                if (candidate.TracksByDisc[i].Count == discTrackCount)
                {
                    return candidate with { DiscNumber = i + 1, IsTocVerified = true };
                }
            }

            return candidate;
        }

        return candidate.TrackTitles.Count == discTrackCount
            ? candidate with { IsTocVerified = true }
            : candidate;
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

    public void Dispose() => _http.Dispose();
}
