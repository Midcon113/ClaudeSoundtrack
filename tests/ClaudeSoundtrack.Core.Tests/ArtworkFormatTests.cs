using ClaudeSoundtrack.Core.Models;
using ClaudeSoundtrack.Core.Services;
using Xunit;

namespace ClaudeSoundtrack.Core.Tests;

/// <summary>
/// Guards the cover-art format rule.
///
/// A WebP cover embedded into FLAC files passed every check the app had, uploaded
/// without error, and then simply did not appear in YouTube Music. Nothing in the
/// pipeline was wrong except the format, and nothing was looking at it.
/// </summary>
public class ArtworkFormatTests
{
    // Minimal but genuinely valid files of each format.
    private static byte[] Png => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static byte[] WebP => Convert.FromBase64String(
        "UklGRiIAAABXRUJQVlA4IBYAAAAwAQCdASoBAAEADsD+JaQAA3AAAAAA");

    private static byte[] Gif => Convert.FromBase64String(
        "R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

    private static byte[] Jpeg
    {
        get
        {
            // SOI + APP0/JFIF + SOF0 declaring 1x1, which is all the sniffer reads.
            var bytes = new byte[]
            {
                0xFF, 0xD8,                                     // SOI
                0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, // APP0 "JFIF"
                0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01,
                0x00, 0x00,
                0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x01, 0x00, // SOF0, 1x1
                0x01, 0x03, 0x01, 0x11, 0x00, 0x02, 0x11, 0x01,
                0x03, 0x11, 0x01,
                0xFF, 0xD9                                      // EOI
            };
            return bytes;
        }
    }

    [Fact]
    public void DetectsEachFormatFromItsHeader()
    {
        Assert.Equal(ImageDimensions.Format.Png, ImageDimensions.DetectFormat(Png));
        Assert.Equal(ImageDimensions.Format.WebP, ImageDimensions.DetectFormat(WebP));
        Assert.Equal(ImageDimensions.Format.Gif, ImageDimensions.DetectFormat(Gif));
        Assert.Equal(ImageDimensions.Format.Jpeg, ImageDimensions.DetectFormat(Jpeg));
    }

    [Fact]
    public void UnrecognisedDataIsNotMistakenForAnImage()
    {
        var noise = new byte[64];
        new Random(3).NextBytes(noise);

        Assert.Equal(ImageDimensions.Format.Unknown, ImageDimensions.DetectFormat(noise));
        Assert.Equal(ImageDimensions.Format.Unknown, ImageDimensions.DetectFormat([1, 2, 3]));
    }

    /// <summary>
    /// The distinction the whole fix rests on: WebP is a perfectly good image and
    /// a useless cover, because the far end will not render it.
    /// </summary>
    [Theory]
    [InlineData(ImageDimensions.Format.Jpeg, true)]
    [InlineData(ImageDimensions.Format.Png, true)]
    [InlineData(ImageDimensions.Format.WebP, false)]
    [InlineData(ImageDimensions.Format.Gif, false)]
    [InlineData(ImageDimensions.Format.Bmp, false)]
    [InlineData(ImageDimensions.Format.Unknown, false)]
    public void OnlyJpegAndPngCountAsDisplayable(ImageDimensions.Format format, bool expected)
    {
        Assert.Equal(expected, ImageDimensions.IsWidelySupported(format));
    }

    [Fact]
    public void MimeTypesMatchTheFormats()
    {
        Assert.Equal("image/jpeg", ImageDimensions.MimeTypeFor(ImageDimensions.Format.Jpeg));
        Assert.Equal("image/png", ImageDimensions.MimeTypeFor(ImageDimensions.Format.Png));
        Assert.Equal("image/webp", ImageDimensions.MimeTypeFor(ImageDimensions.Format.WebP));
    }

    /// <summary>Builds a minimal project carrying the given artwork.</summary>
    private static AlbumProject ProjectWithArt(byte[] art) => new()
    {
        AlbumTitle = "Score",
        AlbumArtist = "Composer",
        Year = 2000,
        CoverArt = art,
        CoverArtWidth = 1400,
        CoverArtHeight = 1400
    };

    [Fact]
    public void ReadinessCheckRejectsWebPCoverArt()
    {
        var report = new ReadinessChecker().Check(ProjectWithArt(WebP));

        Assert.Contains(report.Issues, i =>
            i.Severity == ReadinessSeverity.Error &&
            i.Message.Contains("WEBP", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReadinessCheckAcceptsJpegAndPngCoverArt()
    {
        foreach (var art in new[] { Jpeg, Png })
        {
            var report = new ReadinessChecker().Check(ProjectWithArt(art));

            Assert.DoesNotContain(report.Issues, i =>
                i.Message.Contains("only displays JPEG and PNG", StringComparison.OrdinalIgnoreCase));
        }
    }
}
