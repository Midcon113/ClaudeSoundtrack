using System.Buffers.Binary;

namespace ClaudeSoundtrack.Core.Services;

/// <summary>
/// Reads pixel dimensions straight out of an image file's header.
///
/// Resolution is the deciding factor when choosing cover art, so it has to be
/// known for every candidate. Decoding each one into a bitmap to find out would
/// mean pulling a UI imaging stack into the core library and paying full decode
/// cost for images the user will never pick. Parsing the header reads a few
/// dozen bytes instead.
/// </summary>
public static class ImageDimensions
{
    /// <summary>Image formats this can recognise from a header.</summary>
    public enum Format
    {
        Unknown,
        Jpeg,
        Png,
        Gif,
        Bmp,
        WebP
    }

    /// <summary>
    /// Identifies an image format from its magic bytes.
    ///
    /// Used to decide whether cover art needs converting before it is embedded:
    /// a FLAC picture block may declare any MIME type, but the things that read
    /// it - YouTube Music included - generally only handle JPEG and PNG.
    /// </summary>
    public static Format DetectFormat(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16) return Format.Unknown;

        if (IsPng(data)) return Format.Png;
        if (IsGif(data)) return Format.Gif;
        if (IsBmp(data)) return Format.Bmp;
        if (IsWebp(data)) return Format.WebP;
        if (IsJpeg(data)) return Format.Jpeg;

        return Format.Unknown;
    }

    /// <summary>The MIME type to declare for a format in a FLAC picture block.</summary>
    public static string MimeTypeFor(Format format) => format switch
    {
        Format.Jpeg => "image/jpeg",
        Format.Png => "image/png",
        Format.Gif => "image/gif",
        Format.Bmp => "image/bmp",
        Format.WebP => "image/webp",
        _ => "application/octet-stream"
    };

    /// <summary>
    /// Whether players can be relied on to display this format from a tag.
    ///
    /// JPEG and PNG only. WebP embeds happily and then simply does not appear,
    /// which is worse than being rejected outright.
    /// </summary>
    public static bool IsWidelySupported(Format format) =>
        format is Format.Jpeg or Format.Png;

    /// <summary>
    /// Returns the pixel size of an encoded image, or null if the format is not
    /// recognised or the header is truncated.
    /// </summary>
    public static (int Width, int Height)? TryGet(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16) return null;

        if (IsPng(data)) return ReadPng(data);
        if (IsGif(data)) return ReadGif(data);
        if (IsBmp(data)) return ReadBmp(data);
        if (IsWebp(data)) return ReadWebp(data);
        if (IsJpeg(data)) return ReadJpeg(data);

        return null;
    }

    private static bool IsPng(ReadOnlySpan<byte> d) =>
        d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G';

    private static bool IsGif(ReadOnlySpan<byte> d) =>
        d[0] == 'G' && d[1] == 'I' && d[2] == 'F';

    private static bool IsBmp(ReadOnlySpan<byte> d) =>
        d[0] == 'B' && d[1] == 'M';

    private static bool IsJpeg(ReadOnlySpan<byte> d) =>
        d[0] == 0xFF && d[1] == 0xD8;

    private static bool IsWebp(ReadOnlySpan<byte> d) =>
        d.Length >= 16 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F'
        && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P';

    /// <summary>PNG stores width and height as big-endian ints in the IHDR chunk at a fixed offset.</summary>
    private static (int, int)? ReadPng(ReadOnlySpan<byte> d)
    {
        if (d.Length < 24) return null;
        var w = BinaryPrimitives.ReadInt32BigEndian(d.Slice(16, 4));
        var h = BinaryPrimitives.ReadInt32BigEndian(d.Slice(20, 4));
        return Valid(w, h);
    }

    /// <summary>GIF stores the logical screen size as little-endian shorts right after the signature.</summary>
    private static (int, int)? ReadGif(ReadOnlySpan<byte> d)
    {
        if (d.Length < 10) return null;
        int w = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(6, 2));
        int h = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(8, 2));
        return Valid(w, h);
    }

    /// <summary>BMP height is signed: a negative value means the rows are stored top-down.</summary>
    private static (int, int)? ReadBmp(ReadOnlySpan<byte> d)
    {
        if (d.Length < 26) return null;
        var w = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(18, 4));
        var h = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(22, 4));
        return Valid(w, Math.Abs(h));
    }

    /// <summary>
    /// WebP comes in three flavours - lossy (VP8), lossless (VP8L) and extended
    /// (VP8X) - each storing the size differently.
    /// </summary>
    private static (int, int)? ReadWebp(ReadOnlySpan<byte> d)
    {
        if (d.Length < 30) return null;

        var format = d.Slice(12, 4);

        if (format[3] == 'X' && d.Length >= 30)
        {
            // VP8X: 24-bit little-endian, stored as (size - 1).
            var w = 1 + (d[24] | (d[25] << 8) | (d[26] << 16));
            var h = 1 + (d[27] | (d[28] << 8) | (d[29] << 16));
            return Valid(w, h);
        }

        if (format[3] == 'L' && d.Length >= 25)
        {
            // VP8L: 14 bits each, packed across four bytes after the 0x2F marker.
            var bits = (uint)(d[21] | (d[22] << 8) | (d[23] << 16) | (d[24] << 24));
            var w = (int)((bits & 0x3FFF) + 1);
            var h = (int)(((bits >> 14) & 0x3FFF) + 1);
            return Valid(w, h);
        }

        if (format[3] == ' ' && d.Length >= 30)
        {
            // VP8: dimensions follow the 3-byte start code, 14 bits each.
            int w = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(26, 2)) & 0x3FFF;
            int h = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(28, 2)) & 0x3FFF;
            return Valid(w, h);
        }

        return null;
    }

    /// <summary>
    /// Walks JPEG segments to the start-of-frame marker, which is the only place
    /// the dimensions appear. Everything before it is metadata of varying length,
    /// so there is no fixed offset to jump to.
    /// </summary>
    private static (int, int)? ReadJpeg(ReadOnlySpan<byte> d)
    {
        var i = 2;
        while (i + 9 < d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }

            var marker = d[i + 1];

            // Padding and standalone markers carry no length field.
            if (marker == 0xFF) { i++; continue; }
            if (marker is 0x01 or >= 0xD0 and <= 0xD9) { i += 2; continue; }

            if (i + 3 >= d.Length) return null;
            var length = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(i + 2, 2));
            if (length < 2) return null;

            // SOF0-SOF15, excluding DHT (C4), JPG (C8) and DAC (CC), which are
            // not frame headers despite sitting in the same numeric range.
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                if (i + 9 >= d.Length) return null;
                int h = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(i + 5, 2));
                int w = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(i + 7, 2));
                return Valid(w, h);
            }

            i += 2 + length;
        }

        return null;
    }

    /// <summary>Rejects absurd sizes that indicate the header was misread.</summary>
    private static (int, int)? Valid(int width, int height) =>
        width > 0 && height > 0 && width <= 100_000 && height <= 100_000
            ? (width, height)
            : null;
}
