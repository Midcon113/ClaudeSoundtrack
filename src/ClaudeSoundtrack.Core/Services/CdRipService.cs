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
/// <param name="HadErrors">True once any sector in this track needed error handling.</param>
public readonly record struct RipTrackProgress(
    int TrackNumber,
    string TrackTitle,
    int TrackIndex,
    int TrackCount,
    double TrackPercent,
    bool HadErrors)
{
    /// <summary>Completion across the whole disc, 0-100.</summary>
    public double OverallPercent =>
        TrackCount <= 0 ? 0 : (TrackIndex * 100.0 + TrackPercent) / TrackCount;
}

/// <summary>
/// Rips audio tracks off a CD and encodes them straight to FLAC.
///
/// Audio never touches an intermediate WAV: sectors come back from FoxRedbook as
/// raw 16-bit stereo PCM and go directly into the FLAC encoder. A 74-minute disc
/// would otherwise cost ~750MB of scratch space per disc for no benefit.
///
/// The rip runs through <c>RipSession.CreateAutoCorrected</c>, which applies the
/// drive's read offset and re-reads damaged sectors - the difference between a
/// bit-perfect rip and one that is quietly a few samples out.
/// </summary>
public sealed class CdRipService
{
    /// <summary>A CD sector holds 2352 bytes = 588 stereo 16-bit sample frames.</summary>
    private const int BytesPerSector = 2352;

    /// <summary>Red Book audio is always 16-bit, stereo, 44.1kHz.</summary>
    private static AudioPCMConfig RedBookPcm => new(16, 2, 44100);

    /// <summary>
    /// How many sectors to accumulate before handing a block to the encoder.
    /// Roughly a second of audio - large enough that encoding is not called in a
    /// tight loop, small enough that progress stays responsive.
    /// </summary>
    private const int SectorsPerEncodeBlock = 64;

    /// <summary>
    /// Rips the requested tracks from the disc in <paramref name="devicePath"/>,
    /// writing one FLAC per track into <paramref name="outputFolder"/>.
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

        var drive = OpticalDrive.Open(devicePath);
        try
        {
            var toc = await drive.ReadTocAsync(cancellationToken).ConfigureAwait(false);

            using var session = RipSession.CreateAutoCorrected(drive, new RipOptions());

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
                await RipSingleTrackAsync(session, tocTrack, track, destination, i, tracks.Count, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            if (drive is IDisposable disposable) disposable.Dispose();
        }
    }

    /// <summary>
    /// Rips one track: pull sectors, batch them, push them into the encoder.
    /// </summary>
    private static async Task RipSingleTrackAsync(
        RipSession session,
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

            var block = new byte[SectorsPerEncodeBlock * BytesPerSector];
            var blockSectors = 0;
            var sectorsDone = 0;
            var lastReported = -1.0;

            await foreach (var sector in session
                .RipTrackAsync(tocTrack, progress: null, cancellationToken)
                .ConfigureAwait(false))
            {
                if (sector.HadErrors) hadErrors = true;

                sector.Pcm.Span.CopyTo(block.AsSpan(blockSectors * BytesPerSector, BytesPerSector));
                blockSectors++;
                sectorsDone++;

                if (blockSectors == SectorsPerEncodeBlock)
                {
                    WriteBlock(writer, pcm, block, blockSectors);
                    blockSectors = 0;
                }

                // Reporting on every sector would flood the UI thread; a disc is
                // ~350,000 sectors. Report on whole-percent changes only.
                var percent = totalSectors > 0 ? sectorsDone * 100.0 / totalSectors : 0;
                if (progress is not null && Math.Floor(percent) > lastReported)
                {
                    lastReported = Math.Floor(percent);
                    progress.Report(new RipTrackProgress(
                        track.SourceTrackNumber, track.Title, trackIndex, trackCount, percent, hadErrors));
                }
            }

            // Flush whatever did not fill a whole block.
            if (blockSectors > 0) WriteBlock(writer, pcm, block, blockSectors);

            writer.Close();
            writer = null;

            if (File.Exists(destination)) File.Delete(destination);
            File.Move(tempPath, destination);

            track.FilePath = destination;
            track.IsRipped = true;
            track.HadReadErrors = hadErrors;
            track.Duration = TimeSpan.FromSeconds((double)totalSectors / CdConstants.SectorsPerSecond);

            try
            {
                track.AccurateRipCrc = session.GetAccurateRipV1Crc(tocTrack);
            }
            catch
            {
                // Purely informational; a drive that cannot supply it is not a failure.
            }

            progress?.Report(new RipTrackProgress(
                track.SourceTrackNumber, track.Title, trackIndex, trackCount, 100, hadErrors));
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
    /// Hands one block of raw CD bytes to the FLAC encoder.
    ///
    /// AudioBuffer's length is in sample frames, not bytes - passing byte counts
    /// here silently produces four times too much audio.
    /// </summary>
    private static void WriteBlock(FlakeWriter writer, AudioPCMConfig pcm, byte[] block, int sectorCount)
    {
        var frames = sectorCount * CdConstants.SampleFramesPerSector;
        writer.Write(new AudioBuffer(pcm, block, frames));
    }
}
