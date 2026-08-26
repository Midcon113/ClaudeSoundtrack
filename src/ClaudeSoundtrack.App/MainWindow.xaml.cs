using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ClaudeSoundtrack.App.Dialogs;
using ClaudeSoundtrack.App.ViewModels;
using ClaudeSoundtrack.Core.Models;
using ClaudeSoundtrack.Core.Services;
using FoxRedbook;

// System.Windows.Shapes.Path (the vector shape) and System.IO.Path collide here,
// and this file wants the file-system one throughout.
using Path = System.IO.Path;

namespace ClaudeSoundtrack.App;

public partial class MainWindow : Window
{
    /// <summary>The five stages shown down the left rail.</summary>
    private enum Stage { Disc = 0, Identify = 1, Rip = 2, Artwork = 3, Verify = 4 }

    private readonly OpticalDriveService _drives = new();
    private readonly CdRipService _ripper = new();
    private readonly TaggingService _tagger = new();
    private readonly ReadinessChecker _checker = new();
    private readonly MetadataLookupService _lookup = new();

    private readonly AlbumProject _project = new();
    private readonly ObservableCollection<MatchRow> _matches = new();
    private readonly ObservableCollection<IssueRow> _issues = new();

    /// <summary>File name stems already written, so later discs cannot overwrite earlier ones.</summary>
    private readonly HashSet<string> _usedFileStems = new(StringComparer.OrdinalIgnoreCase);

    private DiscInfo? _disc;
    private ReleaseMatch? _selectedMatch;
    private ArtworkCandidate? _pendingArtwork;
    private CancellationTokenSource? _ripCancellation;
    private Stage _stage = Stage.Disc;

    /// <summary>Which physical disc is being ripped next. Increments after each disc.</summary>
    private int _currentDiscNumber = 1;

    public MainWindow()
    {
        InitializeComponent();

        MatchList.ItemsSource = _matches;
        IssueList.ItemsSource = _issues;

        LoadDrives();
        GoToStage(Stage.Disc);
    }

    // ================= Stage plumbing =================

    /// <summary>
    /// Shows one step and lights the rail to match.
    ///
    /// Lamp colours follow the panel convention set in the theme: green for a
    /// completed stage, amber for the one in progress, unlit for pending.
    /// </summary>
    private void GoToStage(Stage stage)
    {
        _stage = stage;

        StepDisc.Visibility = stage == Stage.Disc ? Visibility.Visible : Visibility.Collapsed;
        StepIdentify.Visibility = stage == Stage.Identify ? Visibility.Visible : Visibility.Collapsed;
        StepRip.Visibility = stage == Stage.Rip ? Visibility.Visible : Visibility.Collapsed;
        StepArtwork.Visibility = stage == Stage.Artwork ? Visibility.Visible : Visibility.Collapsed;
        StepVerify.Visibility = stage == Stage.Verify ? Visibility.Visible : Visibility.Collapsed;

        for (var i = 0; i < 5; i++)
        {
            var lamp = (Ellipse)FindName($"Lamp{i}")!;
            var label = (TextBlock)FindName($"Label{i}")!;

            if (i < (int)stage)
            {
                lamp.Fill = (Brush)FindResource("LampGreenBrush");
                lamp.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = (Color)FindResource("LampGreenColor"),
                    BlurRadius = 10,
                    ShadowDepth = 0,
                    Opacity = 0.8
                };
                label.Foreground = (Brush)FindResource("ParchmentDimBrush");
            }
            else if (i == (int)stage)
            {
                lamp.Fill = (Brush)FindResource("AmberBrush");
                lamp.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = (Color)FindResource("AmberColor"),
                    BlurRadius = 14,
                    ShadowDepth = 0,
                    Opacity = 1
                };
                label.Foreground = (Brush)FindResource("AmberBrightBrush");
            }
            else
            {
                lamp.Fill = new SolidColorBrush(Color.FromRgb(0x1A, 0x15, 0x12));
                lamp.Effect = null;
                label.Foreground = (Brush)FindResource("ParchmentFaintBrush");
            }
        }
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    /// <summary>Refreshes the header readout and the collected-tracks panel.</summary>
    private void RefreshSummary()
    {
        if (_project.Tracks.Count == 0 && string.IsNullOrEmpty(_project.AlbumTitle))
        {
            HeaderAlbumText.Text = "NO DISC";
            HeaderDetailText.Text = "Insert a disc to begin";
        }
        else
        {
            HeaderAlbumText.Text = string.IsNullOrWhiteSpace(_project.AlbumTitle)
                ? "UNIDENTIFIED"
                : _project.AlbumTitle.ToUpperInvariant();

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(_project.AlbumArtist)) parts.Add(_project.AlbumArtist);
            if (_project.Year is > 0) parts.Add(_project.Year.ToString()!);
            HeaderDetailText.Text = parts.Count > 0 ? string.Join("  ·  ", parts) : "Unidentified release";
        }

        CollectedTracksText.Text = _project.Tracks.Count == 1 ? "1 track" : $"{_project.Tracks.Count} tracks";
        CollectedDiscsText.Text = _project.RippedDiscs.Count switch
        {
            0 => "no discs ripped",
            1 => "1 disc ripped",
            var n => $"{n} discs ripped"
        };

        OutputFolderButton.IsEnabled = !string.IsNullOrEmpty(_project.OutputFolder)
                                       && Directory.Exists(_project.OutputFolder);
    }

    // ================= Step 0: disc =================

    private void LoadDrives()
    {
        DriveCombo.Items.Clear();

        var drives = _drives.GetDrives();
        foreach (var drive in drives)
        {
            DriveCombo.Items.Add(new ComboBoxItem { Content = drive.DisplayName, Tag = drive.DevicePath });
        }

        if (DriveCombo.Items.Count > 0)
        {
            DriveCombo.SelectedIndex = 0;
            ReadDiscButton.IsEnabled = true;
            SetStatus($"{DriveCombo.Items.Count} optical drive(s) found.");
        }
        else
        {
            ReadDiscButton.IsEnabled = false;
            SetStatus("No optical drive found. Connect a CD drive and press Refresh.");
        }
    }

    private void RefreshDrivesButton_Click(object sender, RoutedEventArgs e) => LoadDrives();

    private async void ReadDiscButton_Click(object sender, RoutedEventArgs e)
    {
        if (DriveCombo.SelectedItem is not ComboBoxItem { Tag: string devicePath }) return;

        ReadDiscButton.IsEnabled = false;
        SetStatus("Reading the table of contents...");
        DiscReadoutText.Text = "Spinning up...";

        try
        {
            _disc = await _drives.ReadDiscInfoAsync(devicePath);
            ShowDiscReadout(_disc);

            SetStatus("Disc read. Looking the release up...");
            GoToStage(Stage.Identify);
            await LookupAsync();
        }
        catch (MediaNotPresentException)
        {
            DiscReadoutText.Text = "No disc in the drive.";
            SetStatus("The drive is empty. Insert the first disc of the set and try again.");
        }
        catch (DriveNotReadyException)
        {
            DiscReadoutText.Text = "The drive is not ready.";
            SetStatus("The disc is still spinning up. Wait a moment and try again.");
        }
        catch (Exception ex)
        {
            DiscReadoutText.Text = "Could not read the disc.";
            SetStatus($"Read failed: {ex.Message}");
        }
        finally
        {
            ReadDiscButton.IsEnabled = true;
        }
    }

    /// <summary>Prints the TOC into the recessed readout, as a panel would.</summary>
    private void ShowDiscReadout(DiscInfo disc)
    {
        var sb = new StringBuilder();
        var audioTracks = MetadataLookupService.CountAudioTracks(disc.Toc);

        sb.AppendLine($"DISC {_currentDiscNumber}   {audioTracks} audio tracks");

        if (disc.Toc is not null)
        {
            var total = TimeSpan.FromSeconds((double)disc.Toc.TotalAudioSectors / CdConstants.SectorsPerSecond);
            sb.AppendLine($"RUNTIME  {total:hh\\:mm\\:ss}");
        }

        if (!string.IsNullOrWhiteSpace(disc.MusicBrainzDiscId))
            sb.AppendLine($"DISC ID  {disc.MusicBrainzDiscId}");

        if (!string.IsNullOrWhiteSpace(disc.CdText?.UpcEan))
            sb.AppendLine($"BARCODE  {disc.CdText.UpcEan}");

        if (!string.IsNullOrWhiteSpace(disc.CdText?.AlbumTitle))
            sb.AppendLine($"CD-TEXT  {disc.CdText.AlbumTitle}");

        sb.AppendLine();

        if (disc.Toc?.Tracks is not null)
        {
            foreach (var track in disc.Toc.Tracks.Where(t => t.Type != TrackType.Data))
            {
                var duration = TimeSpan.FromSeconds((double)track.SectorCount / CdConstants.SectorsPerSecond);
                var title = disc.CdText?.Tracks?.FirstOrDefault(t => t.Number == track.Number)?.Title;
                sb.AppendLine($"  {track.Number,2}   {duration:mm\\:ss}   {title}".TrimEnd());
            }
        }

        DiscReadoutText.Text = sb.ToString();
    }

    // ================= Step 1: identify =================

    private async Task LookupAsync()
    {
        if (_disc is null) return;

        _matches.Clear();
        SetStatus("Searching MusicBrainz and Discogs...");

        var discTrackCount = MetadataLookupService.CountAudioTracks(_disc.Toc);

        // On disc 2 and later the album is already known, so the title we have is
        // a far better search seed than anything on the disc itself.
        var hint = string.IsNullOrWhiteSpace(_project.AlbumTitle) ? null : _project.AlbumTitle;

        try
        {
            var results = await _lookup.LookupAsync(_disc, hint);

            foreach (var match in results) _matches.Add(new MatchRow(match, discTrackCount));

            if (_matches.Count > 0)
            {
                MatchList.SelectedIndex = 0;
                var verified = _matches.Count(m => m.Match.IsTocVerified);
                SetStatus(verified > 0
                    ? $"{_matches.Count} match(es); {verified} verified against this disc."
                    : $"{_matches.Count} match(es), none verified against this disc. Check the track count before ripping.");
            }
            else
            {
                SetStatus("Nothing found. Type the album title above and press Search, or fill the fields in by hand.");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Lookup failed: {ex.Message}. Enter the album details by hand.");
        }

        // Whatever happened, the disc's own track count is known, so the fields
        // can be filled in manually and the rip can still go ahead.
        if (string.IsNullOrWhiteSpace(AlbumTitleBox.Text) && _disc.CdText?.AlbumTitle is { } cdTitle)
            AlbumTitleBox.Text = cdTitle;

        RefreshSummary();
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e) => await RunSearchAsync();

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await RunSearchAsync();
    }

    private async Task RunSearchAsync()
    {
        var query = SearchBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query)) return;

        SearchButton.IsEnabled = false;
        SetStatus($"Searching for \"{query}\"...");

        try
        {
            var discTrackCount = _disc is null ? 0 : MetadataLookupService.CountAudioTracks(_disc.Toc);
            var results = await _lookup.SearchAsync(query, discTrackCount);

            _matches.Clear();
            foreach (var match in results) _matches.Add(new MatchRow(match, discTrackCount));

            if (_matches.Count > 0) MatchList.SelectedIndex = 0;
            SetStatus(_matches.Count > 0 ? $"{_matches.Count} match(es)." : "No matches. Fill the fields in by hand.");
        }
        catch (Exception ex)
        {
            SetStatus($"Search failed: {ex.Message}");
        }
        finally
        {
            SearchButton.IsEnabled = true;
        }
    }

    private void MatchList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MatchList.SelectedItem is not MatchRow row) return;

        _selectedMatch = row.Match;

        AlbumTitleBox.Text = row.Match.Title;
        AlbumArtistBox.Text = row.Match.Artist;
        YearBox.Text = row.Match.Year?.ToString() ?? string.Empty;

        SetStatus(row.Match.IsTocVerified
            ? $"\"{row.Match.Title}\" matches this disc's track count."
            : $"\"{row.Match.Title}\" was not verified against this disc - check the track count carefully.");
    }

    // ================= Step 2: rip =================

    private async void StartRipButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disc?.Toc is null)
        {
            SetStatus("Read a disc first.");
            return;
        }

        if (string.IsNullOrWhiteSpace(AlbumTitleBox.Text))
        {
            MessageBox.Show(this, "The album needs a title before it can be ripped.",
                "ClaudeSoundtrack", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Album fields are locked in from the boxes, not the match, so hand edits count.
        _project.AlbumTitle = AlbumTitleBox.Text.Trim();
        _project.AlbumArtist = AlbumArtistBox.Text.Trim();
        _project.Year = int.TryParse(YearBox.Text?.Trim(), out var year) && year > 0 ? year : null;

        if (_selectedMatch is not null)
        {
            _project.Label ??= _selectedMatch.Label;
            _project.CatalogNumber ??= _selectedMatch.CatalogNumber;
        }

        if (!EnsureOutputFolder()) return;

        GoToStage(Stage.Rip);
        await RipCurrentDiscAsync();
    }

    /// <summary>
    /// Creates the album folder under the user's Music library.
    ///
    /// Only created once per album; later discs write into the same folder, which
    /// is what makes the set land as one album.
    /// </summary>
    private bool EnsureOutputFolder()
    {
        if (!string.IsNullOrEmpty(_project.OutputFolder) && Directory.Exists(_project.OutputFolder)) return true;

        try
        {
            var musicRoot = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            var folderName = FileNaming.BuildAlbumFolderName(_project.AlbumTitle, _project.AlbumArtist, _project.Year);
            var path = Path.Combine(musicRoot, folderName);

            // An existing folder with FLACs in it is almost always a previous run
            // of this same album. Overwriting silently once cost a finished rip.
            if (Directory.Exists(path) && Directory.EnumerateFiles(path, "*.flac").Any())
            {
                var answer = MessageBox.Show(this,
                    $"This folder already exists and contains FLAC files:\n\n{path}\n\n" +
                    "Continue and write into it anyway? Files with the same name will be replaced.",
                    "ClaudeSoundtrack", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (answer != MessageBoxResult.Yes) return false;
            }

            Directory.CreateDirectory(path);
            _project.OutputFolder = path;
            RefreshSummary();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not create the album folder:\n\n{ex.Message}",
                "ClaudeSoundtrack", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private async Task RipCurrentDiscAsync()
    {
        if (_disc?.Toc is null || DriveCombo.SelectedItem is not ComboBoxItem { Tag: string devicePath }) return;

        RipHeadingText.Text = $"Ripping disc {_currentDiscNumber}";
        RipLogText.Text = string.Empty;
        CancelRipButton.IsEnabled = true;
        NextDiscButton.Visibility = Visibility.Collapsed;
        ToArtworkButton.Visibility = Visibility.Collapsed;

        var newTracks = BuildTracksForCurrentDisc();
        if (newTracks.Count == 0)
        {
            AppendRipLog("This disc has no audio tracks.");
            return;
        }

        // Append first, then flatten, so numbering accounts for every disc so far.
        _project.Tracks.AddRange(newTracks);
        TrackFlattener.Flatten(_project);

        var fileNames = FileNaming.BuildUniqueFileNames(
            newTracks.Select(t => t.Title), _usedFileStems);

        _ripCancellation = new CancellationTokenSource();
        var progress = new Progress<RipTrackProgress>(OnRipProgress);

        try
        {
            AppendRipLog($"Writing to {_project.OutputFolder}");
            AppendRipLog($"{newTracks.Count} tracks queued.");

            await _ripper.RipTracksAsync(
                devicePath, newTracks, _project.OutputFolder!, fileNames, progress, _ripCancellation.Token);

            _project.RippedDiscs.Add(_currentDiscNumber);
            _project.DiscCount = _project.RippedDiscs.Count;

            var errored = newTracks.Count(t => t.HadReadErrors);
            AppendRipLog(errored == 0
                ? $"Disc {_currentDiscNumber} complete, no read errors."
                : $"Disc {_currentDiscNumber} complete, {errored} track(s) had read errors.");

            RipSubText.Text = $"Disc {_currentDiscNumber} is done. Insert the next disc, or continue to artwork.";
            NextDiscButton.Visibility = Visibility.Visible;
            ToArtworkButton.Visibility = Visibility.Visible;
            CancelRipButton.IsEnabled = false;
            SetStatus($"Disc {_currentDiscNumber} ripped: {newTracks.Count} tracks.");
        }
        catch (OperationCanceledException)
        {
            // Drop the tracks this disc contributed; the files were removed with them.
            foreach (var track in newTracks) _project.Tracks.Remove(track);
            TrackFlattener.Flatten(_project);

            AppendRipLog("Rip cancelled.");
            SetStatus("Rip cancelled.");
            NextDiscButton.Visibility = Visibility.Visible;
            ToArtworkButton.Visibility = _project.Tracks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            AppendRipLog($"Rip failed: {ex.Message}");
            SetStatus($"Rip failed: {ex.Message}");
            NextDiscButton.Visibility = Visibility.Visible;
        }
        finally
        {
            _ripCancellation?.Dispose();
            _ripCancellation = null;
            RefreshSummary();
        }
    }

    /// <summary>
    /// Builds the track list for the disc in the drive, taking titles from the
    /// selected release where it has them and falling back to CD-Text, then to a
    /// placeholder the user can fix in the manual editor.
    /// </summary>
    private List<SoundtrackTrack> BuildTracksForCurrentDisc()
    {
        var tracks = new List<SoundtrackTrack>();
        if (_disc?.Toc?.Tracks is null) return tracks;

        var audioTracks = _disc.Toc.Tracks.Where(t => t.Type != TrackType.Data).OrderBy(t => t.Number).ToList();

        // A multi-disc release supplies titles per disc; use the disc we are on.
        var titles = _selectedMatch?.TitlesForDisc(
            _selectedMatch.TracksByDisc.Count > 1 ? _currentDiscNumber : _selectedMatch.DiscNumber) ?? [];

        // Only trust the title list when it covers this disc exactly. A partial
        // list would silently pair titles with the wrong tracks.
        var titlesUsable = titles.Count == audioTracks.Count;

        for (var i = 0; i < audioTracks.Count; i++)
        {
            var tocTrack = audioTracks[i];

            var title = titlesUsable
                ? titles[i]
                : _disc.CdText?.Tracks?.FirstOrDefault(t => t.Number == tocTrack.Number)?.Title;

            if (string.IsNullOrWhiteSpace(title)) title = $"Track {tocTrack.Number}";

            tracks.Add(new SoundtrackTrack
            {
                SourceDiscNumber = _currentDiscNumber,
                SourceTrackNumber = tocTrack.Number,
                // Strip any numbering the source baked into the title.
                Title = FileNaming.StripLeadingTrackNumber(title),
                Artist = _project.AlbumArtist,
                Composer = _project.AlbumArtist,
                SectorCount = tocTrack.SectorCount,
                Duration = TimeSpan.FromSeconds((double)tocTrack.SectorCount / CdConstants.SectorsPerSecond)
            });
        }

        return tracks;
    }

    private void OnRipProgress(RipTrackProgress progress)
    {
        RipTrackLabel.Text = $"{progress.TrackNumber:D2}  {progress.TrackTitle}";
        RipTrackPercent.Text = $"{progress.TrackPercent:F0}%";
        RipTrackProgress.Value = progress.TrackPercent;

        RipOverallProgress.Value = progress.OverallPercent;
        RipOverallPercent.Text = $"{progress.OverallPercent:F0}%";

        if (progress.TrackPercent >= 100)
        {
            AppendRipLog($"  {progress.TrackNumber:D2}  {progress.TrackTitle}" +
                         (progress.HadErrors ? "   [read errors]" : "   ok"));
        }
    }

    private void AppendRipLog(string line)
    {
        RipLogText.Text += (RipLogText.Text.Length > 0 ? "\n" : "") + line;
        RipLogScroller.ScrollToEnd();
    }

    private void CancelRipButton_Click(object sender, RoutedEventArgs e)
    {
        _ripCancellation?.Cancel();
        CancelRipButton.IsEnabled = false;
        SetStatus("Cancelling after the current track...");
    }

    private async void NextDiscButton_Click(object sender, RoutedEventArgs e)
    {
        _currentDiscNumber = _project.RippedDiscs.Count + 1;

        MessageBox.Show(this,
            $"Insert disc {_currentDiscNumber} and click OK once the drive has settled.",
            "ClaudeSoundtrack", MessageBoxButton.OK, MessageBoxImage.Information);

        if (DriveCombo.SelectedItem is not ComboBoxItem { Tag: string devicePath }) return;

        SetStatus($"Reading disc {_currentDiscNumber}...");

        try
        {
            _disc = await _drives.ReadDiscInfoAsync(devicePath);
            ShowDiscReadout(_disc);

            // Re-look-up so a set catalogued as separate releases still resolves,
            // but keep the album fields the user already settled on.
            GoToStage(Stage.Identify);
            await LookupAsync();
            SetStatus($"Disc {_currentDiscNumber} read. Confirm the release and start the rip.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not read disc {_currentDiscNumber}: {ex.Message}");
            GoToStage(Stage.Disc);
        }
    }

    private void ToArtworkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_project.Tracks.Count == 0)
        {
            SetStatus("Nothing has been ripped yet.");
            return;
        }

        GoToStage(Stage.Artwork);
        SetStatus("Choose the cover art for this album.");
    }

    // ================= Step 3: artwork =================

    private async void SearchArtworkButton_Click(object sender, RoutedEventArgs e)
    {
        SearchArtworkButton.IsEnabled = false;
        SetStatus("Searching iTunes, Cover Art Archive and Discogs...");

        try
        {
            using var artwork = new ArtworkSearchService(_lookup.Discogs);

            var candidates = await artwork.SearchAsync(
                _project.AlbumTitle,
                _project.AlbumArtist,
                musicBrainzReleaseId: _selectedMatch?.Source == "MusicBrainz" ? _selectedMatch.ReleaseId : null,
                musicBrainzReleaseGroupId: _selectedMatch?.ReleaseGroupId,
                discogsReleaseId: _selectedMatch?.Source == "Discogs" ? _selectedMatch.ReleaseId : null);

            if (candidates.Count == 0)
            {
                SetStatus("No artwork found online. Save the cover yourself and use Choose File.");
                MessageBox.Show(this,
                    "No cover art was found for this album.\n\n" +
                    "Limited soundtrack pressings often are not catalogued. Find the cover " +
                    "yourself, save it somewhere on this PC, then click Choose File.",
                    "ClaudeSoundtrack", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // The user confirms before anything is written, as required.
            var dialog = new ArtworkSelectionWindow(candidates, _project.AlbumTitle) { Owner = this };

            if (dialog.ShowDialog() == true && dialog.SelectedCandidate is not null)
            {
                SetPendingArtwork(dialog.SelectedCandidate);
            }
            else
            {
                SetStatus("Artwork rejected. Save the cover you want and use Choose File.");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Artwork search failed: {ex.Message}");
        }
        finally
        {
            SearchArtworkButton.IsEnabled = true;
        }
    }

    private void ChooseArtworkButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose cover art",
            Filter = "Images (*.jpg;*.jpeg;*.png;*.webp;*.bmp)|*.jpg;*.jpeg;*.png;*.webp;*.bmp|All files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };

        if (dialog.ShowDialog(this) != true) return;

        var candidate = ArtworkSearchService.FromLocalFile(dialog.FileName);

        if (candidate.ImageData is not { Length: > 0 })
        {
            MessageBox.Show(this, "That file could not be read as an image.",
                "ClaudeSoundtrack", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetPendingArtwork(candidate);
    }

    /// <summary>Shows the chosen art and arms the Apply button.</summary>
    private void SetPendingArtwork(ArtworkCandidate candidate)
    {
        _pendingArtwork = candidate;

        ArtworkIdlePanel.Visibility = Visibility.Collapsed;
        ArtworkPreviewPanel.Visibility = Visibility.Visible;
        ArtworkPreviewImage.Source = LoadBitmap(candidate.ImageData!);

        ArtworkSourceText.Text = candidate.Source.ToUpperInvariant();
        ArtworkResolutionText.Text = $"{candidate.ResolutionText} pixels";

        var edge = Math.Min(candidate.Width, candidate.Height);
        ArtworkQualityText.Text = edge switch
        {
            >= 1400 => "Excellent for upload.",
            >= 1000 => "Good for upload.",
            >= 600 => "Usable, but a larger scan would look better.",
            _ => "Low resolution - this will look soft in YouTube Music."
        };

        ApplyArtworkButton.IsEnabled = true;
        SetStatus($"Artwork selected from {candidate.Source} ({candidate.ResolutionText}).");
    }

    /// <summary>
    /// Decodes image bytes for display.
    ///
    /// The stream is fully cached on load so the bitmap does not keep a handle
    /// on the source, which would lock a file the user picked.
    /// </summary>
    private static BitmapImage? LoadBitmap(byte[] data)
    {
        try
        {
            var bitmap = new BitmapImage();
            using var stream = new MemoryStream(data);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private async void ApplyArtworkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingArtwork?.ImageData is not { Length: > 0 }) return;

        _project.CoverArt = _pendingArtwork.ImageData;
        _project.CoverArtWidth = _pendingArtwork.Width;
        _project.CoverArtHeight = _pendingArtwork.Height;
        _project.CoverArtSource = _pendingArtwork.LocalPath ?? _pendingArtwork.Source;

        ApplyArtworkButton.IsEnabled = false;
        SetStatus("Writing tags and artwork into every track...");

        try
        {
            // Flatten once more in case the track list changed since the last disc.
            TrackFlattener.Flatten(_project);

            var progress = new Progress<(int Done, int Total)>(p =>
                SetStatus($"Tagging {p.Done} of {p.Total}..."));

            var written = await Task.Run(() => _tagger.WriteAllTags(_project, progress));

            SetStatus($"{written} file(s) tagged. Running the readiness check...");
            GoToStage(Stage.Verify);
            RunReadinessCheck();
        }
        catch (Exception ex)
        {
            SetStatus($"Tagging failed: {ex.Message}");
            ApplyArtworkButton.IsEnabled = true;
        }
    }

    // ================= Step 4: verify =================

    private void RecheckButton_Click(object sender, RoutedEventArgs e) => RunReadinessCheck();

    private void RunReadinessCheck()
    {
        _issues.Clear();

        var report = _checker.Check(_project);
        foreach (var issue in report.Issues) _issues.Add(new IssueRow(issue));

        var green = (Color)FindResource("LampGreenColor");
        var red = (Color)FindResource("LampRedColor");
        var amber = (Color)FindResource("AmberColor");

        if (report.IsPerfect)
        {
            SetLamp(green);
            VerdictText.Text = "READY TO UPLOAD";
            VerdictDetailText.Text =
                $"{report.FilesChecked} tracks, flattened to one disc, all tagged and illustrated.";
        }
        else if (report.IsReady)
        {
            SetLamp(amber);
            VerdictText.Text = "READY, WITH NOTES";
            VerdictDetailText.Text =
                $"{report.FilesChecked} tracks checked. {report.WarningCount} thing(s) worth a look, nothing that breaks the upload.";
        }
        else
        {
            SetLamp(red);
            VerdictText.Text = "NOT READY";
            VerdictDetailText.Text =
                $"{report.ErrorCount} problem(s) must be fixed. Use Edit Tags to correct them track by track.";
        }

        UploadButton.IsEnabled = report.IsReady;

        SetStatus(report.IsReady
            ? "Album passed the readiness check."
            : $"{report.ErrorCount} problem(s) found. Fix them in Edit Tags, then re-check.");
    }

    private void SetLamp(Color color)
    {
        VerdictLamp.Fill = new SolidColorBrush(color);
        VerdictLamp.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = color,
            BlurRadius = 18,
            ShadowDepth = 0,
            Opacity = 0.95
        };
    }

    private void ManualEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (_project.Tracks.Count == 0)
        {
            SetStatus("There is nothing to edit yet.");
            return;
        }

        var editor = new ManualTagEditorWindow(_project, _tagger) { Owner = this };
        editor.ShowDialog();

        // The editor writes directly to the files, so re-check against disk.
        RunReadinessCheck();
        RefreshSummary();
    }

    private void UploadButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_project.OutputFolder)) return;

        var instructions = new UploadInstructionsWindow(_project) { Owner = this };

        if (instructions.ShowDialog() == true)
        {
            OpenOutputFolder();
        }
        else
        {
            // The user said it is not ready after all - fall back to the editor.
            ManualEditButton_Click(sender, e);
        }
    }

    private void OutputFolderButton_Click(object sender, RoutedEventArgs e) => OpenOutputFolder();

    /// <summary>
    /// Opens the album folder in Explorer so the files can be dragged into the
    /// YouTube Music upload page.
    /// </summary>
    private void OpenOutputFolder()
    {
        if (string.IsNullOrEmpty(_project.OutputFolder) || !Directory.Exists(_project.OutputFolder))
        {
            SetStatus("The album folder does not exist yet.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_project.OutputFolder}\"",
                UseShellExecute = true
            });

            SetStatus("Album folder opened. Drag the FLAC files onto the YouTube Music upload page.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open the folder: {ex.Message}");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _ripCancellation?.Cancel();
        _ripCancellation?.Dispose();
        _lookup.Dispose();
        base.OnClosed(e);
    }
}
