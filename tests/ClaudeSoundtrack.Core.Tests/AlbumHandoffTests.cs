using ClaudeSoundtrack.Core.Models;
using ClaudeSoundtrack.Core.Services;
using FoxRedbook;
using Xunit;

namespace ClaudeSoundtrack.Core.Tests;

public class AlbumTitleCleanerTests
{
    [Theory]
    [InlineData("The Rock - Disc 1", "The Rock", 1, null)]
    [InlineData("The Rock - Disc 2", "The Rock", 2, null)]
    [InlineData("The Rock – Disc 2", "The Rock", 2, null)]
    [InlineData("The Rock: Disc 2 of 2", "The Rock", 2, 2)]
    [InlineData("The Rock Disc 2 of 3", "The Rock", 2, 3)]
    [InlineData("The Rock (Disc 2)", "The Rock", 2, null)]
    [InlineData("The Rock (Disc 2 of 2)", "The Rock", 2, 2)]
    [InlineData("The Rock [CD 2]", "The Rock", 2, null)]
    [InlineData("The Rock [CD 2 of 2]", "The Rock", 2, 2)]
    [InlineData("The Rock - CD2", "The Rock", 2, null)]
    [InlineData("the rock / disk 1", "the rock", 1, null)]
    public void StripsADiscSuffix(string raw, string title, int disc, int? count)
    {
        var parsed = AlbumTitleCleaner.Parse(raw);

        Assert.True(parsed.HadDiscSuffix);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(disc, parsed.DiscNumber);
        Assert.Equal(count, parsed.DiscCount);
    }

    [Theory]
    [InlineData("Disc")]
    [InlineData("Discworld")]
    [InlineData("The Disc")]
    [InlineData("CD")]
    [InlineData("The Rock")]
    [InlineData("The Rock: Original Motion Picture Score")]
    [InlineData("2001: A Space Odyssey")]
    public void LeavesARealTitleAlone(string title)
    {
        var parsed = AlbumTitleCleaner.Parse(title);

        Assert.False(parsed.HadDiscSuffix);
        Assert.Equal(title, parsed.Title);
        Assert.Null(parsed.DiscNumber);
    }

    [Fact]
    public void LockingStripsTheSuffixAndALaterDiscCannotPutItBack()
    {
        var project = new AlbumProject();
        AlbumAssembler.LockIdentity(project, "The Rock - Disc 1", "Hans Zimmer", 2023, match: null);

        Assert.Equal("The Rock", project.AlbumTitle);
        Assert.True(project.IdentityLocked);

        AlbumAssembler.LockIdentity(project, "The Rock - Disc 2", "Somebody Else", 1996, match: null);

        Assert.Equal("The Rock", project.AlbumTitle);
        Assert.Equal("Hans Zimmer", project.AlbumArtist);
        Assert.Equal(2023, project.Year);
    }
}

public class DiscHandoffTests
{
    [Fact]
    public void ChoosingADiscTwoCandidateSetsDiscTwo()
    {
        var project = new AlbumProject { IdentityLocked = true, DiscCount = 2, AlbumTitle = "The Rock" };
        project.RippedDiscs.Add(1);
        var match = new ReleaseMatch(
            "MusicBrainz", "The Rock: Original Motion Picture Score", "Hans Zimmer", 2023,
            ["Hostage"], 2, 2, "Intrada", "ISC 491", "628cd292-8fc1-4b35-93ce-192cfc5cd5d2", true);

        Assert.Equal(2, AlbumAssembler.ChooseDiscNumber(project, match));
    }

    [Fact]
    public void CdTextDiscTwoWinsOverAMediumNumberedOne()
    {
        var project = new AlbumProject { IdentityLocked = true, AlbumTitle = "The Rock - Disc 1" };
        project.RippedDiscs.Add(1);
        var match = new ReleaseMatch(
            "CD-Text", "The Rock - Disc 2", "Hans Zimmer", null,
            ["Hostage"], 1, 1, null, null, null, true);

        Assert.Equal(2, AlbumAssembler.ChooseDiscNumber(project, match));
    }

    [Fact]
    public void WithNoCandidateTheNextDiscIsLastRippedPlusOne()
    {
        var project = new AlbumProject { IdentityLocked = true, DiscCount = 2 };
        project.RippedDiscs.Add(1);

        Assert.Equal(2, AlbumAssembler.ChooseDiscNumber(project, match: null));
        Assert.Equal("Insert disc 2 of 2", AlbumAssembler.DescribeNextDisc(project));
    }

    [Fact]
    public void NextDiscIsCappedAtTheDiscTotal()
    {
        var project = new AlbumProject { IdentityLocked = true, DiscCount = 2 };
        project.RippedDiscs.Add(1);
        project.RippedDiscs.Add(2);
        var match = new ReleaseMatch(
            "CD-Text", "The Rock - Disc 9", "Hans Zimmer", null,
            ["x"], 9, 9, null, null, null, false);

        Assert.Equal(2, AlbumAssembler.ChooseDiscNumber(project, match));
    }

    [Fact]
    public void LockedAlbumHidesUnrelatedHitsAndKeepsTheRock()
    {
        var project = new AlbumProject
        {
            IdentityLocked = true,
            AlbumTitle = "The Rock",
            AlbumArtist = "Hans Zimmer"
        };
        var cdText = new ReleaseMatch(
            "CD-Text", "The Rock - Disc 2", "Hans Zimmer", null,
            ["Hostage"], 2, 2, null, null, null, true);
        var musicBrainz = new ReleaseMatch(
            "MusicBrainz", "The Rock: Original Motion Picture Score",
            "Nick Glennie-Smith, Hans Zimmer and Harry Gregson-Williams", 2023,
            ["Hostage"], 2, 2, "Intrada", "ISC 491", "628cd292-8fc1-4b35-93ce-192cfc5cd5d2", true)
        { ReleaseGroupId = "cae7deb7-8bc8-3798-98e8-6e3f511fdd65" };
        var pinkFloyd = new ReleaseMatch(
            "Discogs", "The Wall", "Pink Floyd", 1979,
            ["Hey You"], 1, 1, null, null, "pink", false);

        Assert.True(AlbumCandidateFilter.Belongs(project, cdText));
        Assert.True(AlbumCandidateFilter.Belongs(project, musicBrainz));
        Assert.False(AlbumCandidateFilter.Belongs(project, pinkFloyd));
    }
}

/// <summary>
/// Intrada ISC 491, The Rock (MusicBrainz 628cd292-8fc1-4b35-93ce-192cfc5cd5d2).
/// Disc 2's submitted TOC is what a drive reports, minus the 150-frame lead-in.
/// </summary>
public class TheRockMusicBrainzTests
{
    private const string Disc2Id = "iyfrebx.KKTZzKVyclTp.XauyfM-";

    private static readonly long[] Disc2Lbas =
    [
        0, 25154, 29067, 56255, 65232, 138697, 146182, 156233, 163311, 170642, 182401, 193268, 209566, 245906, 283152
    ];

    private const long Disc2LeadOutLba = 302490;

    [Fact]
    public void TocQueryUsesTheLeadInAndMatchesTheSubmittedDisc()
    {
        var toc = MusicBrainzClient.FormatToc(1, 15, Disc2LeadOutLba, Disc2Lbas);

        Assert.Equal(
            "1+15+302640+150+25304+29217+56405+65382+138847+146332+156383+163461+170792+182551+193418+209716+246056+283302",
            toc);

        var url = MusicBrainzClient.TocUrl(toc);
        Assert.Contains("cdstubs=no", url, StringComparison.Ordinal);
        Assert.DoesNotContain("discids", url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Uri.EscapeDataString(toc), url, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscIdUrlDoesNotAskForTheIncludeMusicBrainzRejects()
    {
        var url = MusicBrainzClient.DiscIdUrl(Disc2Id);

        Assert.Contains("/discid/" + Disc2Id, url, StringComparison.Ordinal);
        Assert.DoesNotContain("discids", url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fmt=json", url, StringComparison.Ordinal);
    }

    [Fact]
    public void EnhancedCdLeadOutStopsBeforeTheDataSession()
    {
        var toc = new TableOfContents
        {
            FirstTrackNumber = 1,
            LastTrackNumber = 3,
            LeadOutLba = 200000,
            Tracks = new[]
            {
                new TrackInfo { Number = 1, StartLba = 0, SectorCount = 100, Type = TrackType.Audio, Control = TrackControl.None },
                new TrackInfo { Number = 2, StartLba = 100, SectorCount = 50, Type = TrackType.Audio, Control = TrackControl.None },
                new TrackInfo { Number = 3, StartLba = 160000, SectorCount = 1000, Type = TrackType.Data, Control = TrackControl.DataTrack }
            }.ToList().AsReadOnly()
        };

        Assert.Equal(160000 - MusicBrainzClient.MultiSessionGap, MusicBrainzClient.AudioLeadOutLba(toc));
    }

    [Fact]
    public void DiscIdResponseForThisReleaseSelectsDiscTwo()
    {
        const string json = """
            {
              "releases": [
                {
                  "id": "628cd292-8fc1-4b35-93ce-192cfc5cd5d2",
                  "title": "The Rock: Original Motion Picture Score",
                  "date": "2023",
                  "barcode": "720258549105",
                  "artist-credit": [
                    { "name": "Nick Glennie-Smith", "joinphrase": ", " },
                    { "name": "Hans Zimmer", "joinphrase": " and " },
                    { "name": "Harry Gregson-Williams", "joinphrase": "" }
                  ],
                  "release-group": { "id": "cae7deb7-8bc8-3798-98e8-6e3f511fdd65" },
                  "media": [
                    {
                      "position": 1,
                      "track-count": 20,
                      "discs": [ { "id": "9BuQvKAe752uu5ZHhvqNVJyII7E-", "sectors": 300912 } ],
                      "tracks": [ { "title": "Opening", "length": 487386 } ]
                    },
                    {
                      "position": 2,
                      "track-count": 15,
                      "discs": [ { "id": "iyfrebx.KKTZzKVyclTp.XauyfM-", "sectors": 302640 } ],
                      "tracks": [ { "title": "Hostage", "length": 335386 } ]
                    }
                  ]
                }
              ]
            }
            """;

        var match = Assert.Single(MusicBrainzClient.ParseDiscIdResponse(json, Disc2Id));

        Assert.Equal("628cd292-8fc1-4b35-93ce-192cfc5cd5d2", match.ReleaseId);
        Assert.Equal(2, match.DiscNumber);
        Assert.Equal(2, match.DiscCount);
        Assert.Equal(DiscMatchKind.DiscId, match.MatchKind);
        Assert.Equal(["Hostage"], match.TitlesForDisc(2));
        Assert.Equal("Nick Glennie-Smith, Hans Zimmer and Harry Gregson-Williams", match.Artist);
        Assert.Contains(match.Media, m => m.Number == 1 && m.DiscId == "9BuQvKAe752uu5ZHhvqNVJyII7E-");
    }
}

public class AlbumRepairTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClaudeSoundtrackRepair", Guid.NewGuid().ToString("N"));

    public AlbumRepairTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void RepairRenamesTheFolderAndRetagsWithoutTouchingTheAudio()
    {
        var folder = Path.Combine(_root, "Hans Zimmer - The Rock - Disc 1 (2023)");
        Directory.CreateDirectory(folder);
        var project = new AlbumProject
        {
            AlbumTitle = "The Rock - Disc 1",
            AlbumArtist = "Hans Zimmer",
            Year = 2023,
            DiscCount = 1,
            OutputFolder = folder,
            CoverArt = SampleAlbumWriter.SampleCover,
            CoverArtWidth = 1000,
            CoverArtHeight = 1000
        };
        project.Tracks.Add(new SoundtrackTrack
        {
            SourceDiscNumber = 1,
            SourceTrackNumber = 1,
            Title = "Opening",
            Artist = "Hans Zimmer"
        });
        TrackFlattener.Flatten(project);
        var path = Path.Combine(folder, "Opening.flac");
        WriteTone(path);
        project.Tracks[0].FilePath = path;
        project.Tracks[0].IsRipped = true;
        new TaggingService().WriteAllTags(project);

        var plan = AlbumRepair.Plan(project);

        Assert.NotNull(plan);
        Assert.True(plan!.ChangesAnything);
        Assert.Equal("The Rock", plan.ToTitle);
        Assert.Contains("The Rock - Disc 1", plan.FromTitle);

        project.DiscCount = 2;
        plan = AlbumRepair.Plan(project);
        AlbumRepair.Apply(project, plan!);

        Assert.Equal("The Rock", project.AlbumTitle);
        Assert.True(Directory.Exists(plan!.ToFolder));
        Assert.False(Directory.Exists(folder));

        var moved = project.Tracks[0].FilePath!;
        var file = new ATL.Track(moved);
        Assert.Equal("The Rock", file.Album);
        Assert.Equal("Hans Zimmer", file.AlbumArtist);
        Assert.Equal(1, file.DiscNumber);
        Assert.Equal(2, file.DiscTotal);
        Assert.Equal(1, file.TrackNumber);
        Assert.Equal(SampleAlbumWriter.SampleCover, file.EmbeddedPictures[0].PictureData);

        // The audio frames are still in the file. Retagging rewrites metadata,
        // so the whole file is not byte-identical, but it still plays.
        Assert.True(file.DurationMs > 0);
        Assert.Equal("Opening", file.Title);
    }

    private static void WriteTone(string path)
    {
        var pcm = new CUETools.Codecs.AudioPCMConfig(16, 2, 44100);
        const int frames = 4410;
        var writer = new CUETools.Codecs.FLAKE.FlakeWriter(path, pcm) { FinalSampleCount = frames, CompressionLevel = 5 };
        var bytes = new byte[frames * 4];
        for (var i = 0; i < frames; i++)
        {
            bytes[i * 4] = 1;
            bytes[i * 4 + 2] = 1;
        }

        writer.Write(new CUETools.Codecs.AudioBuffer(pcm, bytes, frames));
        writer.Close();
    }
}
