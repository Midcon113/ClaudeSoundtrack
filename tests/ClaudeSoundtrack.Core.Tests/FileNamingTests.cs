using ClaudeSoundtrack.Core.Services;
using Xunit;

namespace ClaudeSoundtrack.Core.Tests;

public class StripLeadingTrackNumberTests
{
    [Theory]
    [InlineData("01 - Main Titles", "Main Titles")]
    [InlineData("1 - Main Titles", "Main Titles")]
    [InlineData("01. Main Titles", "Main Titles")]
    [InlineData("01_Main Titles", "Main Titles")]
    [InlineData("(01) Main Titles", "Main Titles")]
    [InlineData("01) Main Titles", "Main Titles")]
    [InlineData("001 - Main Titles", "Main Titles")]
    [InlineData("07 – The Chase", "The Chase")]     // en dash
    [InlineData("07 — The Chase", "The Chase")]     // em dash
    [InlineData("12  Double Spaced", "Double Spaced")]
    public void StripsLeadingTrackNumbers(string input, string expected)
    {
        Assert.Equal(expected, FileNaming.StripLeadingTrackNumber(input));
    }

    [Theory]
    [InlineData("1-01 The Chase", "The Chase")]
    [InlineData("2-14 - End Credits", "End Credits")]
    [InlineData("3.07. Finale", "Finale")]
    public void StripsDiscQualifiedTrackNumbers(string input, string expected)
    {
        Assert.Equal(expected, FileNaming.StripLeadingTrackNumber(input));
    }

    /// <summary>
    /// The failure mode that matters most: titles that legitimately begin with a
    /// number must survive untouched. Stripping these would silently corrupt the
    /// tag of a track nobody would think to check.
    /// </summary>
    [Theory]
    [InlineData("2001: A Space Odyssey")]
    [InlineData("13 Ghosts")]
    [InlineData("633 Squadron")]
    [InlineData("1941 Main Theme")]
    [InlineData("99 Red Balloons")]
    [InlineData("Main Titles")]
    public void LeavesTitlesThatGenuinelyStartWithNumbers(string input)
    {
        Assert.Equal(input, FileNaming.StripLeadingTrackNumber(input));
    }

    [Fact]
    public void NeverStripsATitleAwayEntirely()
    {
        // A track genuinely titled "01" keeps its name rather than becoming empty.
        Assert.Equal("01", FileNaming.StripLeadingTrackNumber("01"));
    }

    [Fact]
    public void HandlesNullAndEmpty()
    {
        Assert.Equal(string.Empty, FileNaming.StripLeadingTrackNumber(null));
        Assert.Equal(string.Empty, FileNaming.StripLeadingTrackNumber("   "));
    }
}

public class SanitizeFileNameTests
{
    /// <summary>
    /// A colon in "Suite: Stingers And Act-Out Music" is the exact shape of
    /// character that kills a long rip partway through.
    /// </summary>
    [Fact]
    public void ReplacesColonRatherThanDroppingIt()
    {
        Assert.Equal("Suite - Stingers And Act-Out Music",
            FileNaming.SanitizeFileName("Suite: Stingers And Act-Out Music"));
    }

    [Theory]
    [InlineData("A/B", "A-B")]
    [InlineData("A\\B", "A-B")]
    [InlineData("A|B", "A-B")]
    [InlineData("What?", "What")]
    [InlineData("Star*", "Star")]
    [InlineData("\"Quoted\"", "'Quoted'")]
    [InlineData("<Angle>", "(Angle)")]
    public void ReplacesInvalidCharacters(string input, string expected)
    {
        Assert.Equal(expected, FileNaming.SanitizeFileName(input));
    }

    [Fact]
    public void TrimsTrailingDotsAndSpaces()
    {
        // Windows strips these itself, which would desync the path we recorded
        // from the file actually on disk.
        Assert.Equal("Finale", FileNaming.SanitizeFileName("Finale. "));
    }

    [Fact]
    public void EscapesReservedDeviceNames()
    {
        Assert.Equal("_CON", FileNaming.SanitizeFileName("CON"));
        Assert.Equal("_aux", FileNaming.SanitizeFileName("aux"));
    }

    [Fact]
    public void TruncatesVeryLongNames()
    {
        var result = FileNaming.SanitizeFileName(new string('x', 400));
        Assert.True(result.Length <= 120);
    }

    [Fact]
    public void NeverReturnsAnEmptyName()
    {
        Assert.Equal("Untitled", FileNaming.SanitizeFileName("???"));
        Assert.Equal("Untitled", FileNaming.SanitizeFileName(null));
    }
}

public class BuildUniqueFileNamesTests
{
    [Fact]
    public void RemovesTrackNumbersFromFileNames()
    {
        var names = FileNaming.BuildUniqueFileNames(["01 - Main Titles", "02 - The Chase"]);

        Assert.Equal(["Main Titles.flac", "The Chase.flac"], names);
    }

    /// <summary>
    /// Dropping the track number reintroduces a collision risk that numbering
    /// used to hide. Soundtracks repeat titles constantly.
    /// </summary>
    [Fact]
    public void DisambiguatesRepeatedTitles()
    {
        var names = FileNaming.BuildUniqueFileNames(["Source Music", "Source Music", "Source Music"]);

        Assert.Equal(["Source Music.flac", "Source Music (2).flac", "Source Music (3).flac"], names);
        Assert.Equal(3, names.Distinct().Count());
    }

    [Fact]
    public void DisambiguationIsCaseInsensitive()
    {
        // Windows would treat these as the same file.
        var names = FileNaming.BuildUniqueFileNames(["Main Title", "MAIN TITLE"]);

        Assert.Equal(2, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// Discs are ripped one at a time, so disc 2 must dodge the names disc 1
    /// already wrote or it will overwrite them.
    /// </summary>
    [Fact]
    public void AvoidsNamesAlreadyUsedByAnEarlierDisc()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var disc1 = FileNaming.BuildUniqueFileNames(["Main Title", "The Chase"], used);
        var disc2 = FileNaming.BuildUniqueFileNames(["Main Title", "Finale"], used);

        Assert.Equal("Main Title.flac", disc1[0]);
        Assert.Equal("Main Title (2).flac", disc2[0]);
        Assert.Empty(disc1.Intersect(disc2));
    }

    [Fact]
    public void HandlesATitleThatAlreadyLooksLikeADisambiguatedOne()
    {
        var names = FileNaming.BuildUniqueFileNames(["Cue (2)", "Cue", "Cue"]);

        Assert.Equal(3, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}

public class BuildAlbumFolderNameTests
{
    [Fact]
    public void CombinesArtistTitleAndYear()
    {
        Assert.Equal("Jerry Goldsmith - Star Trek (1979)",
            FileNaming.BuildAlbumFolderName("Star Trek", "Jerry Goldsmith", 1979));
    }

    [Fact]
    public void OmitsMissingParts()
    {
        Assert.Equal("Star Trek", FileNaming.BuildAlbumFolderName("Star Trek", null, null));
    }

    [Fact]
    public void SanitizesTheResult()
    {
        var name = FileNaming.BuildAlbumFolderName("Alien: Director's Cut", "Jerry Goldsmith", 2003);

        Assert.DoesNotContain(':', name);
        Assert.Equal("Jerry Goldsmith - Alien - Director's Cut (2003)", name);
    }
}
