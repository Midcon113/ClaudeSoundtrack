using ClaudeSoundtrack.Core.Models;
using CUETools.Codecs;
using CUETools.Codecs.FLAKE;

namespace ClaudeSoundtrack.Core.Services;

/// <summary>
/// Writes a small two-disc FLAC album with the real tagger, and no CD drive.
///
/// This is the dry run: the files can be opened in a tag editor, and the same
/// readiness check the app runs after a rip is run against them.
/// </summary>
public static class SampleAlbumWriter
{
    /// <summary>
    /// A solid 1000×1000 PNG. Large enough to pass the cover check, and the same
    /// bytes on every track and in folder.png.
    /// </summary>
    public static byte[] SampleCover { get; } = SolidPng(1000, 0x8C, 0x6A, 0x3A);

    /// <summary>
    /// Writes the sample album into <paramref name="folder"/> and returns a
    /// plain-text report of the tags that landed on disk.
    /// </summary>
    public static string Write(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Directory.CreateDirectory(folder);

        var project = new AlbumProject
        {
            AlbumTitle = "The Complete Test Score",
            AlbumArtist = "Jerry Goldsmith",
            Year = 1979,
            DiscCount = 2,
            CoverArt = SampleCover,
            CoverArtWidth = 1000,
            CoverArtHeight = 1000,
            CoverArtSource = "sample",
            OutputFolder = folder,
            IdentityLocked = true
        };

        var discs = new (int Disc, string[] Titles)[]
        {
            (1, ["Main Titles", "The Docking", "Source Music"]),
            (2, ["Spock's Walk", "Source Music", "End Credits"])
        };

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (disc, titles) in discs)
        {
            var tracks = AlbumAssembler.BuildTracks(
                disc,
                Enumerable.Repeat(75, titles.Length).ToList(),
                titles,
                project.AlbumArtist);
            project.Tracks.AddRange(tracks);
            TrackFlattener.Flatten(project);
            var names = FileNaming.BuildUniqueFileNames(tracks.Select(t => t.Title), used);
            for (var i = 0; i < tracks.Count; i++)
            {
                var path = Path.Combine(folder, names[i]);
                WriteTone(path);
                tracks[i].FilePath = path;
                tracks[i].IsRipped = true;
            }

            project.RippedDiscs.Add(disc);
        }

        new TaggingService().WriteAllTags(project);
        var report = new ReadinessChecker().Check(project);

        var lines = new List<string>
        {
            $"Folder: {folder}",
            $"Readiness: {(report.IsReady ? "ready" : "not ready")} " +
            $"({report.ErrorCount} errors, {report.WarningCount} warnings, {report.FilesChecked} files)"
        };
        foreach (var issue in report.Issues)
            lines.Add($"  {issue.Severity}: {issue.Message}");

        lines.Add("");
        foreach (var track in project.Tracks.OrderBy(t => t.FlatTrackNumber))
        {
            var comments = VorbisComments.TryRead(track.FilePath!);
            string Field(string key) => comments?.Values(key).FirstOrDefault() ?? "(missing)";
            lines.Add(
                $"{track.FlatTrackNumber:D2}  disc {Field("DISCNUMBER")}  track {Field("TRACKNUMBER")}/{Field("TRACKTOTAL")}  " +
                $"{Field("ARTIST")} — {Field("ALBUM")} — {Field("TITLE")}");
        }

        var artPath = Directory.GetFiles(folder, "folder.*").FirstOrDefault();
        lines.Add("");
        lines.Add(artPath is null
            ? "folder art: missing"
            : $"folder art: {Path.GetFileName(artPath)} ({new FileInfo(artPath).Length} bytes)");

        var text = string.Join(Environment.NewLine, lines);
        File.WriteAllText(Path.Combine(folder, "sample-report.txt"), text);
        return text;
    }

    private static void WriteTone(string path)
    {
        var pcm = new AudioPCMConfig(16, 2, 44100);
        const int frames = 4410;
        var writer = new FlakeWriter(path, pcm) { FinalSampleCount = frames, CompressionLevel = 5 };
        var bytes = new byte[frames * 4];
        for (var i = 0; i < frames; i++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * 440 * i / 44100.0) * 8000);
            bytes[i * 4] = (byte)(sample & 0xFF);
            bytes[i * 4 + 1] = (byte)((sample >> 8) & 0xFF);
            bytes[i * 4 + 2] = bytes[i * 4];
            bytes[i * 4 + 3] = bytes[i * 4 + 1];
        }

        writer.Write(new AudioBuffer(pcm, bytes, frames));
        writer.Close();
    }

    /// <summary>A solid-colour PNG. Deflate makes it small even at 1000×1000.</summary>
    private static byte[] SolidPng(int size, byte r, byte g, byte b)
    {
        var raw = new byte[size * (1 + size * 3)];
        for (var y = 0; y < size; y++)
        {
            var row = y * (1 + size * 3);
            raw[row] = 0; // filter: none
            for (var x = 0; x < size; x++)
            {
                var i = row + 1 + x * 3;
                raw[i] = r;
                raw[i + 1] = g;
                raw[i + 2] = b;
            }
        }

        using var output = new MemoryStream();
        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        WriteChunk(output, "IHDR", Ihdr(size));
        WriteChunk(output, "IDAT", Zlib(raw));
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static byte[] Ihdr(int size)
    {
        var data = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(0, 4), size);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(4, 4), size);
        data[8] = 8;  // bit depth
        data[9] = 2;  // truecolour
        return data;
    }

    private static byte[] Zlib(byte[] raw)
    {
        using var output = new MemoryStream();
        output.WriteByte(0x78);
        output.WriteByte(0x01);
        using (var deflate = new System.IO.Compression.DeflateStream(output, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(raw);
        }

        var checksum = Adler32(raw);
        Span<byte> tail = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(tail, checksum);
        output.Write(tail);
        return output.ToArray();
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        var crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        void Feed(byte value)
        {
            crc ^= value;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        foreach (var value in type) Feed(value);
        foreach (var value in data) Feed(value);
        return ~crc;
    }
}
