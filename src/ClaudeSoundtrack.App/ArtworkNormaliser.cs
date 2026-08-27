using System.IO;
using System.Windows.Media.Imaging;
using ClaudeSoundtrack.Core.Services;

namespace ClaudeSoundtrack.App;

/// <summary>
/// Converts cover art into a format that will actually be displayed.
///
/// A FLAC picture block can declare any MIME type, and tagging libraries will
/// happily embed a WebP. The problem is at the other end: YouTube Music, and most
/// players, only handle JPEG and PNG. A WebP cover embeds without complaint,
/// passes every check, uploads, and then simply does not appear - which is worse
/// than being rejected, because nothing anywhere says why.
///
/// Often the best scan available for a limited pressing is a WebP, so refusing it
/// is not the answer. It is converted instead.
///
/// Decoding uses WIC through WPF, which handles WebP natively on Windows 11.
/// Where the codec is missing the original is returned untouched and the caller
/// is told, rather than silently producing something broken.
/// </summary>
public static class ArtworkNormaliser
{
    /// <summary>
    /// Quality for converted JPEGs. High enough that the conversion is invisible
    /// on album art, low enough that a 3000x3000 cover is not tens of megabytes -
    /// which matters when it is embedded into every track of an 88-track set.
    /// </summary>
    private const int JpegQuality = 92;

    /// <summary>The outcome of normalising a piece of artwork.</summary>
    /// <param name="Data">Bytes to embed - converted, or the original when no change was needed.</param>
    /// <param name="Format">The format <paramref name="Data"/> is now in.</param>
    /// <param name="Width">Pixel width.</param>
    /// <param name="Height">Pixel height.</param>
    /// <param name="WasConverted">True when the bytes differ from what came in.</param>
    /// <param name="Problem">Set when conversion was needed but could not be done.</param>
    public readonly record struct Result(
        byte[] Data,
        ImageDimensions.Format Format,
        int Width,
        int Height,
        bool WasConverted,
        string? Problem)
    {
        /// <summary>True when the result is safe to embed and expect to see.</summary>
        public bool IsDisplayable => ImageDimensions.IsWidelySupported(Format);
    }

    /// <summary>
    /// Returns artwork in a format players will display, converting if necessary.
    /// </summary>
    public static Result Normalise(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var format = ImageDimensions.DetectFormat(data);
        var size = ImageDimensions.TryGet(data);

        // Already fine - do not re-encode, which would only lose quality.
        if (ImageDimensions.IsWidelySupported(format))
        {
            return new Result(data, format, size?.Width ?? 0, size?.Height ?? 0, false, null);
        }

        try
        {
            using var input = new MemoryStream(data);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];

            var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(frame));

            using var output = new MemoryStream();
            encoder.Save(output);

            return new Result(
                output.ToArray(),
                ImageDimensions.Format.Jpeg,
                frame.PixelWidth,
                frame.PixelHeight,
                WasConverted: true,
                Problem: null);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or IOException)
        {
            // No codec for this format on this machine. Hand back the original and
            // say so; the caller warns rather than pretending it worked.
            return new Result(
                data, format, size?.Width ?? 0, size?.Height ?? 0,
                WasConverted: false,
                Problem: $"{DescribeFormat(format)} could not be converted on this machine ({ex.Message}). " +
                         "YouTube Music will probably not show it - save the cover as JPEG or PNG instead.");
        }
    }

    /// <summary>A human-readable name for a format, for messages.</summary>
    public static string DescribeFormat(ImageDimensions.Format format) => format switch
    {
        ImageDimensions.Format.Jpeg => "JPEG",
        ImageDimensions.Format.Png => "PNG",
        ImageDimensions.Format.WebP => "WebP",
        ImageDimensions.Format.Gif => "GIF",
        ImageDimensions.Format.Bmp => "BMP",
        _ => "This image format"
    };
}
