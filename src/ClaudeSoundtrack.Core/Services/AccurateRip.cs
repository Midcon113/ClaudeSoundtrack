using System.Buffers.Binary;
using System.Globalization;
using System.Net;

namespace ClaudeSoundtrack.Core.Services;

/// <summary>
/// AccurateRip v1 and v2 checksums, the database URL, and the binary response.
///
/// The checksum is the one FoxRedbook computes and describes as checked against
/// the AccurateRip database: a 1-based sample counter, the first 2939 samples of
/// track 1 and the last 2940 samples of the last track left out of the sum, v1
/// the low half of each 64-bit product, v2 the sum of both halves. It is
/// track-relative. A test compares it with FoxRedbook's own routine so the two
/// cannot drift.
///
/// Pressings that are not in the database — most limited soundtrack runs — get
/// no checksum hit. The ripper then reads the disc a second time instead.
/// </summary>
public static class AccurateRipChecksum
{
    /// <summary>Five CD sectors of stereo frames. The last-track skip.</summary>
    public const int EdgeSamples = 5 * 588;

    public static (uint V1, uint V2) Compute(ReadOnlySpan<byte> pcm, int trackNumber, int totalTracks)
    {
        var state = new State(trackNumber, totalTracks, pcm.Length / 4);
        state.Update(pcm);
        return (state.V1, state.V2);
    }

    /// <summary>Running checksum, so a track can be hashed as it is read.</summary>
    public sealed class State
    {
        private uint _low;
        private uint _high;
        private uint _multiplier;
        private readonly uint _from;
        private readonly uint _to;

        public State(int trackNumber, int totalTracks, int sampleCount)
        {
            // The classic comparison is multiplier >= 2940, and the multiplier
            // starts at 1, so track 1 drops 2939 samples rather than 2940.
            _from = trackNumber == 1 ? (uint)EdgeSamples : 0;
            _to = trackNumber == totalTracks && sampleCount > EdgeSamples
                ? (uint)(sampleCount - EdgeSamples)
                : (uint)Math.Max(sampleCount, 0);
            _multiplier = 1;
        }

        public uint V1 => _low;
        public uint V2 => _low + _high;

        public void Update(ReadOnlySpan<byte> pcm)
        {
            var samples = pcm.Length / 4;
            for (var i = 0; i < samples; i++)
            {
                if (_multiplier >= _from && _multiplier <= _to)
                {
                    var sample = BinaryPrimitives.ReadUInt32LittleEndian(pcm.Slice(i * 4, 4));
                    var product = (ulong)sample * _multiplier;
                    _low += (uint)product;
                    _high += (uint)(product >> 32);
                }

                _multiplier++;
            }
        }
    }
}

/// <summary>One pressing's AccurateRip record for a disc.</summary>
public sealed record AccurateRipPressing(
    int TrackCount,
    uint DiscId1,
    uint DiscId2,
    uint CddbId,
    IReadOnlyList<byte> Confidence,
    IReadOnlyList<uint> CrcV1,
    IReadOnlyList<uint> CrcV2);

/// <summary>Builds the AccurateRip database path and parses the body it returns.</summary>
public static class AccurateRipDatabase
{
    public const string Host = "http://www.accuraterip.com/accuraterip/";

    /// <summary>
    /// Path under the AccurateRip host. The three directories are the lowest
    /// three nibbles of disc id 1, reversed.
    /// </summary>
    public static string RelativePath(uint id1, uint id2, uint cddbId, int trackCount)
    {
        var hex = id1.ToString("x8", CultureInfo.InvariantCulture);
        var directories = $"{hex[^1]}/{hex[^2]}/{hex[^3]}";
        var file = string.Create(CultureInfo.InvariantCulture,
            $"dBAR-{trackCount:D3}-{id1:x8}-{id2:x8}-{cddbId:x8}.bin");
        return directories + "/" + file;
    }

    public static string Url(uint id1, uint id2, uint cddbId, int trackCount) =>
        Host + RelativePath(id1, id2, cddbId, trackCount);

    /// <summary>
    /// Splits a database body into pressings. Each record is a track count, the
    /// three ids, then nine bytes per track: confidence, v1 CRC, v2 CRC.
    /// </summary>
    public static IReadOnlyList<AccurateRipPressing> Parse(ReadOnlySpan<byte> body)
    {
        var results = new List<AccurateRipPressing>();
        var offset = 0;
        while (offset < body.Length)
        {
            var tracks = body[offset];
            var size = 1 + 12 + tracks * 9;
            if (tracks == 0 || offset + size > body.Length) break;

            var id1 = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(offset + 1, 4));
            var id2 = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(offset + 5, 4));
            var cddb = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(offset + 9, 4));
            var confidence = new byte[tracks];
            var v1 = new uint[tracks];
            var v2 = new uint[tracks];
            var cursor = offset + 13;
            for (var i = 0; i < tracks; i++)
            {
                confidence[i] = body[cursor];
                v1[i] = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor + 1, 4));
                v2[i] = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor + 5, 4));
                cursor += 9;
            }

            results.Add(new AccurateRipPressing(tracks, id1, id2, cddb, confidence, v1, v2));
            offset += size;
        }

        return results;
    }

    /// <summary>
    /// Finds the pressing where every supplied checksum hits either v1 or v2.
    /// Returns null when the disc is absent or the audio does not agree.
    /// </summary>
    public static AccurateRipPressing? BestMatch(
        IReadOnlyList<AccurateRipPressing> pressings,
        IReadOnlyList<(uint V1, uint V2)> checksums)
    {
        AccurateRipPressing? best = null;
        var bestConfidence = -1;

        foreach (var pressing in pressings)
        {
            if (pressing.TrackCount != checksums.Count) continue;
            var ok = true;
            var confidence = 0;
            for (var i = 0; i < checksums.Count; i++)
            {
                var hit = checksums[i].V1 == pressing.CrcV1[i] || checksums[i].V2 == pressing.CrcV2[i];
                if (!hit)
                {
                    ok = false;
                    break;
                }

                confidence += pressing.Confidence[i];
            }

            if (ok && confidence > bestConfidence)
            {
                best = pressing;
                bestConfidence = confidence;
            }
        }

        return best;
    }

    /// <summary>
    /// Downloads the record for these ids. A 404 is an empty list — the pressing
    /// has simply never been submitted. Any other failure is also an empty list;
    /// the caller falls back to a second read.
    /// </summary>
    public static async Task<IReadOnlyList<AccurateRipPressing>> LookupAsync(
        HttpClient http, uint id1, uint id2, uint cddbId, int trackCount, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(Url(id1, id2, cddbId, trackCount), cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return [];
            if (!response.IsSuccessStatusCode) return [];
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return Parse(bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return [];
        }
    }
}
