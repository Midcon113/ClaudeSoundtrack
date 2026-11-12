using ClaudeSoundtrack.Core.Models;
using ClaudeSoundtrack.Core.Services;
using FoxRedbook;
using Xunit;

namespace ClaudeSoundtrack.Core.Tests;

/// <summary>
/// The failures that made a multi-disc upload come out differently each time:
/// the same track count treated as disc 1, a later match renaming the album,
/// and disc numbers stored as whatever order the discs were inserted.
/// </summary>
public class AlbumConsistencyTests
{
    private static DiscMedium Disc(int number, params int[] lengthMs) =>
        new(number, lengthMs.Select((_, i) => $"D{number} T{i + 1}").ToList(), lengthMs, DiscId: $"id-{number}");

    private static int Sectors(int lengthMs) => (int)Math.Round(lengthMs / 1000.0 * 75.0);

    [Fact]
    public void EqualTrackCountsAreNotBothDiscOne()
    {
        // Both discs have two cues. The old check verified the first one.
        var media = new[]
        {
            Disc(1, 180_000, 90_000),
            Disc(2, 200_000, 120_000)
        };
        var disc2 = new DiscSnapshot("something-else", [Sectors(200_000), Sectors(120_000)]);

        var id = DiscIdentifier.Identify(disc2, media);

        Assert.Equal(DiscMatchKind.Duration, id.Kind);
        Assert.Equal(2, id.DiscNumber);
    }

    [Fact]
    public void SharedTrackCountWithoutDurationsIsNotGuessed()
    {
        var media = new[]
        {
            new DiscMedium(1, ["A", "B"], [0, 0], null),
            new DiscMedium(2, ["C", "D"], [0, 0], null)
        };

        var id = DiscIdentifier.Identify(new DiscSnapshot(null, [1000, 1000]), media);

        Assert.Equal(DiscMatchKind.Ambiguous, id.Kind);
        Assert.False(id.IsConfident);
        Assert.Null(id.DiscNumber);
    }

    [Fact]
    public void DiscIdBeatsADurationThatWouldFitAnotherDisc()
    {
        var media = new[]
        {
            Disc(1, 180_000, 90_000),
            Disc(2, 200_000, 120_000)
        };
        // Lengths are disc 1's, but the disc id is disc 2's. The id wins.
        var snapshot = new DiscSnapshot("id-2", [Sectors(180_000), Sectors(90_000)]);

        var id = DiscIdentifier.Identify(snapshot, media);

        Assert.Equal(DiscMatchKind.DiscId, id.Kind);
        Assert.Equal(2, id.DiscNumber);
    }

    [Fact]
    public void ALaterMatchCannotRenameTheAlbumOrItsFolder()
    {
        var project = new AlbumProject();
        var first = new ReleaseMatch(
            "MusicBrainz", "Star Trek", "Jerry Goldsmith", 1979,
            ["Main Titles"], 1, 3, "La-La Land", "LLLCD 1", "rel-1", true);

        AlbumAssembler.LockIdentity(project, "Star Trek", "Jerry Goldsmith", 1979, first);
        var folder = FileNaming.BuildAlbumFolderName(project.AlbumTitle, project.AlbumArtist, project.Year);

        var second = new ReleaseMatch(
            "Discogs", "Star Trek (Disc 2)", "Goldsmith", 2012,
            ["Spock"], 2, 3, null, null, "other-release", true);

        AlbumAssembler.LockIdentity(project, second.Title, second.Artist, second.Year, second);

        Assert.Equal("Star Trek", project.AlbumTitle);
        Assert.Equal("Jerry Goldsmith", project.AlbumArtist);
        Assert.Equal(1979, project.Year);
        Assert.Equal(folder, FileNaming.BuildAlbumFolderName(project.AlbumTitle, project.AlbumArtist, project.Year));
    }

    [Fact]
    public void DetectedDiscNumberIsWhatGetsFlattened()
    {
        var project = new AlbumProject { AlbumTitle = "Score", AlbumArtist = "Jerry Goldsmith", DiscCount = 2 };
        // Disc 2 was inserted first. It must not become tracks 1-2.
        project.Tracks.AddRange(AlbumAssembler.BuildTracks(2, [100, 100], ["Spock's Walk", "End Credits"], project.AlbumArtist));
        project.Tracks.AddRange(AlbumAssembler.BuildTracks(1, [100], ["Main Titles"], project.AlbumArtist));
        TrackFlattener.Flatten(project);

        Assert.Equal(["Main Titles", "Spock's Walk", "End Credits"], project.Tracks.Select(t => t.Title));
        Assert.Equal([1, 2, 3], project.Tracks.Select(t => t.FlatTrackNumber));
        Assert.Equal([1, 2, 2], project.Tracks.Select(t => t.SourceDiscNumber));
    }

    [Fact]
    public void TitlesForTheWrongDiscAreNotReused()
    {
        var match = new ReleaseMatch(
            "MusicBrainz", "Score", "Jerry Goldsmith", 1979,
            ["Only Disc 2"], 2, 2, null, null, "rel", true);

        Assert.Equal(["Only Disc 2"], match.TitlesForDisc(2));
        Assert.Empty(match.TitlesForDisc(1));
    }

    [Fact]
    public void NextDiscPromptUsesTheReleaseSize()
    {
        var project = new AlbumProject { DiscCount = 3 };
        project.RippedDiscs.Add(1);

        Assert.Equal("Insert disc 2 of 3", AlbumAssembler.DescribeNextDisc(project));

        project.RippedDiscs.Add(2);
        Assert.Equal("Insert disc 3 of 3", AlbumAssembler.DescribeNextDisc(project));
    }

    [Fact]
    public void AmbiguousDiscogsMatchIsNotVerifiedAsDiscOne()
    {
        var json = """
            {
              "title": "The Complete Score",
              "year": 2012,
              "artists": [ { "name": "Jerry Goldsmith (2)" } ],
              "labels": [ { "name": "La-La Land Records", "catno": "LLLCD 1234" } ],
              "tracklist": [
                { "position": "1-1", "type_": "track", "title": "Main Titles", "duration": "3:00" },
                { "position": "1-2", "type_": "track", "title": "The Docking", "duration": "1:30" },
                { "position": "2-1", "type_": "track", "title": "Spock's Walk", "duration": "3:20" },
                { "position": "2-2", "type_": "track", "title": "End Credits", "duration": "2:00" }
              ]
            }
            """;

        var parsed = DiscogsClient.ParseReleaseJson(json, "999");
        Assert.NotNull(parsed);
        Assert.Equal("Jerry Goldsmith", parsed!.Artist);
        Assert.Equal(2, parsed.Media.Count);
        Assert.Equal(180_000, parsed.Media[0].LengthMs[0]);
        Assert.Equal(200_000, parsed.Media[1].LengthMs[0]);

        // No sector lengths: both discs have two tracks, so this must not become disc 1.
        var byCount = MetadataLookupService.ApplyToc(parsed, new DiscSnapshot(null, [1, 1]));
        Assert.False(byCount.IsTocVerified);
        Assert.Equal(DiscMatchKind.Ambiguous, byCount.MatchKind);

        // Real lengths pick disc 2. 3:20 is 200 seconds, 2:00 is 120 seconds.
        var disc2 = new DiscSnapshot(null, [Sectors(200_000), Sectors(120_000)]);
        var byLength = MetadataLookupService.ApplyToc(parsed, disc2);
        Assert.True(byLength.IsTocVerified);
        Assert.Equal(2, byLength.DiscNumber);
        Assert.Equal(DiscMatchKind.Duration, byLength.MatchKind);
        Assert.Equal(["Spock's Walk", "End Credits"], byLength.TitlesForDisc(2));
    }
}

public class MusicBrainzParseTests
{
    private const string Response = """
        {
          "releases": [
            {
              "id": "rel-box",
              "title": "The Complete Score",
              "date": "2012-06-01",
              "artist-credit": [
                { "name": "Jerry Goldsmith", "joinphrase": " & " },
                { "name": "The London Symphony Orchestra", "joinphrase": "" }
              ],
              "label-info": [ { "catalog-number": "LLLCD 1234", "label": { "name": "La-La Land Records" } } ],
              "release-group": { "id": "rg-1" },
              "media": [
                {
                  "position": 1,
                  "tracks": [
                    { "title": "Main Titles", "length": 180000 },
                    { "title": "The Docking", "length": 90000 }
                  ],
                  "discs": [ { "id": "discA" } ]
                },
                {
                  "position": 2,
                  "tracks": [
                    { "title": "Spock's Walk", "length": 200000 },
                    { "title": "End Credits", "length": 120000 }
                  ],
                  "discs": [ { "id": "discB" } ]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void EveryMediumIsKeptAndTheDiscIdSelectsOne()
    {
        var matches = MusicBrainzClient.ParseDiscIdResponse(Response, "discB");

        var match = Assert.Single(matches);
        Assert.Equal("Jerry Goldsmith & The London Symphony Orchestra", match.Artist);
        Assert.Equal(2, match.Media.Count);
        Assert.Equal(2, match.DiscNumber);
        Assert.True(match.IsTocVerified);
        Assert.Equal(DiscMatchKind.DiscId, match.MatchKind);
        Assert.Equal(["Spock's Walk", "End Credits"], match.TitlesForDisc(2));
        Assert.Equal(["Main Titles", "The Docking"], match.TitlesForDisc(1));
        Assert.Equal("rg-1", match.ReleaseGroupId);
        Assert.Equal(2012, match.Year);
    }

    [Fact]
    public void ATocHitDoesNotPretendToKnowWhichDisc()
    {
        var match = Assert.Single(MusicBrainzClient.ParseDiscIdResponse(Response, discId: null));

        Assert.False(match.IsTocVerified);
        Assert.Equal(2, match.Media.Count);
        Assert.Equal(0, match.DiscNumber);
    }
}

public class GnudbParseTests
{
    [Fact]
    public void ReadResponseBecomesASingleDiscMatch()
    {
        var body = """
            210 rock abcdef01
            DTITLE=Jerry Goldsmith / Star Trek
            DYEAR=1979
            TTITLE0=Main Titles
            TTITLE1=End Credits
            .
            """;

        var match = GnudbClient.ParseRead(body);

        Assert.NotNull(match);
        Assert.Equal("gnudb", match!.Source);
        Assert.Equal("Jerry Goldsmith", match.Artist);
        Assert.Equal("Star Trek", match.Title);
        Assert.Equal(["Main Titles", "End Credits"], match.TrackTitles);
        Assert.Equal(1979, match.Year);
    }

    [Fact]
    public void QueryResponseFindsTheFirstHit()
    {
        var hit = GnudbClient.ParseQuery("210 Found exact matches, list follows (until terminating `.')\nsoundtrack deadbeef Album Name\n.");
        // The status line is 210 plus words; the hit is the next line.
        Assert.NotNull(hit);
        Assert.Equal("soundtrack", hit!.Value.Genre);
        Assert.Equal("deadbeef", hit.Value.DiscId);
    }
}

public class TagStrategyTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ClaudeSoundtrackTests", Guid.NewGuid().ToString("N"));

    public TagStrategyTests() => Directory.CreateDirectory(_folder);
    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void SampleAlbumUsesPlainDiscNumbersAndOneSetOfBytes()
    {
        var report = SampleAlbumWriter.Write(_folder);

        Assert.Contains("Readiness: ready", report, StringComparison.OrdinalIgnoreCase);
        var flacs = Directory.GetFiles(_folder, "*.flac").OrderBy(f => f).ToList();
        Assert.Equal(6, flacs.Count);

        byte[]? art = null;
        var discNumbers = new List<string>();
        var trackNumbers = new List<int>();
        foreach (var path in flacs)
        {
            var comments = VorbisComments.TryRead(path);
            Assert.NotNull(comments);
            var disc = Assert.Single(comments!.Values("DISCNUMBER"));
            Assert.DoesNotContain("/", disc);
            discNumbers.Add(disc);
            var track = Assert.Single(comments.Values("TRACKNUMBER"));
            Assert.DoesNotContain("/", track);
            trackNumbers.Add(int.Parse(track));
            Assert.Equal("Jerry Goldsmith", Assert.Single(comments.Values("ARTIST")));
            Assert.Equal("Jerry Goldsmith", Assert.Single(comments.Values("ALBUMARTIST")));
            Assert.Equal("The Complete Test Score", Assert.Single(comments.Values("ALBUM")));

            var file = new ATL.Track(path);
            Assert.Single(file.EmbeddedPictures);
            var bytes = file.EmbeddedPictures[0].PictureData;
            art ??= bytes;
            Assert.Equal(art, bytes);
        }

        Assert.Equal(Enumerable.Range(1, 6), trackNumbers.OrderBy(n => n));
        Assert.Contains("1", discNumbers);
        Assert.Contains("2", discNumbers);

        var folderArt = Directory.GetFiles(_folder, "folder.*");
        Assert.Single(folderArt);
        Assert.Equal(SampleAlbumWriter.SampleCover, File.ReadAllBytes(folderArt[0]));
    }

    [Fact]
    public void CombinedDiscNumberIsRejectedEvenWhenAtlParsesIt()
    {
        var reportText = SampleAlbumWriter.Write(_folder);
        Assert.Contains("Readiness: ready", reportText, StringComparison.OrdinalIgnoreCase);

        var path = Directory.GetFiles(_folder, "*.flac")[0];
        var tagged = new ATL.Track(path);
        // ATL's numeric disc property cannot represent "1/2", and it overwrites
        // a comment of that name when the property is set. Clearing it leaves
        // the raw "n/n" value a tagger that copies the ID3 shape would write.
        tagged.DiscNumber = null;
        tagged.DiscTotal = null;
        tagged.AdditionalFields["DISCNUMBER"] = "1/2";
        Assert.True(tagged.Save());

        var project = ProjectFromSample();
        var check = new ReadinessChecker().Check(project);

        Assert.False(check.IsReady);
        Assert.Contains(check.Issues, i => i.Message.Contains("1/2", StringComparison.Ordinal));
    }

    private AlbumProject ProjectFromSample()
    {
        var project = new AlbumProject
        {
            AlbumTitle = "The Complete Test Score",
            AlbumArtist = "Jerry Goldsmith",
            Year = 1979,
            DiscCount = 2,
            CoverArt = SampleAlbumWriter.SampleCover,
            CoverArtWidth = 1400,
            CoverArtHeight = 1400,
            OutputFolder = _folder
        };

        // The sample names files without numbers; recover tracks from the tags.
        foreach (var path in Directory.GetFiles(_folder, "*.flac"))
        {
            var file = new ATL.Track(path);
            project.Tracks.Add(new SoundtrackTrack
            {
                FlatTrackNumber = file.TrackNumber ?? 0,
                SourceDiscNumber = file.DiscNumber ?? 1,
                SourceTrackNumber = 1,
                Title = file.Title ?? "",
                Artist = "Jerry Goldsmith",
                FilePath = path,
                IsRipped = true
            });
        }

        return project;
    }
}

public class AccurateRipTests
{
    [Fact]
    public void ChecksumMatchesTheHandWorkedFormula()
    {
        // Track 2 of 3: nothing is skipped. Three frames, each a small uint.
        var pcm = new byte[12];
        WriteFrame(pcm, 0, 1);
        WriteFrame(pcm, 4, 2);
        WriteFrame(pcm, 8, 3);

        var (v1, v2) = AccurateRipChecksum.Compute(pcm, trackNumber: 2, totalTracks: 3);

        // products 1*1 + 2*2 + 3*3, all inside 32 bits
        Assert.Equal(14u, v1);
        Assert.Equal(14u, v2);
    }

    [Fact]
    public void FirstTrackDropsTheOpeningEdge()
    {
        // Fewer samples than the opening skip: a track-1 checksum is zero.
        var pcm = new byte[4];
        WriteFrame(pcm, 0, 7);
        var (v1, v2) = AccurateRipChecksum.Compute(pcm, trackNumber: 1, totalTracks: 3);
        Assert.Equal(0u, v1);
        Assert.Equal(0u, v2);
    }

    [Fact]
    public void DatabasePathUsesTheLowNibblesAndParsesARecord()
    {
        var path = AccurateRipDatabase.RelativePath(0x12345678, 0x9abcdef0, 0x00aabbcc, 2);
        Assert.Equal("8/7/6/dBAR-002-12345678-9abcdef0-00aabbcc.bin", path);

        var body = new byte[1 + 12 + 9];
        body[0] = 1;
        BitConverter.GetBytes(0x12345678).CopyTo(body, 1);
        BitConverter.GetBytes(0x9abcdef0u).CopyTo(body, 5);
        BitConverter.GetBytes(0x00aabbcc).CopyTo(body, 9);
        body[13] = 4; // confidence
        BitConverter.GetBytes(0x11111111).CopyTo(body, 14);
        BitConverter.GetBytes(0x22222222).CopyTo(body, 18);

        var pressing = Assert.Single(AccurateRipDatabase.Parse(body));
        Assert.Equal(4, pressing.Confidence[0]);
        Assert.Equal(0x11111111u, pressing.CrcV1[0]);
        Assert.Equal(0x22222222u, pressing.CrcV2[0]);

        var hit = AccurateRipDatabase.BestMatch([pressing], [(0x11111111, 0)]);
        Assert.NotNull(hit);
        Assert.Null(AccurateRipDatabase.BestMatch([pressing], [(1, 2)]));
    }

    private static void WriteFrame(byte[] pcm, int offset, uint value)
    {
        pcm[offset] = (byte)value;
        pcm[offset + 1] = (byte)(value >> 8);
        pcm[offset + 2] = (byte)(value >> 16);
        pcm[offset + 3] = (byte)(value >> 24);
    }
}

public class RipVerificationTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ClaudeSoundtrackTests", Guid.NewGuid().ToString("N"));

    public RipVerificationTests() => Directory.CreateDirectory(_folder);
    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task ASecondReadThatAgreesIsRepeatable()
    {
        var drive = new FakeDrive(sectorsPerTrack: 2, tracks: 2, corruptSecondPass: false);
        var ripped = await RipAsync(drive);

        Assert.All(ripped, t => Assert.Equal(RipConfidence.Repeatable, t.Confidence));
        Assert.All(ripped, t => Assert.False(t.IsSilent));
        Assert.All(ripped, t => Assert.NotNull(t.AccurateRipCrc));
    }

    [Fact]
    public async Task ASecondReadThatDisagreesIsUnstableAndFailsReadiness()
    {
        var drive = new FakeDrive(sectorsPerTrack: 2, tracks: 2, corruptSecondPass: true);
        var ripped = await RipAsync(drive);

        Assert.All(ripped, t => Assert.Equal(RipConfidence.Unstable, t.Confidence));

        var project = new AlbumProject
        {
            AlbumTitle = "Score",
            AlbumArtist = "Jerry Goldsmith",
            Year = 1979,
            CoverArt = SampleAlbumWriter.SampleCover,
            CoverArtWidth = 1400,
            CoverArtHeight = 1400,
            OutputFolder = _folder
        };
        project.Tracks.AddRange(ripped);
        TrackFlattener.Flatten(project);
        new TaggingService().WriteAllTags(project);

        var report = new ReadinessChecker().Check(project);
        Assert.False(report.IsReady);
        Assert.Contains(report.Issues, i => i.Message.Contains("second time", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<List<SoundtrackTrack>> RipAsync(FakeDrive drive)
    {
        var tracks = new List<SoundtrackTrack>
        {
            new() { SourceDiscNumber = 1, SourceTrackNumber = 1, Title = "Main Titles", FlatTrackNumber = 1 },
            new() { SourceDiscNumber = 1, SourceTrackNumber = 2, Title = "End Credits", FlatTrackNumber = 2 }
        };
        var ripper = new CdRipService { QueryAccurateRip = false };
        await ripper.RipTracksAsync(drive, tracks, _folder, ["Main Titles.flac", "End Credits.flac"]);
        return tracks;
    }

    /// <summary>A drive whose sectors are a pure function of the LBA, unless the second pass is asked to lie.</summary>
    private sealed class FakeDrive : IOpticalDrive
    {
        private readonly int _sectorsPerTrack;
        private readonly int _tracks;
        private readonly bool _corruptSecondPass;
        private int _passes;

        public FakeDrive(int sectorsPerTrack, int tracks, bool corruptSecondPass)
        {
            _sectorsPerTrack = sectorsPerTrack;
            _tracks = tracks;
            _corruptSecondPass = corruptSecondPass;
        }

        public DriveInquiry Inquiry => new() { Vendor = "TEST", Product = "FAKE", Revision = "1" };

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<CdText?> ReadCdTextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<CdText?>(new CdText());

        public Task<TableOfContents> ReadTocAsync(CancellationToken cancellationToken = default)
        {
            var list = new List<TrackInfo>();
            for (var n = 1; n <= _tracks; n++)
            {
                list.Add(new TrackInfo
                {
                    Number = n,
                    StartLba = (n - 1) * _sectorsPerTrack,
                    SectorCount = _sectorsPerTrack,
                    Type = TrackType.Audio,
                    Control = TrackControl.None
                });
            }

            var toc = new TableOfContents
            {
                FirstTrackNumber = 1,
                LastTrackNumber = _tracks,
                LeadOutLba = _tracks * _sectorsPerTrack,
                Tracks = list.AsReadOnly()
            };
            return Task.FromResult(toc);
        }

        public Task<int> ReadSectorsAsync(
            long startLba, int count, Memory<byte> buffer, ReadOptions options, CancellationToken cancellationToken = default)
        {
            // The first full pass of the disc is the rip. Anything after it is the verify.
            var corrupt = _corruptSecondPass && _passes >= _tracks;
            for (var i = 0; i < count; i++)
            {
                var span = buffer.Slice(i * 2352, 2352).Span;
                var lba = (byte)((startLba + i) & 0xFF);
                span.Fill(corrupt ? (byte)(lba ^ 0x5A) : lba);
                span[0] = 1; // never digital silence
            }

            _passes++;
            return Task.FromResult(count);
        }
    }
}
