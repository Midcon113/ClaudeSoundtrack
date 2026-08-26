using ClaudeSoundtrack.Core.Models;
using ClaudeSoundtrack.Core.Services;
using Xunit;

namespace ClaudeSoundtrack.Core.Tests;

public class TrackFlattenerTests
{
    /// <summary>Builds a project with <paramref name="perDisc"/> tracks on each of several discs.</summary>
    private static AlbumProject BuildProject(params int[] perDisc)
    {
        var project = new AlbumProject { AlbumTitle = "Test Score", AlbumArtist = "Composer" };

        for (var disc = 1; disc <= perDisc.Length; disc++)
        {
            for (var track = 1; track <= perDisc[disc - 1]; track++)
            {
                project.Tracks.Add(new SoundtrackTrack
                {
                    SourceDiscNumber = disc,
                    SourceTrackNumber = track,
                    Title = $"D{disc}T{track}"
                });
            }
        }

        return project;
    }

    /// <summary>
    /// The requirement stated literally: three discs of ten become 1-10, 11-20,
    /// 21-30.
    /// </summary>
    [Fact]
    public void ThreeDiscsOfTenBecomeOneToThirty()
    {
        var project = BuildProject(10, 10, 10);

        TrackFlattener.Flatten(project);

        Assert.Equal(30, project.Tracks.Count);
        Assert.Equal(Enumerable.Range(1, 30), project.Tracks.Select(t => t.FlatTrackNumber));

        var disc2 = project.Tracks.Where(t => t.SourceDiscNumber == 2).ToList();
        Assert.Equal(11, disc2.First().FlatTrackNumber);
        Assert.Equal(20, disc2.Last().FlatTrackNumber);

        var disc3 = project.Tracks.Where(t => t.SourceDiscNumber == 3).ToList();
        Assert.Equal(21, disc3.First().FlatTrackNumber);
        Assert.Equal(30, disc3.Last().FlatTrackNumber);
    }

    [Fact]
    public void HandlesDiscsOfUnequalLength()
    {
        var project = BuildProject(23, 18, 25);

        TrackFlattener.Flatten(project);

        Assert.Equal(66, project.Tracks.Count);
        Assert.Equal(Enumerable.Range(1, 66), project.Tracks.Select(t => t.FlatTrackNumber));
        Assert.Equal(24, project.Tracks.First(t => t.SourceDiscNumber == 2).FlatTrackNumber);
        Assert.Equal(42, project.Tracks.First(t => t.SourceDiscNumber == 3).FlatTrackNumber);
    }

    [Fact]
    public void SingleDiscIsLeftInPlace()
    {
        var project = BuildProject(12);

        TrackFlattener.Flatten(project);

        Assert.Equal(Enumerable.Range(1, 12), project.Tracks.Select(t => t.FlatTrackNumber));
        Assert.All(project.Tracks, t => Assert.Equal(t.SourceTrackNumber, t.FlatTrackNumber));
    }

    /// <summary>
    /// Discs can be ripped out of order if the user grabs disc 2 first. The
    /// flattened order must still follow the composer's sequencing.
    /// </summary>
    [Fact]
    public void SortsByDiscThenTrackRegardlessOfInsertionOrder()
    {
        var project = new AlbumProject();
        project.Tracks.Add(new SoundtrackTrack { SourceDiscNumber = 2, SourceTrackNumber = 1, Title = "B1" });
        project.Tracks.Add(new SoundtrackTrack { SourceDiscNumber = 1, SourceTrackNumber = 2, Title = "A2" });
        project.Tracks.Add(new SoundtrackTrack { SourceDiscNumber = 1, SourceTrackNumber = 1, Title = "A1" });

        TrackFlattener.Flatten(project);

        Assert.Equal(["A1", "A2", "B1"], project.Tracks.Select(t => t.Title));
        Assert.Equal([1, 2, 3], project.Tracks.Select(t => t.FlatTrackNumber));
    }

    [Fact]
    public void FlatteningIsIdempotent()
    {
        var project = BuildProject(10, 10);

        TrackFlattener.Flatten(project);
        var first = project.Tracks.Select(t => t.FlatTrackNumber).ToList();
        TrackFlattener.Flatten(project);

        Assert.Equal(first, project.Tracks.Select(t => t.FlatTrackNumber));
    }

    [Fact]
    public void SourceNumbersAreNotDestroyed()
    {
        var project = BuildProject(10, 10);

        TrackFlattener.Flatten(project);

        // Provenance survives, so the mapping can be shown and recomputed.
        var disc2First = project.Tracks.First(t => t.SourceDiscNumber == 2);
        Assert.Equal(1, disc2First.SourceTrackNumber);
        Assert.Equal(11, disc2First.FlatTrackNumber);
    }

    [Fact]
    public void EmptyProjectIsHandled()
    {
        var project = new AlbumProject();

        Assert.Equal(0, TrackFlattener.Flatten(project));
    }

    [Fact]
    public void DescribesTheMappingPerDisc()
    {
        var project = BuildProject(10, 10, 10);
        TrackFlattener.Flatten(project);

        var lines = TrackFlattener.DescribeMapping(project);

        Assert.Equal(3, lines.Count);
        Assert.Contains("unchanged", lines[0]);
        Assert.Contains("become 11-20", lines[1]);
        Assert.Contains("become 21-30", lines[2]);
    }
}
