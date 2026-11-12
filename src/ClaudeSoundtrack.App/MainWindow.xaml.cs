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

public partial class MainWindow : PanelWindow
{
    /// <summary>The five stages shown down the left rail.</summary>
    private enum Stage { Disc = 0, Identify = 1, Rip = 2, Artwork = 3, Verify = 4 }

    private readonly AppSettings _settings = AppSettings.Load();

    private readonly OpticalDriveService _drives = new();
    private readonly CdRipService _ripper = new();
    private readonly TaggingService _tagger = new();
    private readonly ReadinessChecker _checker = new();
    private readonly MetadataLookupService _lookup = new();

    // Not readonly: starting a new album replaces it wholesale. Reusing one
    // instance across albums silently merged the second into the first.
    private AlbumProject _project = new();
    private readonly ObservableCollection<MatchRow> _matches = new();
    private readonly ObservableCollection<IssueRow> _issues = new();

    /// <summary>File name stems already written, so later discs cannot overwrite earlier ones.</summary>
    private readonly HashSet<string> _usedFileStems = new(StringComparer.OrdinalIgnoreCase);

    private DiscInfo? _disc;
    private ReleaseMatch? _selectedMatch;
    private ArtworkCandidate? _pendingArtwork;
    private CancellationTokenSource? _ripCancellation;
    private Stage _stage = Stage.Disc;

    /// <summary>Which physical disc is being ripped. Set from the disc itself, not from how many have been inserted.</summary>
    private int _currentDiscNumber = 1;

    /// <summary>A line to put in the rip log after it is cleared for the next disc.</summary>
    private string? _pendingDiscNote;

    /// <summary>Every hit from the last lookup, before the locked-album filter.</summary>
    private List<ReleaseMatch> _lookupResults = [];

    private int _lookupTrackCount;

    /// <summary>The repair dialog is offered once per album, and stays available on the button.</summary>
    private bool _repairOffered;

    public MainWindow()
    {
        InitializeComponent();

        // Push the saved look into the live state before anything renders, so the
        // lamps come up at the user's chosen glow rather than flashing at default.
        UiState.Current.ApplyFrom(_settings);

        MatchList.ItemsSource = _matches;
        IssueList.ItemsSource = _issues;

        LoadDrives();
        GoToStage(Stage.Disc);

        // After the window is up, so the warning appears over a drawn panel
        // rather than in front of nothing.
        Loaded += (_, _) => CheckFlacMimeRegistration();

        SetUpDiscWatching();
    }

    // ================= Tray and disc watching =================

    private TrayPresence? _tray;
    private DiscWatcher? _discWatcher;

    /// <summary>True when the app was launched by Windows at login.</summary>
    private static bool StartedInTray =>
        Environment.GetCommandLineArgs().Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Starts the notification-area icon and the disc watcher when either is
    /// wanted, and hides the window if Windows launched us at login.
    /// </summary>
    private void SetUpDiscWatching()
    {
        // A single-file app gets moved around; keep the Run entry pointing at
        // wherever it actually lives now.
        StartupRegistration.RefreshPathIfRegistered();

        var startedInTray = StartedInTray;
        if (!_settings.WatchForDiscs && !startedInTray) return;

        _tray = new TrayPresence(this);
        _tray.RipRequested += (_, _) => BeginRipFromTray();

        _discWatcher = new DiscWatcher();
        _discWatcher.DiscInserted += OnDiscInserted;

        if (startedInTray)
        {
            // Hidden before the first show, so nothing flashes on screen at login.
            Visibility = Visibility.Hidden;
            ShowInTaskbar = false;

            // A window that is never shown never raises Loaded, so the watcher has
            // to be started here. EnsureHandle inside Start creates the HWND
            // without showing anything, which is all WM_DEVICECHANGE needs.
            _discWatcher.Start(this);
        }
        else
        {
            Loaded += (_, _) => _discWatcher.Start(this);
        }
    }

    private string? _pendingDiscPath;

    private void OnDiscInserted(object? sender, DiscInsertedEventArgs e)
    {
        _pendingDiscPath = e.DevicePath;

        SelectDrive(e.DevicePath);

        // A disc going in while a set is already underway is the next disc, not a
        // new album. Read it and show which one it is.
        if (_project.IdentityLocked && _project.RippedDiscs.Count > 0 && _ripCancellation is null)
        {
            SetStatus(AlbumAssembler.DescribeNextDisc(_project));
            _ = ReadNextDiscAsync();
            return;
        }

        SetStatus($"Audio CD detected in {e.DevicePath}. Press Read Disc to begin.");

        // Only announce it if the window is not already in front of the user.
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            _tray?.NotifyDiscInserted(e.DevicePath, e.VolumeLabel);
        }
    }

    /// <summary>Brings the app up and reads whichever disc prompted the notification.</summary>
    private async void BeginRipFromTray()
    {
        _tray?.ShowWindow();

        if (_pendingDiscPath is not null) SelectDrive(_pendingDiscPath);

        // Only auto-read from a clean start; barging into a rip in progress would
        // be worse than doing nothing.
        if (_stage == Stage.Disc && _project.Tracks.Count == 0)
        {
            await ReadSelectedDiscAsync();
        }
    }

    /// <summary>Points the drive selector at a particular drive, if it is listed.</summary>
    private void SelectDrive(string devicePath)
    {
        foreach (var item in DriveCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && string.Equals(tag, devicePath, StringComparison.OrdinalIgnoreCase))
            {
                DriveCombo.SelectedItem = item;
                return;
            }
        }

        // The drive appeared after the list was built.
        LoadDrives();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Closing the window while watching means "get out of the way", not
        // "stop watching" - otherwise the feature switches itself off the first
        // time the user tidies their desktop.
        if (_tray is not null && _settings.WatchForDiscs && !_isExiting)
        {
            e.Cancel = true;
            _tray.HideWindow();
            return;
        }

        base.OnClosing(e);
    }

    private bool _isExiting;

    /// <summary>
    /// Warns if Windows reports the wrong MIME type for .flac, which silently
    /// breaks browser uploads to YouTube Music.
    ///
    /// Only ever shown when the registration is genuinely wrong. A warning that
    /// greets a healthy machine teaches people to dismiss warnings.
    /// </summary>
    private void CheckFlacMimeRegistration()
    {
        if (_settings.SuppressFlacMimeCheck) return;

        var report = FlacMimeCheck.Check();
        if (!report.NeedsFixing) return;

        var dialog = new FlacMimeWindow(report) { Owner = this };
        dialog.ShowDialog();

        if (dialog.WasFixed)
        {
            SetStatus("FLAC file type corrected. Restart Firefox for it to take effect.");
        }

        if (dialog.SuppressFutureChecks)
        {
            _settings.SuppressFlacMimeCheck = true;
            _settings.Save();
        }
    }

    // ================= Window furniture =================

    private void MinimiseButton_Click(object sender, RoutedEventArgs e) => Minimise();

    private void MaximiseButton_Click(object sender, RoutedEventArgs e) => ToggleMaximise();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void LibraryButton_Click(object sender, RoutedEventArgs e)
    {
        var library = new LibraryWindow(_settings) { Owner = this };
        library.ShowDialog();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings) { Owner = this };
        dialog.ShowDialog();

        // The dialog edits UiState live for preview; persist whatever survived.
        _settings.Save();
    }

    // ================= Starting over =================

    /// <summary>
    /// Clears everything belonging to the album just finished and returns to the
    /// first stage.
    ///
    /// This has to replace the whole project rather than tidy it. Every field
    /// below outlived an album at some point: the output folder made a second
    /// album's tracks land in the first album's folder, the track list made
    /// flattening renumber across both, and the used-name set and disc counter
    /// carried over with them.
    /// </summary>
    private void StartNewAlbum()
    {
        _project = new AlbumProject();

        _matches.Clear();
        _issues.Clear();
        _usedFileStems.Clear();

        _disc = null;
        _selectedMatch = null;
        _pendingArtwork = null;
        _currentDiscNumber = 1;
        _pendingDiscPath = null;

        AlbumTitleBox.Text = string.Empty;
        AlbumArtistBox.Text = string.Empty;
        YearBox.Text = string.Empty;
        DiscBox.Text = string.Empty;
        SearchBox.Text = string.Empty;
        _lookupResults = [];
        _repairOffered = false;
        ShowAllMatchesCheck.IsChecked = false;
        ShowAllMatchesCheck.Visibility = Visibility.Collapsed;
        DiscReadoutText.Text = "Awaiting disc...";
        RipLogText.Text = string.Empty;

        RipTrackProgress.Value = 0;
        RipOverallProgress.Value = 0;
        RipTrackLabel.Text = "Ready";
        RipTrackPercent.Text = "0%";
        RipOverallPercent.Text = "0%";

        ArtworkIdlePanel.Visibility = Visibility.Visible;
        ArtworkPreviewPanel.Visibility = Visibility.Collapsed;
        ArtworkPreviewImage.Source = null;
        ApplyArtworkButton.IsEnabled = false;

        UploadButton.IsEnabled = false;
        VerdictText.Text = "Not yet checked";
        VerdictDetailText.Text = string.Empty;
        VerdictLamp.Fill = new SolidColorBrush(Color.FromRgb(0x1A, 0x15, 0x12));
        VerdictLamp.Effect = null;

        LoadDrives();
        GoToStage(Stage.Disc);
        RefreshSummary();

        SetStatus("Ready for a new album.");
    }

    /// <summary>
    /// Asks whether to clear down after an album is finished.
    ///
    /// Offered rather than assumed, and offered at all because leaving the last
    /// album loaded is what let a second one be started on top of it.
    /// </summary>
    private void OfferNewAlbum()
    {
        var answer = MessageBox.Show(this,
            $"\"{_project.AlbumTitle}\" is done.\n\nClear it and start another album?",
            "ClaudeSoundtrack", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer == MessageBoxResult.Yes) StartNewAlbum();
    }

    private void NewAlbumButton_Click(object sender, RoutedEventArgs e)
    {
        // Only worth confirming when there is something to lose. The files on disk
        // are safe either way; what is discarded is the in-app session.
        if (_project.Tracks.Count > 0)
        {
            var answer = MessageBox.Show(this,
                $"Start a new album?\n\n\"{_project.AlbumTitle}\" stays on disk at:\n{_project.OutputFolder}\n\n" +
                "It is only cleared from this window.",
                "ClaudeSoundtrack", MessageBoxButton.OKCancel, MessageBoxImage.Question);

            if (answer != MessageBoxResult.OK) return;
        }

        StartNewAlbum();
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
        CollectedDiscsText.Text = _project.DiscCount > 1
            ? $"{_project.RippedDiscs.Count} of {_project.DiscCount} discs ripped"
            : _project.RippedDiscs.Count switch
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

    private async void ReadDiscButton_Click(object sender, RoutedEventArgs e) =>
        await ReadSelectedDiscAsync();

    /// <summary>
    /// Reads the disc in the selected drive. Separate from the click handler so
    /// the tray can trigger exactly the same path.
    /// </summary>
    private async Task ReadSelectedDiscAsync()
    {
        if (DriveCombo.SelectedItem is not ComboBoxItem { Tag: string devicePath }) return;

        // Reading a fresh disc while a finished album is still loaded is how a
        // second album ended up inheriting the first one's folder. Use "Rip
        // Another Disc" to add a disc to the current set; this path is for a new
        // album, so offer to clear down first.
        if (_project.Tracks.Count > 0 && _stage >= Stage.Artwork)
        {
            var answer = MessageBox.Show(this,
                $"\"{_project.AlbumTitle}\" is still loaded.\n\n" +
                "Start a new album? It stays on disk either way.\n\n" +
                "Choose No if this disc belongs to the album already loaded.",
                "ClaudeSoundtrack", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes)
            {
                StartNewAlbum();
                SelectDrive(devicePath);
            }
        }

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
            _lookupResults = (await _lookup.LookupAsync(_disc, hint, _project.ReleaseId)).ToList();
            _lookupTrackCount = discTrackCount;
            ShowFilteredMatches();
        }
        catch (Exception ex)
        {
            SetStatus($"Lookup failed: {ex.Message}. Enter the album details by hand.");
        }

        // Whatever happened, the disc's own track count is known, so the fields
        // can be filled in manually and the rip can still go ahead.
        if (string.IsNullOrWhiteSpace(AlbumTitleBox.Text) && _disc.CdText?.AlbumTitle is { } cdTitle)
            AlbumTitleBox.Text = AlbumTitleCleaner.Parse(cdTitle).Title;

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
            _lookupResults = (await _lookup.SearchAsync(query, discTrackCount)).ToList();
            _lookupTrackCount = discTrackCount;
            ShowFilteredMatches();
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
        var belongs = AlbumCandidateFilter.Belongs(_project, row.Match);

        if (!_project.IdentityLocked)
        {
            var cleaned = AlbumTitleCleaner.Parse(row.Match.Title);
            AlbumTitleBox.Text = cleaned.Title;
            AlbumArtistBox.Text = row.Match.Artist;
            YearBox.Text = row.Match.Year?.ToString() ?? string.Empty;
        }
        else if (belongs)
        {
            AlbumAssembler.AdoptStructure(_project, row.Match);
            // A title locked as "The Rock - Disc 1" is still the same album.
            // Show the cleaned name. Do not replace it with a different release's title.
            var cleaned = AlbumTitleCleaner.Parse(
                string.IsNullOrWhiteSpace(AlbumTitleBox.Text) ? _project.AlbumTitle : AlbumTitleBox.Text);
            if (cleaned.HadDiscSuffix)
            {
                AlbumTitleBox.Text = cleaned.Title;
                _project.AlbumTitle = YouTubeMusicTags.Normalize(cleaned.Title);
            }
        }

        var discNumber = AlbumAssembler.ChooseDiscNumber(_project, belongs ? row.Match : null);
        DiscBox.Text = discNumber.ToString();
        _currentDiscNumber = discNumber;

        if (_project.IdentityLocked && !belongs)
        {
            SetStatus($"\"{row.Match.Title}\" is not part of \"{_project.AlbumTitle}\". The album title was left as it is.");
            return;
        }

        if (_project.RippedDiscs.Contains(discNumber))
        {
            SetStatus($"Disc {discNumber} is already ripped. {AlbumAssembler.DescribeNextDisc(_project)}.");
            return;
        }

        SetStatus(_project.IdentityLocked
            ? AlbumAssembler.DescribeDetection(_project, discNumber)
            : row.Match.IsTocVerified
                ? $"\"{AlbumTitleCleaner.Parse(row.Match.Title).Title}\" matches this disc."
                : $"\"{row.Match.Title}\" was not verified against this disc. Check it before ripping.");
    }

    private void ShowAllMatches_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ShowFilteredMatches();
    }

    /// <summary>
    /// Fills the list. Once an album is locked, unrelated hits stay hidden until
    /// "Show all results" is ticked.
    /// </summary>
    private void ShowFilteredMatches()
    {
        var showAll = ShowAllMatchesCheck.IsChecked == true;
        ShowAllMatchesCheck.Visibility = _project.IdentityLocked ? Visibility.Visible : Visibility.Collapsed;

        var visible = _lookupResults
            .Where(m => showAll || AlbumCandidateFilter.Belongs(_project, m))
            .ToList();

        _matches.Clear();
        foreach (var match in visible) _matches.Add(new MatchRow(match, _lookupTrackCount));

        if (_matches.Count > 0)
        {
            MatchList.SelectedIndex = 0;
        }
        else if (_lookupResults.Count > 0 && _project.IdentityLocked && !showAll)
        {
            SetStatus($"Nothing in this lookup belongs to \"{_project.AlbumTitle}\". Tick Show all results to see the rest.");
        }
        else if (_lookupResults.Count == 0)
        {
            SetStatus("Nothing found. Type the album title above and press Search, or fill the fields in by hand.");
        }

        RefreshSummary();
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

        if (string.IsNullOrWhiteSpace(AlbumArtistBox.Text))
        {
            MessageBox.Show(this,
                "The album needs an album artist.\n\nYouTube Music files tracks with different artists as different albums, so every disc of this set has to share one.",
                "ClaudeSoundtrack", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // The boxes stay editable. A disc suffix is stripped here, so a title
        // locked as "The Rock - Disc 1" cannot follow disc 2 into YouTube Music.
        var year = int.TryParse(YearBox.Text?.Trim(), out var parsed) && parsed > 0 ? parsed : (int?)null;
        AlbumAssembler.ApplyUserEdits(_project, AlbumTitleBox.Text, AlbumArtistBox.Text, year);
        AlbumTitleBox.Text = _project.AlbumTitle;

        if (_selectedMatch is not null && AlbumCandidateFilter.Belongs(_project, _selectedMatch))
            AlbumAssembler.AdoptStructure(_project, _selectedMatch);
        else if (_selectedMatch is not null)
        {
            var anyway = MessageBox.Show(this,
                $"\"{_selectedMatch.Title}\" does not look like \"{_project.AlbumTitle}\".\n\nRip it as part of this album anyway?",
                "ClaudeSoundtrack", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (anyway != MessageBoxResult.Yes) return;
        }

        _currentDiscNumber = ResolveDiscNumber();
        DiscBox.Text = _currentDiscNumber.ToString();

        if (_project.RippedDiscs.Contains(_currentDiscNumber))
        {
            MessageBox.Show(this,
                $"Disc {_currentDiscNumber} is already ripped.\n\n{AlbumAssembler.DescribeNextDisc(_project)}.",
                "ClaudeSoundtrack", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        OfferRepairIfNeeded();
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
        var folderName = FileNaming.BuildAlbumFolderName(_project.AlbumTitle, _project.AlbumArtist, _project.Year);

        if (!string.IsNullOrEmpty(_project.OutputFolder) && Directory.Exists(_project.OutputFolder))
        {
            // Reuse it only if it still belongs to this album. Disc 2 of a set
            // must land beside disc 1, but a different album must not - reusing
            // it unconditionally sent a second album's tracks into the first
            // album's folder and left the upload pointing at the wrong place.
            if (string.Equals(Path.GetFileName(_project.OutputFolder), folderName, StringComparison.OrdinalIgnoreCase))
                return true;

            // The usual reason the names differ is "The Rock - Disc 1" versus
            // "The Rock". Repair renames the folder instead of opening a second one.
            if (OfferRepairIfNeeded()
                && string.Equals(Path.GetFileName(_project.OutputFolder), FileNaming.BuildAlbumFolderName(_project.AlbumTitle, _project.AlbumArtist, _project.Year), StringComparison.OrdinalIgnoreCase))
                return true;

            _project.OutputFolder = null;
        }

        try
        {
            var musicRoot = _settings.ResolveMusicFolder();
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

        RipHeadingText.Text = _project.DiscCount > 1
            ? $"Ripping disc {_currentDiscNumber} of {_project.DiscCount}"
            : $"Ripping disc {_currentDiscNumber}";
        RipLogText.Text = string.Empty;
        if (_pendingDiscNote is not null)
        {
            AppendRipLog(_pendingDiscNote);
            _pendingDiscNote = null;
        }
        CancelRipButton.IsEnabled = true;
        NextDiscButton.Visibility = Visibility.Collapsed;
        RepairRippedButton.Visibility = Visibility.Collapsed;
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

            // Lights up the header lamp bank for the duration.
            UiState.Current.IsWorking = true;

            // Task.Run is load-bearing, not decoration. FoxRedbook's
            // ReadSectorsAsync issues its SCSI command synchronously and hands
            // back an already-completed Task, so awaiting it does not yield - the
            // whole rip ran on the UI thread and the window froze solid for the
            // length of the disc, progress bar included.
            //
            // Progress<T> captured the UI SynchronizationContext when it was
            // constructed above, so reports still marshal back correctly.
            var token = _ripCancellation.Token;
            await Task.Run(
                () => _ripper.RipTracksAsync(
                    devicePath, newTracks, _project.OutputFolder!, fileNames, progress, token),
                token);

            _project.RippedDiscs.Add(_currentDiscNumber);

            var errored = newTracks.Count(t => t.HadReadErrors);
            var unstable = newTracks.Count(t => t.Confidence == RipConfidence.Unstable);
            var accurate = newTracks.Count(t => t.Confidence == RipConfidence.AccurateRip);
            var repeatable = newTracks.Count(t => t.Confidence == RipConfidence.Repeatable);
            AppendRipLog(errored == 0
                ? $"Disc {_currentDiscNumber} complete, no read errors."
                : $"Disc {_currentDiscNumber} complete, {errored} track(s) had read errors.");
            if (accurate > 0) AppendRipLog($"{accurate} track(s) matched AccurateRip.");
            if (repeatable > 0) AppendRipLog($"{repeatable} track(s) matched a second read (not in AccurateRip, or it disagreed).");
            if (unstable > 0) AppendRipLog($"{unstable} track(s) read differently the second time — re-rip this disc before uploading.");

            var next = AlbumAssembler.DescribeNextDisc(_project);
            RipSubText.Text = $"Disc {_currentDiscNumber} is done. {next}, or continue to artwork.";
            NextDiscButton.Content = next;
            NextDiscButton.Visibility = Visibility.Visible;
            RepairRippedButton.Visibility = Visibility.Visible;
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
            UiState.Current.IsWorking = false;
            _ripCancellation?.Dispose();
            _ripCancellation = null;
            RefreshSummary();
        }
    }

    /// <summary>
    /// Which disc this TOC is. A number typed in the disc box wins, then a
    /// confident match against the locked release, then the match's own disc
    /// number. The insertion counter is only the last resort.
    /// </summary>
    private int ResolveDiscNumber()
    {
        if (int.TryParse(DiscBox.Text?.Trim(), out var typed) && typed > 0)
            return typed;

        if (_disc is not null)
        {
            var id = DiscIdentifier.Identify(MetadataLookupService.DiscSnapshotFrom(_disc), _project.Media);
            if (id.IsConfident && id.DiscNumber is int detected) return detected;
        }

        if (_selectedMatch is { IsTocVerified: true, DiscNumber: > 0 })
            return _selectedMatch.DiscNumber;

        return _currentDiscNumber > 0 ? _currentDiscNumber : 1;
    }

    /// <summary>
    /// Builds the track list for the disc in the drive.
    ///
    /// Titles come from the locked release's copy of this disc number, then from
    /// the selected match, then from CD-Text. They are used only when the list
    /// is the same length as the disc.
    /// </summary>
    private List<SoundtrackTrack> BuildTracksForCurrentDisc()
    {
        if (_disc?.Toc?.Tracks is null) return [];

        var sectors = _disc.Toc.Tracks
            .Where(t => t.Type != TrackType.Data)
            .OrderBy(t => t.Number)
            .Select(t => t.SectorCount)
            .ToList();

        var titles = AlbumAssembler.TitlesFor(_project, _currentDiscNumber, _selectedMatch);
        if (titles.Count != sectors.Count)
        {
            titles = _disc.CdText?.Tracks?
                .OrderBy(t => t.Number)
                .Select(t => t.Title)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t!)
                .ToList() ?? [];
        }

        return AlbumAssembler.BuildTracks(_currentDiscNumber, sectors, titles, _project.AlbumArtist);
    }

    private void OnRipProgress(RipTrackProgress progress)
    {
        RipTrackLabel.Text = $"{progress.TrackNumber:D2}  {progress.TrackTitle}";
        RipTrackPercent.Text = $"{progress.TrackPercent:F0}%";
        RipTrackProgress.Value = progress.TrackPercent;

        RipOverallProgress.Value = progress.OverallPercent;
        RipOverallPercent.Text = $"{progress.OverallPercent:F0}%";

        // Log on the single completion report, not on reaching 100 percent -
        // progress can legitimately report 100 more than once, which previously
        // wrote every track into the log twice.
        if (progress.IsComplete)
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
        var prompt = AlbumAssembler.DescribeNextDisc(_project);
        MessageBox.Show(this,
            $"{prompt}\n\nClick OK once the drive has settled.",
            "ClaudeSoundtrack", MessageBoxButton.OK, MessageBoxImage.Information);

        await ReadNextDiscAsync();
    }

    private bool _readingDisc;

    /// <summary>
    /// Reads the disc now in the drive and fills the title and disc number.
    /// The rip itself waits for Start Rip, so the numbers can still be changed.
    /// </summary>
    private async Task ReadNextDiscAsync()
    {
        if (_readingDisc) return;
        if (DriveCombo.SelectedItem is not ComboBoxItem { Tag: string devicePath }) return;

        _readingDisc = true;
        var prompt = AlbumAssembler.DescribeNextDisc(_project);
        SetStatus($"{prompt} — reading the disc...");

        try
        {
            _disc = await _drives.ReadDiscInfoAsync(devicePath);
            ShowDiscReadout(_disc);
            GoToStage(Stage.Identify);
            await LookupAsync();

            if (_matches.Count == 0)
            {
                var next = AlbumAssembler.ChooseDiscNumber(_project, null);
                DiscBox.Text = next.ToString();
                _currentDiscNumber = next;
                SetStatus(AlbumAssembler.DescribeDetection(_project, next) + ". Set the disc number if that is wrong, then Start Rip.");
            }

            if (_project.RippedDiscs.Contains(_currentDiscNumber))
            {
                MessageBox.Show(this,
                    $"That is disc {_currentDiscNumber}, which is already ripped.\n\n{AlbumAssembler.DescribeNextDisc(_project)}.",
                    "ClaudeSoundtrack", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (_selectedMatch is not null && !AlbumCandidateFilter.Belongs(_project, _selectedMatch))
            {
                MessageBox.Show(this,
                    $"This disc does not look like \"{_project.AlbumTitle}\".\n\n" +
                    "The list is limited to this album. Tick Show all results if the right release is missing.",
                    "ClaudeSoundtrack", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            OfferRepairIfNeeded();
        }
        catch (Exception ex)
        {
            SetStatus($"Could not read the next disc: {ex.Message}");
            GoToStage(Stage.Disc);
        }
        finally
        {
            _readingDisc = false;
        }
    }

    private void RepairButton_Click(object sender, RoutedEventArgs e)
    {
        if (_project.Tracks.Count == 0)
        {
            using var browser = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Folder of FLAC files to repair",
                UseDescriptionForTitle = true
            };
            if (browser.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            _project = AlbumRepair.LoadFolder(browser.SelectedPath);
            _usedFileStems.Clear();
            foreach (var track in _project.Tracks)
            {
                if (!string.IsNullOrEmpty(track.FilePath))
                    _usedFileStems.Add(Path.GetFileNameWithoutExtension(track.FilePath));
            }
        }

        var plan = AlbumRepair.Plan(_project);
        if (plan is null)
        {
            SetStatus("No ripped FLAC files to repair.");
            return;
        }

        _repairOffered = true;
        if (ConfirmRepair(plan))
            RefreshSummary();
    }

    /// <summary>Opens the repair preview once per album when the files disagree with the cleaned title.</summary>
    private bool OfferRepairIfNeeded()
    {
        if (_repairOffered) return false;
        var plan = AlbumRepair.Plan(_project);
        if (plan is not { ChangesAnything: true }) return false;
        _repairOffered = true;
        return ConfirmRepair(plan);
    }

    private bool ConfirmRepair(AlbumRepairPlan plan)
    {
        var dialog = new RepairAlbumWindow(plan) { Owner = this };
        if (dialog.ShowDialog() != true) return false;

        try
        {
            AlbumRepair.Apply(_project, plan);
            AlbumTitleBox.Text = _project.AlbumTitle;
            AlbumArtistBox.Text = _project.AlbumArtist;
            RefreshSummary();
            SetStatus($"Updated {plan.FileCount} file(s) to \"{_project.AlbumTitle}\".");
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "ClaudeSoundtrack", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void ToArtworkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_project.Tracks.Count == 0)
        {
            SetStatus("Nothing has been ripped yet.");
            return;
        }

        if (_project.DiscCount > _project.RippedDiscs.Count)
        {
            var missing = Enumerable.Range(1, _project.DiscCount)
                .Where(n => !_project.RippedDiscs.Contains(n))
                .ToList();
            var answer = MessageBox.Show(this,
                $"Disc {string.Join(", ", missing)} of {_project.DiscCount} has not been ripped.\n\n" +
                "Continue to artwork anyway? The upload will be missing those discs, and the track order cannot be fixed in YouTube Music afterwards.",
                "ClaudeSoundtrack", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        GoToStage(Stage.Artwork);
        SetStatus("Choose the cover art for this album.");
    }

    // ================= Step 3: artwork =================

    private async void SearchArtworkButton_Click(object sender, RoutedEventArgs e)
    {
        SearchArtworkButton.IsEnabled = false;
        SetStatus("Searching Cover Art Archive, then Discogs, then iTunes...");

        try
        {
            using var artwork = new ArtworkSearchService(_lookup.Discogs);

            // Prefer the release locked on disc 1. The match selected while
            // identifying a later disc must not swap in a different cover.
            var musicBrainzId = _project.ReleaseSource == "MusicBrainz"
                ? _project.ReleaseId
                : _selectedMatch?.Source == "MusicBrainz" ? _selectedMatch.ReleaseId : null;
            var discogsId = _project.ReleaseSource == "Discogs"
                ? _project.ReleaseId
                : _selectedMatch?.Source == "Discogs" ? _selectedMatch.ReleaseId : null;

            var candidates = await artwork.SearchAsync(
                _project.AlbumTitle,
                _project.AlbumArtist,
                musicBrainzReleaseId: musicBrainzId,
                musicBrainzReleaseGroupId: _project.ReleaseGroupId ?? _selectedMatch?.ReleaseGroupId,
                discogsReleaseId: discogsId);

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
        // Convert here, before it is ever embedded, so the preview, the recorded
        // resolution and the bytes written to the files are all the same thing.
        // A WebP cover embeds happily and then never appears in YouTube Music.
        var normalised = ArtworkNormaliser.Normalise(candidate.ImageData!);

        candidate.ImageData = normalised.Data;
        if (normalised.Width > 0) candidate.Width = normalised.Width;
        if (normalised.Height > 0) candidate.Height = normalised.Height;

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

        if (normalised.Problem is not null)
        {
            ArtworkQualityText.Text = normalised.Problem;
            SetStatus("Artwork selected, but it is in a format YouTube Music will not display.");
            MessageBox.Show(this, normalised.Problem,
                "ClaudeSoundtrack", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else if (normalised.WasConverted)
        {
            SetStatus($"Artwork selected from {candidate.Source} ({candidate.ResolutionText}), " +
                      "converted to JPEG so YouTube Music will display it.");
        }
        else
        {
            SetStatus($"Artwork selected from {candidate.Source} ({candidate.ResolutionText}).");
        }
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

    private async void UploadButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_project.OutputFolder)) return;

        // Direct upload first: it needs no browser at all. Falls through to the
        // browser route if the session is missing or the user prefers it.
        var direct = new DirectUploadWindow(_project) { Owner = this };
        var uploaded = direct.ShowDialog();

        if (uploaded == true)
        {
            SetStatus("Album uploaded to YouTube Music.");
            OfferNewAlbum();
            return;
        }

        if (!direct.UseBrowserInstead)
        {
            SetStatus("Upload cancelled.");
            return;
        }

        var instructions = new UploadInstructionsWindow(_project) { Owner = this };

        if (instructions.ShowDialog() != true)
        {
            // The user said it is not ready after all - fall back to the editor.
            ManualEditButton_Click(sender, e);
            return;
        }

        if (!instructions.WatchForFileDialog)
        {
            OpenOutputFolder();
            return;
        }

        await CatchUploadDialogAsync();
    }

    /// <summary>
    /// Waits for the browser's file picker and points it at this album.
    ///
    /// Falls back to opening Explorer if no picker turns up, so the user is never
    /// left with nothing to drag from.
    /// </summary>
    private async Task CatchUploadDialogAsync()
    {
        var files = _project.Tracks
            .OrderBy(t => t.FlatTrackNumber)
            .Select(t => t.FilePath)
            .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
            .Select(p => p!)
            .ToList();

        SetStatus("Waiting for the browser's upload dialog... open Upload music in YouTube Music now.");
        UiState.Current.IsWorking = true;

        try
        {
            var outcome = await new FileDialogRedirector()
                .RedirectNextDialogAsync(_project.OutputFolder!, files);

            switch (outcome)
            {
                case FileDialogRedirector.Outcome.Redirected:
                    SetStatus($"Upload dialog filled in with {files.Count} track(s). Press Open in the browser to start.");
                    break;

                case FileDialogRedirector.Outcome.NavigatedOnly:
                    SetStatus("The upload dialog was found but the tracks would not stay selected. " +
                              "Select them by hand in the album folder.");
                    OpenOutputFolder();
                    break;

                case FileDialogRedirector.Outcome.DialogNotUnderstood:
                    SetStatus("A file dialog opened but could not be filled in. Opening the album folder instead.");
                    OpenOutputFolder();
                    break;

                default:
                    SetStatus("No upload dialog appeared. Opening the album folder instead.");
                    OpenOutputFolder();
                    break;
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Could not watch for the upload dialog: {ex.Message}");
            OpenOutputFolder();
        }
        finally
        {
            UiState.Current.IsWorking = false;
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

        _discWatcher?.Dispose();
        // The tray icon outlives the window unless it is explicitly removed, and
        // a ghost icon that does nothing until hovered is a classic Windows sin.
        _tray?.Dispose();

        base.OnClosed(e);
    }
}
