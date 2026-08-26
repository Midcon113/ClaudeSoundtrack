using ClaudeSoundtrack.Core.Models;
using ClaudeSoundtrack.Core.Services;
using CUETools.Codecs;
using CUETools.Codecs.FLAKE;
using Xunit;

namespace ClaudeSoundtrack.Core.Tests;

/// <summary>
/// Exercises the real pipeline end to end, minus the optical drive: encode FLAC,
/// flatten a two-disc set, write tags and artwork, then verify from disk.
///
/// The unit tests cover the rules in isolation; this covers the thing that
/// actually ships. Every assertion here is read back off a real file rather than
/// from the in-memory model, because the failure that matters is the one where
/// the app believes it wrote something it did not.
/// </summary>
public class PipelineIntegrationTests : IDisposable
{
    private readonly string _folder;

    public PipelineIntegrationTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "ClaudeSoundtrackTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Writes a short but genuine FLAC file, the same way the ripper does.</summary>
    private static void WriteFlac(string path, double seconds = 0.5)
    {
        var pcm = new AudioPCMConfig(16, 2, 44100);
        var frames = (int)(44100 * seconds);

        var writer = new FlakeWriter(path, pcm) { FinalSampleCount = frames, CompressionLevel = 5 };

        var bytes = new byte[frames * 4];
        for (var i = 0; i < frames; i++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * 440 * i / 44100.0) * 8000);
            bytes[i * 4 + 0] = (byte)(sample & 0xFF);
            bytes[i * 4 + 1] = (byte)((sample >> 8) & 0xFF);
            bytes[i * 4 + 2] = bytes[i * 4 + 0];
            bytes[i * 4 + 3] = bytes[i * 4 + 1];
        }

        writer.Write(new AudioBuffer(pcm, bytes, frames));
        writer.Close();
    }

    /// <summary>A tiny but structurally valid PNG, standing in for cover art.</summary>
    private static byte[] TinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    /// <summary>
    /// Builds a two-disc set (3 tracks + 2 tracks), ripped and named the way the
    /// app does it.
    /// </summary>
    private AlbumProject BuildRippedProject()
    {
        var project = new AlbumProject
        {
            AlbumTitle = "The Complete Test Score",
            AlbumArtist = "Jerry Goldsmith",
            Year = 1979,
            CoverArt = TinyPng(),
            CoverArtWidth = 1400,
            CoverArtHeight = 1400,
            OutputFolder = _folder
        };

        var discs = new[]
        {
            (Disc: 1, Titles: new[] { "01 - Main Titles", "02 - The Chase", "03 - Source Music" }),
            (Disc: 2, Titles: new[] { "01 - Source Music", "02 - End Credits" })
        };

        var usedStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (disc, titles) in discs)
        {
            var tracks = titles.Select((title, i) => new SoundtrackTrack
            {
                SourceDiscNumber = disc,
                SourceTrackNumber = i + 1,
                Title = FileNaming.StripLeadingTrackNumber(title),
                Artist = "Jerry Goldsmith"
            }).ToList();

            project.Tracks.AddRange(tracks);
            TrackFlattener.Flatten(project);

            var names = FileNaming.BuildUniqueFileNames(tracks.Select(t => t.Title), usedStems);

            for (var i = 0; i < tracks.Count; i++)
            {
                var path = Path.Combine(_folder, names[i]);
                WriteFlac(path);
                tracks[i].FilePath = path;
                tracks[i].IsRipped = true;
            }

            project.RippedDiscs.Add(disc);
        }

        return project;
    }

    [Fact]
    public void TwoDiscSetFlattensTagsAndPassesTheReadinessCheck()
    {
        var project = BuildRippedProject();

        var written = new TaggingService().WriteAllTags(project);
        Assert.Equal(5, written);

        var report = new ReadinessChecker().Check(project);

        // Anything unexpected should be visible in the failure message, not just
        // an opaque false.
        Assert.True(report.IsReady,
            "Expected the album to pass. Issues: " +
            string.Join(" | ", report.Issues.Select(i => $"{i.Severity}: {i.Message}")));

        Assert.Equal(5, report.FilesChecked);
    }

    [Fact]
    public void FlattenedNumbersAreWrittenIntoTheFiles()
    {
        var project = BuildRippedProject();
        new TaggingService().WriteAllTags(project);

        // Disc 2 track 1 must be track 4 of 5 on disc 1 of 1.
        var disc2First = project.Tracks.Single(t => t.SourceDiscNumber == 2 && t.SourceTrackNumber == 1);
        var file = new ATL.Track(disc2First.FilePath!);

        Assert.Equal(4, file.TrackNumber);
        Assert.Equal(5, file.TrackTotal);
        Assert.Equal(1, file.DiscNumber);
        Assert.Equal(1, file.DiscTotal);
    }

    [Fact]
    public void TitlesAndFileNamesCarryNoTrackNumbers()
    {
        var project = BuildRippedProject();
        new TaggingService().WriteAllTags(project);

        foreach (var track in project.Tracks)
        {
            var file = new ATL.Track(track.FilePath!);
            var stem = Path.GetFileNameWithoutExtension(track.FilePath!);

            Assert.Equal(file.Title, FileNaming.StripLeadingTrackNumber(file.Title));
            Assert.Equal(stem, FileNaming.StripLeadingTrackNumber(stem));
        }

        // The source titles all began "01 - "; none of that survives.
        Assert.Contains(project.Tracks, t => t.Title == "Main Titles");
        Assert.DoesNotContain(project.Tracks, t => t.Title.StartsWith("01"));
    }

    /// <summary>
    /// "Source Music" appears on both discs. Without cross-disc disambiguation
    /// the second would overwrite the first and the album would lose a track.
    /// </summary>
    [Fact]
    public void RepeatedTitlesAcrossDiscsProduceSeparateFiles()
    {
        var project = BuildRippedProject();

        var sourceMusic = project.Tracks.Where(t => t.Title == "Source Music").ToList();

        Assert.Equal(2, sourceMusic.Count);
        Assert.NotEqual(sourceMusic[0].FilePath, sourceMusic[1].FilePath);
        Assert.Equal(5, Directory.GetFiles(_folder, "*.flac").Length);
    }

    [Fact]
    public void ArtworkIsEmbeddedInEveryTrack()
    {
        var project = BuildRippedProject();
        new TaggingService().WriteAllTags(project);

        foreach (var track in project.Tracks)
        {
            var file = new ATL.Track(track.FilePath!);
            Assert.Single(file.EmbeddedPictures);
            Assert.Equal(ATL.PictureInfo.PIC_TYPE.Front, file.EmbeddedPictures[0].PicType);
        }
    }

    [Fact]
    public void ReadinessCheckCatchesMissingArtwork()
    {
        var project = BuildRippedProject();
        project.CoverArt = null;
        new TaggingService().WriteAllTags(project);

        var report = new ReadinessChecker().Check(project);

        Assert.False(report.IsReady);
        Assert.Contains(report.Issues, i => i.Message.Contains("cover art", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReadinessCheckCatchesAMissingFile()
    {
        var project = BuildRippedProject();
        new TaggingService().WriteAllTags(project);

        File.Delete(project.Tracks[2].FilePath!);

        var report = new ReadinessChecker().Check(project);

        Assert.False(report.IsReady);
        Assert.Contains(report.Issues, i => i.Message.Contains("missing from disk"));
    }

    /// <summary>
    /// The check must read the files, not the model. Tagging a file as disc 2
    /// behind the app's back has to be caught.
    /// </summary>
    [Fact]
    public void ReadinessCheckReadsTheFilesRatherThanTheModel()
    {
        var project = BuildRippedProject();
        new TaggingService().WriteAllTags(project);

        var tampered = new ATL.Track(project.Tracks[0].FilePath!);
        tampered.DiscNumber = 2;
        tampered.Save();

        var report = new ReadinessChecker().Check(project);

        Assert.False(report.IsReady);
        Assert.Contains(report.Issues, i => i.Message.Contains("disc 2"));
    }

    [Fact]
    public void EncodedFilesAreRealFlacAudio()
    {
        var project = BuildRippedProject();
        new TaggingService().WriteAllTags(project);

        var file = new ATL.Track(project.Tracks[0].FilePath!);

        Assert.Equal("Free Lossless Audio Codec", file.AudioFormat?.Name);
        Assert.Equal(44100, file.SampleRate);
        Assert.True(file.DurationMs > 0);
    }
}
