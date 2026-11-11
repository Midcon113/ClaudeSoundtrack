using ClaudeSoundtrack.Core.Services;

// Dry run: write a two-disc sample album and print the tags that landed on disk.
// No CD drive, no network, no upload.
var folder = args.Length > 0
    ? args[0]
    : Path.Combine(Environment.CurrentDirectory, "sample-album");

Console.WriteLine(SampleAlbumWriter.Write(folder));
