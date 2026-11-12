using System.Globalization;
using System.Text;
using ClaudeSoundtrack.Core.Models;

namespace ClaudeSoundtrack.Core.Services;

/// <summary>
/// Looks a disc up in gnudb, the public successor of freedb.
///
/// Used only when MusicBrainz has no disc ID and no TOC hit. gnudb describes
/// one disc, not a box set, so the result is a single-disc match the user still
/// has to confirm. The protocol is the old CDDB HTTP gateway.
/// </summary>
public sealed class GnudbClient
{
    private const string Endpoint = "https://gnudb.gnudb.org/~cddb/cddb.cgi";

    /// <summary>CDDB requires a hello of user, host, client, version, with spaces as pluses.</summary>
    private const string Hello = "hello=mark+localhost+ClaudeSoundtrack+1.0&proto=6";

    private readonly HttpClient _http;

    public GnudbClient(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// Queries gnudb with a freedb disc id and the CDDB frame offsets (LBA + 150).
    /// </summary>
    public async Task<ReleaseMatch?> LookupAsync(
        uint freedbId,
        IReadOnlyList<int> frameOffsets,
        int lengthSeconds,
        CancellationToken cancellationToken = default)
    {
        if (frameOffsets.Count == 0 || lengthSeconds <= 0) return null;

        try
        {
            var query = BuildQuery(freedbId, frameOffsets, lengthSeconds);
            using var queryResponse = await _http.GetAsync(query, cancellationToken).ConfigureAwait(false);
            if (!queryResponse.IsSuccessStatusCode) return null;
            var queryBody = await queryResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var hit = ParseQuery(queryBody);
            if (hit is null) return null;

            var read = BuildRead(hit.Value.Genre, hit.Value.DiscId);
            using var readResponse = await _http.GetAsync(read, cancellationToken).ConfigureAwait(false);
            if (!readResponse.IsSuccessStatusCode) return null;
            var readBody = await readResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseRead(readBody);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    public static string BuildQuery(uint freedbId, IReadOnlyList<int> frameOffsets, int lengthSeconds)
    {
        var id = freedbId.ToString("x8", CultureInfo.InvariantCulture);
        var offsets = string.Join('+', frameOffsets.Select(o => o.ToString(CultureInfo.InvariantCulture)));
        var command = $"cddb+query+{id}+{frameOffsets.Count}+{offsets}+{lengthSeconds.ToString(CultureInfo.InvariantCulture)}";
        return $"{Endpoint}?cmd={command}&{Hello}";
    }

    public static string BuildRead(string genre, string discId)
    {
        var command = $"cddb+read+{Uri.EscapeDataString(genre)}+{Uri.EscapeDataString(discId)}";
        return $"{Endpoint}?cmd={command}&{Hello}";
    }

    /// <summary>
    /// The first exact or inexact hit. 202 means none. The body is the CDDB
    /// status line, then optionally more hits, then a terminating period.
    /// </summary>
    public static (string Genre, string DiscId)? ParseQuery(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var lines = body.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;

        var status = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // 200 genre discid Title...
        if (status.Length >= 3 && status[0] is "200" or "210" or "211")
        {
            if (status[0] == "200") return (status[1], status[2]);
            // 210/211: the hits follow on their own lines.
            if (lines.Length >= 2)
            {
                var hit = lines[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (hit.Length >= 2) return (hit[0], hit[1]);
            }
        }

        return null;
    }

    /// <summary>
    /// Reads a CDDB record into a single-disc match. DTITLE is "Artist / Album".
    /// </summary>
    public static ReleaseMatch? ParseRead(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        string? dtitle = null;
        int? year = null;
        var titles = new SortedDictionary<int, string>();

        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line == ".") continue;
            var split = line.IndexOf('=');
            if (split <= 0) continue;
            var key = line[..split];
            var value = Decode(line[(split + 1)..]);

            if (key.Equals("DTITLE", StringComparison.OrdinalIgnoreCase))
                dtitle = (dtitle ?? "") + value;
            else if (key.Equals("DYEAR", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var y) && y > 0)
                year = y;
            else if (key.StartsWith("TTITLE", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(key.AsSpan(6), out var index))
                titles[index] = titles.TryGetValue(index, out var existing) ? existing + value : value;
        }

        if (titles.Count == 0 || string.IsNullOrWhiteSpace(dtitle)) return null;

        var artist = "Unknown Artist";
        var album = dtitle.Trim();
        var slash = dtitle.IndexOf(" / ", StringComparison.Ordinal);
        if (slash > 0)
        {
            artist = YouTubeMusicTags.Normalize(dtitle[..slash]);
            album = dtitle[(slash + 3)..].Trim();
        }

        if (string.IsNullOrWhiteSpace(album)) return null;
        var ordered = titles.OrderBy(p => p.Key).Select(p => p.Value.Trim()).Where(t => t.Length > 0).ToList();
        if (ordered.Count == 0) return null;

        return new ReleaseMatch(
            Source: "gnudb",
            Title: YouTubeMusicTags.Normalize(album),
            Artist: artist,
            Year: year,
            TrackTitles: ordered,
            DiscNumber: 1,
            DiscCount: 1,
            Label: null,
            CatalogNumber: null,
            ReleaseId: null,
            // The disc id found the record. Track-count verification still runs,
            // so a bad submission cannot be marked verified against this disc.
            IsTocVerified: false)
        {
            Media = [new DiscMedium(1, ordered, Enumerable.Repeat(0, ordered.Count).ToList(), DiscId: null)]
        };
    }

    /// <summary>CDDB encodes non-ASCII as backslash-octal or just leaves it. Keep it simple.</summary>
    private static string Decode(string value)
    {
        if (!value.Contains('\\')) return value.Trim();
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length && value[i + 1] == 'n')
            {
                i++;
                continue;
            }

            sb.Append(value[i]);
        }

        return sb.ToString().Trim();
    }
}
