using System.Net.Http.Headers;
using System.Security.Cryptography;
using ClaudeSoundtrack.Core.Models;
using CUETools.Codecs;
using CUETools.Codecs.FLAKE;
using FoxRedbook;

namespace ClaudeSoundtrack.Core.Services;

/// <summary>Progress for a single track being ripped.</summary>
/// <param name="TrackNumber">Track number on the source disc.</param>
/// <param name="TrackTitle">Title being written, for display.</param>
/// <param name="TrackIndex">Zero-based position within this disc's rip queue.</param>
/// <param name="TrackCount">How many tracks this disc's rip will produce.</param>
/// <param name="TrackPercent">Completion of the current track, 0-100.</param>
/// <param name="HadErrors">True once any sector in this track could not be read.</param>
/// <param name="IsComplete">True only on the single report that finishes a track.</param>
public readonly record struct RipTrackProgress(
    int TrackNumber,
    string TrackTitle,
    int TrackIndex,
    int TrackCount,
    double TrackPercent,
    bool HadErrors,
    bool IsComplete)
{
    /// <summary>Completion across the whole disc, 0-100.</summary>
    public double OverallPercent =>
        TrackCount <= 0 ? 0 : (TrackIndex * 100.0 + TrackPercent) / TrackCount;
}

/// <summary>
/// Rips audio tracks off a CD and encodes them straight to FLAC.
///
/// Audio never touches an intermediate WAV: sectors are read into a small buffer
/// and pushed directly into the FLAC encoder. A 74-minute disc would otherwise
/// cost ~750MB of scratch space per disc for no benefit.
///
/// Reads go through <see cref="IOpticalDrive.ReadSectorsAsync"/> rather than
/// FoxRedbook's <c>RipSession</c>. That is deliberate and was arrived at the hard
/// way - see the note on <see cref="RipTracksAsync"/>.
/// </summary>
public sealed class CdRipService
{
    /// <summary>
    /// When true, checksums are looked up in the AccurateRip database and any
    /// track the database does not confirm is read a second time. Tests turn
    /// this off so they do not touch the network.
    /// </summary>
    public bool QueryAccurateRip { get; set; } = true;

    /// <summary>A CD sector holds 2352 bytes = 588 stereo 16-bit sample frames.</summary>
    private const int BytesPerSector = 2352;

    /// <summary>
    /// Sectors per read request. 27 x 2352 = 63,504 bytes, which stays under the
    /// 64KB transfer limit that many drives impose on a single SCSI command.
    /// </summary>
    private const int SectorsPerRead = 27;

    /// <summary>How many times to re-attempt a chunk before narrowing the failure down.</summary>
    private const int MaxChunkRetries = 3;

    /// <summary>Red Book audio is always 16-bit, stereo, 44.1kHz.</summary>
    private static AudioPCMConfig RedBookPcm => new(16, 2, 44100);

    /// <summary>
    /// Rips the requested tracks from the disc in <paramref name="devicePath"/>,
    /// writing one FLAC per track into <paramref name="outputFolder"/>.
    ///
    /// **Why this does not use FoxRedbook's RipSession.** RipSession is the
    /// library's headline API and applies jitter correction and re-reads, but in
    /// 1.0.0-alpha.3 it has two defects that make it unusable here, both
    /// reproduced against a real 28-track disc:
    ///
    ///   1. A session yields correct audio for the first track and then silence
    ///      for every track after it. Nothing reports an error - the sectors
    ///      arrive full of zeros, the durations are right, and the result is a
    ///      complete album of silent FLAC files that looks valid until played.
    ///   2. Even with a fresh session and drive handle per track, some tracks
    ///      throw IndexOutOfRangeException inside WiggleEngine.TryMergeFragment.
    ///
    /// Reading sectors directly returns byte-identical audio to RipSession on the
    /// tracks where RipSession works, so nothing is lost in fidelity. Offset
    /// correction is preserved by wrapping the drive in
    /// <see cref="OffsetCorrectingDrive"/>; damaged-sector recovery is handled by
    /// <see cref="ReadChunkAsync"/> instead of by WiggleEngine.
    ///
    /// Files are written under a temporary per-track name and only moved into
    /// place once the track completes, so an aborted rip never leaves a truncated
    /// FLAC that looks like a finished one.
    /// </summary>
    /// <param name="devicePath">Drive to read, e.g. "D:".</param>
    /// <param name="tracks">
    /// Tracks to rip. <see cref="SoundtrackTrack.SourceTrackNumber"/> selects the
    /// disc track; <see cref="SoundtrackTrack.FilePath"/> is set on success.
    /// </param>
    /// <param name="outputFolder">Existing folder to write FLAC files into.</param>
    /// <param name="fileNames">File name for each track, positionally matched to <paramref name="tracks"/>.</param>
    /// <param name="progress">Receives per-track progress.</param>
    public async Task RipTracksAsync(
        string devicePath,
        IReadOnlyList<SoundtrackTrack> tracks,
        string outputFolder,
        IReadOnlyList<string> fileNames,
        IProgress<RipTrackProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(fileNames);

        if (fileNames.Count != tracks.Count)
            throw new ArgumentException("A file name must be supplied for every track.", nameof(fileNames));

        Directory.CreateDirectory(outputFolder);

        var raw = OpticalDrive.Open(devicePath);
        IOpticalDrive drive = raw;
        IDisposable? wrapper = null;

        try
        {
            // Apply the drive's read offset when the database knows this model.
            // Without it every track is shifted by a few hundred samples, which
            // is inaudible but makes the rip fail checksum verification.
            var offset = LookupOffset(raw);
            if (offset is not null and not 0)
            {
                var corrected = new OffsetCorrectingDrive(raw, offset.Value);
                wrapper = corrected;
                drive = corrected;
            }

            await RipTracksAsync(drive, tracks, outputFolder, fileNames, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // Dispose only the outermost handle; the wrapper owns the inner drive.
            if (wrapper is not null) wrapper.Dispose();
            else if (raw is IDisposable disposable) disposable.Dispose();
        }
    }

    /// <summary>
    /// Rips <paramref name="tracks"/> from an already-open drive. Tests pass a
    /// stand-in here; the app uses the <see cref="string"/> overload, which also
    /// applies the drive's read offset.
    /// </summary>
    public async Task RipTracksAsync(
        IOpticalDrive drive,
        IReadOnlyList<SoundtrackTrack> tracks,
        string outputFolder,
        IReadOnlyList<string> fileNames,
        IProgress<RipTrackProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(fileNames);
        if (fileNames.Count != tracks.Count)
            throw new ArgumentException("A file name must be supplied for every track.", nameof(fileNames));

        Directory.CreateDirectory(outputFolder);

        var toc = await drive.ReadTocAsync(cancellationToken).ConfigureAwait(false);
            var audioTracks = toc.Tracks.Where(t => t.Type != TrackType.Data).OrderBy(t => t.Number).ToList();
            var fingerprints = new List<(SoundtrackTrack Track, TrackInfo Toc, byte[] Sha256)>();

            for (var i = 0; i < tracks.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var track = tracks[i];
                var tocTrack = toc.Tracks.FirstOrDefault(t => t.Number == track.SourceTrackNumber);

                if (tocTrack.Number != track.SourceTrackNumber)
                    throw new InvalidOperationException(
                        $"Track {track.SourceTrackNumber} is not present on the disc.");

                // Data tracks (CD-Extra, enhanced CDs) are not audio and must be skipped.
                if (tocTrack.Type == TrackType.Data) continue;

                var destination = Path.Combine(outputFolder, fileNames[i]);
                var sha = await RipSingleTrackAsync(
                        drive, tocTrack, track, destination, i, tracks.Count, audioTracks.Count,
                        progress, cancellationToken)
                    .ConfigureAwait(false);
                fingerprints.Add((track, tocTrack, sha));
            }

            await ConfirmReadsAsync(drive, toc, audioTracks.Count, fingerprints, progress, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Looks the drive's read offset up in FoxRedbook's bundled database.
    /// An unknown drive simply gets no correction rather than a guess.
    /// </summary>
    private static int? LookupOffset(IOpticalDrive drive)
    {
        try
        {
            return KnownDriveOffsets.Lookup(drive.Inquiry);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Rips one track: read sectors in chunks, encode each chunk as it arrives.</summary>
    /// <returns>SHA-256 of the audio bytes that were encoded, for the second read.</returns>
    private static async Task<byte[]> RipSingleTrackAsync(
        IOpticalDrive drive,
        TrackInfo tocTrack,
        SoundtrackTrack track,
        string destination,
        int trackIndex,
        int trackCount,
        int audioTrackCount,
        IProgress<RipTrackProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Write to a temp name so a cancelled rip cannot be mistaken for a good one.
        var tempPath = destination + ".partial";
        if (File.Exists(tempPath)) File.Delete(tempPath);

        var pcm = RedBookPcm;
        var totalSectors = tocTrack.SectorCount;
        var hadErrors = false;
        long nonZeroBytes = 0;
        var checksum = new AccurateRipChecksum.State(tocTrack.Number, audioTrackCount, totalSectors * CdConstants.SampleFramesPerSector);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        FlakeWriter? writer = null;
        try
        {
            writer = new FlakeWriter(tempPath, pcm)
            {
                // Telling the encoder the length up front lets it write an accurate
                // STREAMINFO header instead of having to seek back and patch it.
                FinalSampleCount = (long)totalSectors * CdConstants.SampleFramesPerSector,
                CompressionLevel = 8
            };

            var buffer = new byte[SectorsPerRead * BytesPerSector];
            long sectorsDone = 0;
            var lastReported = -1.0;

            while (sectorsDone < totalSectors)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var want = (int)Math.Min(SectorsPerRead, totalSectors - sectorsDone);
                var result = await ReadChunkAsync(
                        drive, tocTrack.StartLba + sectorsDone, want, buffer, cancellationToken)
                    .ConfigureAwait(false);

                if (result.HadError) hadErrors = true;

                // Count real audio so a silent rip can be detected rather than
                // shipped. This is cheap next to the cost of the read itself.
                var byteCount = want * BytesPerSector;
                checksum.Update(buffer.AsSpan(0, byteCount));
                hash.AppendData(buffer, 0, byteCount);
                for (var i = 0; i < byteCount; i++)
                {
                    if (buffer[i] != 0) nonZeroBytes++;
                }

                writer.Write(new AudioBuffer(pcm, buffer, want * CdConstants.SampleFramesPerSector));
                sectorsDone += want;

                // Reporting on every chunk would flood the UI thread; a disc is
                // ~350,000 sectors. Report on whole-percent changes only.
                var percent = totalSectors > 0 ? sectorsDone * 100.0 / totalSectors : 100;
                if (progress is not null && Math.Floor(percent) > lastReported)
                {
                    lastReported = Math.Floor(percent);
                    progress.Report(new RipTrackProgress(
                        track.SourceTrackNumber, track.Title, trackIndex, trackCount,
                        Math.Min(percent, 99.9), hadErrors, IsComplete: false));
                }
            }

            writer.Close();
            writer = null;

            if (File.Exists(destination)) File.Delete(destination);
            File.Move(tempPath, destination);

            track.FilePath = destination;
            track.IsRipped = true;
            track.HadReadErrors = hadErrors;
            track.Duration = TimeSpan.FromSeconds((double)totalSectors / CdConstants.SectorsPerSecond);
            track.IsSilent = nonZeroBytes == 0 && totalSectors > 0;
            track.AccurateRipCrc = checksum.V1;
            track.AccurateRipV2Crc = checksum.V2;
            track.CheckedAgainstDisc = true;

            // Exactly one completion report per track, so callers can log on it
            // without having to de-duplicate.
            progress?.Report(new RipTrackProgress(
                track.SourceTrackNumber, track.Title, trackIndex, trackCount, 100, hadErrors, IsComplete: true));

            return hash.GetHashAndReset();
        }
        catch
        {
            // Delete() discards the encoder's own output; the temp file must go too,
            // so a retry does not trip over a stale partial.
            try { writer?.Delete(); } catch { /* already gone */ }
            writer = null;
            if (File.Exists(tempPath)) { try { File.Delete(tempPath); } catch { /* locked */ } }
            throw;
        }
        finally
        {
            writer?.Close();
        }
    }

    /// <summary>
    /// Confirms each track. An AccurateRip hit is enough. Anything else is read
    /// again and compared with the first read's SHA-256. A limited soundtrack
    /// pressing is usually absent from AccurateRip, so the second read is the
    /// check that actually runs.
    /// </summary>
    private async Task ConfirmReadsAsync(
        IOpticalDrive drive,
        TableOfContents toc,
        int audioTrackCount,
        List<(SoundtrackTrack Track, TrackInfo Toc, byte[] Sha256)> fingerprints,
        IProgress<RipTrackProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (fingerprints.Count == 0) return;

        AccurateRipPressing? pressing = null;
        var databaseKnowsDisc = false;
        if (QueryAccurateRip)
        {
            try
            {
                var ids = DiscFingerprint.Compute(toc);
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "ClaudeSoundtrack/1.0 (+https://github.com/Midcon113/ClaudeSoundtrack)");
                var records = await AccurateRipDatabase.LookupAsync(
                    http, ids.AccurateRipId1, ids.AccurateRipId2, ids.FreedbDiscId, audioTrackCount, cancellationToken)
                    .ConfigureAwait(false);
                databaseKnowsDisc = records.Count > 0;

                // The database is for the whole disc. A partial rip (one track
                // retried) cannot be matched track-for-track against it.
                if (fingerprints.Count == audioTrackCount)
                {
                    var checksums = fingerprints
                        .OrderBy(f => f.Toc.Number)
                        .Select(f => (f.Track.AccurateRipCrc ?? 0, f.Track.AccurateRipV2Crc ?? 0))
                        .ToList();
                    pressing = AccurateRipDatabase.BestMatch(records, checksums);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                pressing = null;
            }
        }

        for (var i = 0; i < fingerprints.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (track, tocTrack, sha) = fingerprints[i];

            var arIndex = tocTrack.Number - 1;
            var arHit = pressing is not null
                        && arIndex >= 0 && arIndex < pressing.TrackCount
                        && (track.AccurateRipCrc == pressing.CrcV1[arIndex]
                            || track.AccurateRipV2Crc == pressing.CrcV2[arIndex]);

            if (arHit)
            {
                track.Confidence = RipConfidence.AccurateRip;
                track.AccurateRipDisagreed = false;
                continue;
            }

            if (databaseKnowsDisc)
                track.AccurateRipDisagreed = true;

            progress?.Report(new RipTrackProgress(
                track.SourceTrackNumber, track.Title, i, fingerprints.Count, 0, track.HadReadErrors, IsComplete: false));

            var second = await HashTrackAsync(drive, tocTrack, cancellationToken).ConfigureAwait(false);
            var match = second.AsSpan().SequenceEqual(sha);
            track.Confidence = match ? RipConfidence.Repeatable : RipConfidence.Unstable;
            if (!match) track.HadReadErrors = true;

            progress?.Report(new RipTrackProgress(
                track.SourceTrackNumber, track.Title, i, fingerprints.Count, 100, track.HadReadErrors, IsComplete: false));
        }
    }

    /// <summary>Reads a track again and returns the SHA-256 of the audio bytes.</summary>
    private static async Task<byte[]> HashTrackAsync(
        IOpticalDrive drive, TrackInfo tocTrack, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[SectorsPerRead * BytesPerSector];
        long sectorsDone = 0;
        var total = tocTrack.SectorCount;

        while (sectorsDone < total)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var want = (int)Math.Min(SectorsPerRead, total - sectorsDone);
            var result = await ReadChunkAsync(drive, tocTrack.StartLba + sectorsDone, want, buffer, cancellationToken)
                .ConfigureAwait(false);
            hash.AppendData(buffer, 0, want * BytesPerSector);
            sectorsDone += want;
            _ = result;
        }

        return hash.GetHashAndReset();
    }

    /// <summary>Outcome of reading one chunk of sectors.</summary>
    /// <param name="HadError">True when some part of the chunk could not be read.</param>
    private readonly record struct ChunkResult(bool HadError);

    /// <summary>
    /// Reads <paramref name="count"/> sectors into <paramref name="buffer"/>,
    /// retrying and then narrowing down to isolate an unreadable sector.
    ///
    /// A scratched disc usually has a handful of bad sectors in an otherwise fine
    /// track. Failing the whole track would throw away 4 minutes of good audio
    /// over a few milliseconds of damage, so unreadable sectors are filled with
    /// silence and the track is flagged instead.
    /// </summary>
    private static async Task<ChunkResult> ReadChunkAsync(
        IOpticalDrive drive, long lba, int count, byte[] buffer, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxChunkRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var got = await drive.ReadSectorsAsync(lba, count, buffer, ReadOptions.None, cancellationToken)
                    .ConfigureAwait(false);

                if (got == count) return new ChunkResult(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Fall through to retry, then to per-sector isolation.
            }
        }

        // The chunk failed as a unit. Read it sector by sector so one bad sector
        // does not cost the ~27 good ones around it.
        var hadError = false;

        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var target = buffer.AsMemory(i * BytesPerSector, BytesPerSector);
            var recovered = false;

            for (var attempt = 0; attempt < MaxChunkRetries && !recovered; attempt++)
            {
                try
                {
                    if (await drive.ReadSectorsAsync(lba + i, 1, target, ReadOptions.None, cancellationToken)
                            .ConfigureAwait(false) == 1)
                    {
                        recovered = true;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Try again, then give up on this sector.
                }
            }

            if (!recovered)
            {
                // Silence for the damaged sector: 1/75th of a second, audible as
                // a tick at worst, and the track is flagged so it can be re-ripped.
                target.Span.Clear();
                hadError = true;
            }
        }

        return new ChunkResult(hadError);
    }
}
