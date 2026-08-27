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

            var toc = await drive.ReadTocAsync(cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < tracks.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var track = tracks[i];
                var tocTrack = toc.Tracks.FirstOrDefault(t => t.Number == track.SourceTrackNumber);

                if (tocTrack.Number != track.SourceTrackNumber)
                    throw new InvalidOperationException(
                        $"Track {track.SourceTrackNumber} is not present on the disc in {devicePath}.");

                // Data tracks (CD-Extra, enhanced CDs) are not audio and must be skipped.
                if (tocTrack.Type == TrackType.Data) continue;

                var destination = Path.Combine(outputFolder, fileNames[i]);
                await RipSingleTrackAsync(
                        drive, tocTrack, track, destination, i, tracks.Count, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            // Dispose only the outermost handle; the wrapper owns the inner drive.
            if (wrapper is not null) wrapper.Dispose();
            else if (raw is IDisposable disposable) disposable.Dispose();
        }
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
    private static async Task RipSingleTrackAsync(
        IOpticalDrive drive,
        TrackInfo tocTrack,
        SoundtrackTrack track,
        string destination,
        int trackIndex,
        int trackCount,
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

            // Exactly one completion report per track, so callers can log on it
            // without having to de-duplicate.
            progress?.Report(new RipTrackProgress(
                track.SourceTrackNumber, track.Title, trackIndex, trackCount, 100, hadErrors, IsComplete: true));
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
