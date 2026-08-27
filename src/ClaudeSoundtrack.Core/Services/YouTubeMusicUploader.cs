using System.Net;
using System.Security.Cryptography;
using System.Text;
using ClaudeSoundtrack.Core.Models;

namespace ClaudeSoundtrack.Core.Services;

/// <summary>Progress for one file being uploaded.</summary>
/// <param name="FileIndex">Zero-based position in the queue.</param>
/// <param name="FileCount">How many files the upload covers.</param>
/// <param name="FileName">Name of the file currently going up.</param>
/// <param name="Status">What is happening to it.</param>
public readonly record struct UploadProgress(int FileIndex, int FileCount, string FileName, string Status)
{
    /// <summary>Completion across the whole queue, 0-100.</summary>
    public double Percent => FileCount <= 0 ? 0 : FileIndex * 100.0 / FileCount;
}

/// <summary>What happened to one file.</summary>
/// <param name="FilePath">The file that was attempted.</param>
/// <param name="Succeeded">True when YouTube accepted it.</param>
/// <param name="Detail">Failure reason, or null on success.</param>
public sealed record UploadResult(string FilePath, bool Succeeded, string? Detail);

/// <summary>
/// Uploads tracks straight to YouTube Music, without a browser.
///
/// **This uses an unofficial endpoint.** YouTube Music has no public upload API;
/// this is the same request the web player makes, driven directly. It works, it
/// is what every other uploader tool does, and it can be changed or withdrawn by
/// Google without notice. When it breaks, the browser route still exists.
///
/// Authentication reuses the browser's own session cookies. Google's endpoints
/// authenticate that session with a SAPISIDHASH header - a per-request SHA-1 of
/// the timestamp, the SAPISID cookie and the origin - rather than a bearer token,
/// so the hash has to be recomputed for every request.
///
/// Cookies are held only for the life of this object and are never written to
/// disk, logged, or included in the error text shown to the user.
/// </summary>
public sealed class YouTubeMusicUploader : IDisposable
{
    private const string Origin = "https://music.youtube.com";
    private const string UploadEndpoint = "https://upload.youtube.com/upload/usermusic/http?authuser=0";

    /// <summary>YouTube Music rejects anything larger than this per file.</summary>
    public const long MaxFileBytes = 300L * 1024 * 1024;

    /// <summary>Formats YouTube Music accepts for uploads.</summary>
    private static readonly string[] AcceptedExtensions = [".flac", ".mp3", ".m4a", ".ogg", ".wma"];

    private readonly HttpClient _http;
    private readonly IReadOnlyDictionary<string, string> _cookies;
    private readonly string _sapisid;

    /// <param name="cookies">Session cookies from <see cref="FirefoxCookieStore"/>.</param>
    public YouTubeMusicUploader(IReadOnlyDictionary<string, string> cookies)
    {
        ArgumentNullException.ThrowIfNull(cookies);

        _cookies = cookies;

        // __Secure-3PAPISID is the partitioned equivalent and is what a modern
        // session actually carries; plain SAPISID is the older name.
        _sapisid = Lookup("SAPISID") ?? Lookup("__Secure-3PAPISID")
            ?? throw new InvalidOperationException(
                "The Firefox session has no SAPISID cookie, so it cannot be authenticated. " +
                "Sign in to music.youtube.com in Firefox and try again.");

        _http = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        string? Lookup(string name) =>
            cookies.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value : null;
    }

    /// <summary>Rejects files YouTube Music will not take, before anything is sent.</summary>
    /// <returns>A reason string, or null when the file is acceptable.</returns>
    public static string? Validate(string filePath)
    {
        if (!File.Exists(filePath)) return "file is missing";

        var extension = Path.GetExtension(filePath);
        if (!AcceptedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return $"{extension} is not a format YouTube Music accepts";

        var size = new FileInfo(filePath).Length;
        if (size == 0) return "file is empty";
        if (size > MaxFileBytes) return $"file is {size / 1024 / 1024}MB, over the 300MB limit";

        return null;
    }

    /// <summary>
    /// Uploads every track in the album, one at a time.
    ///
    /// Sequential on purpose: parallel uploads to this endpoint tend to be
    /// throttled, and one failure part-way through a set is far easier to reason
    /// about when the order is known.
    /// </summary>
    public async Task<IReadOnlyList<UploadResult>> UploadAlbumAsync(
        AlbumProject project,
        IProgress<UploadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        var files = project.Tracks
            .OrderBy(t => t.FlatTrackNumber)
            .Select(t => t.FilePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToList();

        return await UploadFilesAsync(files, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Uploads the given files in order.</summary>
    public async Task<IReadOnlyList<UploadResult>> UploadFilesAsync(
        IReadOnlyList<string> filePaths,
        IProgress<UploadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<UploadResult>();

        for (var i = 0; i < filePaths.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = filePaths[i];
            var name = Path.GetFileName(path);

            var problem = Validate(path);
            if (problem is not null)
            {
                results.Add(new UploadResult(path, false, problem));
                progress?.Report(new UploadProgress(i + 1, filePaths.Count, name, $"skipped - {problem}"));
                continue;
            }

            progress?.Report(new UploadProgress(i, filePaths.Count, name, "uploading"));

            try
            {
                await UploadOneAsync(path, cancellationToken).ConfigureAwait(false);
                results.Add(new UploadResult(path, true, null));
                progress?.Report(new UploadProgress(i + 1, filePaths.Count, name, "done"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(new UploadResult(path, false, ex.Message));
                progress?.Report(new UploadProgress(i + 1, filePaths.Count, name, $"failed - {ex.Message}"));
            }
        }

        return results;
    }

    /// <summary>
    /// Uploads one file using Google's resumable protocol: a start request that
    /// reserves a session and returns a URL, then the bytes.
    /// </summary>
    private async Task UploadOneAsync(string filePath, CancellationToken cancellationToken)
    {
        var info = new FileInfo(filePath);

        // --- 1. Reserve an upload session ---
        using var start = new HttpRequestMessage(HttpMethod.Post, UploadEndpoint);
        ApplyAuth(start);
        start.Headers.TryAddWithoutValidation("X-Goog-Upload-Command", "start");
        start.Headers.TryAddWithoutValidation("X-Goog-Upload-Header-Content-Length", info.Length.ToString());
        start.Headers.TryAddWithoutValidation("X-Goog-Upload-Protocol", "resumable");

        start.Content = new StringContent(
            $"filename={Uri.EscapeDataString(info.Name)}",
            Encoding.UTF8,
            "application/x-www-form-urlencoded");

        using var startResponse = await _http.SendAsync(start, cancellationToken).ConfigureAwait(false);

        if (!startResponse.IsSuccessStatusCode)
            throw new InvalidOperationException(DescribeFailure(startResponse));

        if (!startResponse.Headers.TryGetValues("X-Goog-Upload-URL", out var urls))
            throw new InvalidOperationException(
                "YouTube did not return an upload URL. The session may have expired - " +
                "open music.youtube.com in Firefox, confirm you are signed in, and try again.");

        var uploadUrl = urls.First();

        // --- 2. Send the bytes ---
        await using var stream = File.OpenRead(filePath);

        using var upload = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
        ApplyAuth(upload);
        upload.Headers.TryAddWithoutValidation("X-Goog-Upload-Command", "upload, finalize");
        upload.Headers.TryAddWithoutValidation("X-Goog-Upload-Offset", "0");
        upload.Content = new StreamContent(stream);

        using var uploadResponse = await _http.SendAsync(upload, cancellationToken).ConfigureAwait(false);

        if (!uploadResponse.IsSuccessStatusCode)
            throw new InvalidOperationException(DescribeFailure(uploadResponse));
    }

    /// <summary>
    /// Turns a failed response into something a user can act on, without ever
    /// echoing back the request's credentials.
    /// </summary>
    private static string DescribeFailure(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            "YouTube rejected the session (HTTP " + (int)response.StatusCode + "). " +
            "Open music.youtube.com in Firefox, make sure you are signed in, then try again.",

        HttpStatusCode.RequestEntityTooLarge =>
            "The file is larger than YouTube Music accepts (300MB).",

        (HttpStatusCode)429 =>
            "YouTube is rate-limiting the upload. Wait a few minutes and try again.",

        _ => $"YouTube returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase})."
    };

    /// <summary>
    /// Adds the cookie and SAPISIDHASH headers a YouTube Music request needs.
    ///
    /// The hash is time-based and single-use, so it is recomputed per request
    /// rather than cached.
    /// </summary>
    private void ApplyAuth(HttpRequestMessage request)
    {
        var cookieHeader = string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}"));

        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Headers.TryAddWithoutValidation("Authorization", BuildSapisidHash());
        request.Headers.TryAddWithoutValidation("X-Goog-AuthUser", "0");
        request.Headers.TryAddWithoutValidation("Origin", Origin);
        request.Headers.TryAddWithoutValidation("X-Origin", Origin);
        request.Headers.TryAddWithoutValidation("Referer", Origin + "/");

        // Google's endpoints behave differently for clients they do not recognise
        // as a browser, so this presents itself as one.
        request.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:130.0) Gecko/20100101 Firefox/130.0");
    }

    /// <summary>
    /// Builds the SAPISIDHASH authorization value:
    /// <c>SAPISIDHASH {unixSeconds}_{sha1(unixSeconds + " " + SAPISID + " " + origin)}</c>.
    /// </summary>
    private string BuildSapisidHash()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = $"{timestamp} {_sapisid} {Origin}";
        var digest = SHA1.HashData(Encoding.UTF8.GetBytes(payload));
        var hex = Convert.ToHexStringLower(digest);

        return $"SAPISIDHASH {timestamp}_{hex}";
    }

    public void Dispose() => _http.Dispose();
}
