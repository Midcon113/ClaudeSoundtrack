using System.IO;
using System.Windows;
using System.Windows.Media;
using ClaudeSoundtrack.Core.Models;
using ClaudeSoundtrack.Core.Services;

namespace ClaudeSoundtrack.App.Dialogs;

/// <summary>
/// Uploads the finished album straight to YouTube Music.
///
/// The consent panel is not boilerplate. This reads the user's browser session
/// cookies and sends their music to a third party, and both of those deserve to
/// be stated plainly and agreed to before anything happens - the upload only
/// starts when the button is pressed.
/// </summary>
public partial class DirectUploadWindow : PanelWindow
{
    private readonly AlbumProject _project;
    private readonly List<string> _files;

    private IReadOnlyDictionary<string, string>? _cookies;
    private CancellationTokenSource? _cancellation;

    /// <summary>True when the user asked to fall back to the browser route.</summary>
    public bool UseBrowserInstead { get; private set; }

    public DirectUploadWindow(AlbumProject project)
    {
        InitializeComponent();

        _project = project;
        _files = project.Tracks
            .OrderBy(t => t.FlatTrackNumber)
            .Select(t => t.FilePath)
            .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
            .Select(p => p!)
            .ToList();

        AlbumText.Text = $"{project.AlbumTitle} - {_files.Count} tracks";
        BuildManifest();

        Loaded += (_, _) => CheckSession();
    }

    /// <summary>Spells out exactly what is about to leave the machine.</summary>
    private void BuildManifest()
    {
        long bytes = 0;
        var rejected = new List<string>();

        foreach (var file in _files)
        {
            bytes += new FileInfo(file).Length;

            var problem = YouTubeMusicUploader.Validate(file);
            if (problem is not null) rejected.Add($"{Path.GetFileName(file)} - {problem}");
        }

        var lines = new List<string>
        {
            $"{_files.Count} FLAC files, {bytes / 1024.0 / 1024.0:F0} MB",
            $"from {_project.OutputFolder}",
            "to your YouTube Music library (private uploads, not public)"
        };

        if (rejected.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"{rejected.Count} file(s) will be skipped:");
            lines.AddRange(rejected.Take(5).Select(r => "  " + r));
        }

        ManifestText.Text = string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Looks for a usable YouTube session in the local Firefox profile.
    ///
    /// Only whether the cookies exist is ever surfaced - never their values.
    /// </summary>
    private void CheckSession()
    {
        var result = new FirefoxCookieStore().Read();

        if (result.IsUsable)
        {
            _cookies = result.Cookies;
            SetLamp((Color)FindResource("LampGreenColor"));
            SessionText.Text = "SIGNED IN";
            SessionDetailText.Text = $"Using the YouTube session from {Path.GetFileName(result.ProfilePath)}";
            UploadButton.IsEnabled = _files.Count > 0;
            StatusText.Text = "Ready when you are.";
            return;
        }

        SetLamp((Color)FindResource("LampRedColor"));
        SessionText.Text = "NOT SIGNED IN";

        SessionDetailText.Text = result.Problem
            ?? $"Firefox has no YouTube session (missing {string.Join(", ", result.Missing)}). " +
               "Open music.youtube.com in Firefox, sign in, then reopen this window.";

        UploadButton.IsEnabled = false;
        StatusText.Text = "Sign in to YouTube Music in Firefox, or use the browser route.";
    }

    private void SetLamp(Color colour)
    {
        SessionLamp.Fill = new SolidColorBrush(colour);
        SessionLamp.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = colour,
            BlurRadius = 16,
            ShadowDepth = 0,
            Opacity = 0.9
        };
    }

    private async void UploadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cookies is null) return;

        ConsentPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        UploadButton.IsEnabled = false;
        BrowserButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;

        _cancellation = new CancellationTokenSource();
        UiState.Current.IsWorking = true;

        var progress = new Progress<UploadProgress>(p =>
        {
            CurrentFileText.Text = p.FileName;
            CountText.Text = $"{p.FileIndex} / {p.FileCount}";
            UploadProgressBar.Value = p.Percent;

            if (p.Status != "uploading") AppendLog($"  {p.FileName}  {p.Status}");
        });

        try
        {
            using var uploader = new YouTubeMusicUploader(_cookies);

            AppendLog($"Uploading {_files.Count} track(s) to YouTube Music...");

            // Off the UI thread: this is minutes of network I/O.
            var token = _cancellation.Token;
            var results = await Task.Run(
                () => uploader.UploadFilesAsync(_files, progress, token), token);

            var ok = results.Count(r => r.Succeeded);
            var failed = results.Count - ok;

            UploadProgressBar.Value = 100;
            AppendLog(string.Empty);
            AppendLog(failed == 0
                ? $"All {ok} track(s) uploaded."
                : $"{ok} uploaded, {failed} failed.");

            if (failed == 0)
            {
                StatusText.Text = "Upload complete. The album appears under Library, Albums in a few minutes.";
                UploadButton.Content = "Done";
                UploadButton.IsEnabled = true;
                UploadButton.Click -= UploadButton_Click;
                UploadButton.Click += (_, _) => DialogResult = true;
            }
            else
            {
                StatusText.Text = "Some tracks failed. The browser route is still available.";
                BrowserButton.IsEnabled = true;
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("Cancelled.");
            StatusText.Text = "Upload cancelled.";
            BrowserButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            AppendLog($"Upload failed: {ex.Message}");
            StatusText.Text = "Upload failed. Try the browser route.";
            BrowserButton.IsEnabled = true;
        }
        finally
        {
            UiState.Current.IsWorking = false;
            CancelButton.Visibility = Visibility.Collapsed;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private void AppendLog(string line)
    {
        LogText.Text += (LogText.Text.Length > 0 ? "\n" : "") + line;
        LogScroller.ScrollToEnd();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = "Cancelling after the current track...";
    }

    private void BrowserButton_Click(object sender, RoutedEventArgs e)
    {
        UseBrowserInstead = true;
        DialogResult = false;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        DialogResult = false;
    }
}
