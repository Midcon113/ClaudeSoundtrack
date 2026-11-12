using System.Buffers.Binary;
using System.Text;

namespace ClaudeSoundtrack.Core.Services;

/// <summary>
/// Reads Vorbis comments out of a FLAC file.
///
/// ATL parses DISCNUMBER "2/3" back into the integer 2, so a round-trip through
/// ATL cannot see the form YouTube Music mis-sorts. The bytes have to be read.
/// </summary>
public sealed class VorbisComments
{
    public VorbisComments(IReadOnlyList<(string Key, string Value)> fields)
    {
        Fields = fields;
    }

    public IReadOnlyList<(string Key, string Value)> Fields { get; }

    public IEnumerable<string> Values(string key) =>
        Fields.Where(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Select(f => f.Value);

    /// <summary>The comment block, or null when the file is not a FLAC or has none.</summary>
    public static VorbisComments? TryRead(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> marker = stackalloc byte[4];
            if (stream.Read(marker) != 4 || marker[0] != 'f' || marker[1] != 'L' || marker[2] != 'a' || marker[3] != 'C')
                return null;

            Span<byte> header = stackalloc byte[4];
            while (stream.Read(header) == 4)
            {
                var last = (header[0] & 0x80) != 0;
                var type = header[0] & 0x7F;
                var length = (header[1] << 16) | (header[2] << 8) | header[3];
                if (length < 0 || length > 16 * 1024 * 1024) return null;

                var body = new byte[length];
                if (stream.Read(body) != length) return null;

                if (type == 4) return ParseBlock(body);
                if (last) break;
            }
        }
        catch (IOException)
        {
            return null;
        }

        return null;
    }

    private static VorbisComments ParseBlock(byte[] body)
    {
        var fields = new List<(string, string)>();
        var offset = 0;
        if (!TryReadU32(body, ref offset, out var vendorLength)) return new VorbisComments(fields);
        if (vendorLength > body.Length - offset) return new VorbisComments(fields);
        offset += (int)vendorLength;

        if (!TryReadU32(body, ref offset, out var count)) return new VorbisComments(fields);
        for (var i = 0; i < count; i++)
        {
            if (!TryReadU32(body, ref offset, out var fieldLength)) break;
            if (fieldLength > body.Length - offset) break;
            var text = Encoding.UTF8.GetString(body, offset, (int)fieldLength);
            offset += (int)fieldLength;

            var split = text.IndexOf('=');
            if (split <= 0) continue;
            fields.Add((text[..split], text[(split + 1)..]));
        }

        return new VorbisComments(fields);
    }

    private static bool TryReadU32(byte[] body, ref int offset, out uint value)
    {
        if (offset + 4 > body.Length)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset, 4));
        offset += 4;
        return true;
    }
}
