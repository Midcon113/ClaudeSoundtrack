using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClaudeSoundtrack.Core.Models;
using ClaudeSoundtrack.Core.Services;

namespace ClaudeSoundtrack.App.Dialogs;

/// <summary>
/// Browses the albums already ripped into the library and plays them.
///
/// Playback goes through <see cref="MediaPlayer"/>, which decodes FLAC via Media
/// Foundation on Windows 10 and later - no extra codec or audio library to ship.
/// </summary>
public partial class LibraryWindow : PanelWindow
{
    /// <summary>An album row, with its cover decoded small for the list.</summary>
    private sealed class AlbumRow
    {
        public required LibraryAlbum Album { get; init; }
        public required string Title { get; init; }
        public required string Subtitle { get; init; }
        public required string Summary { get; init; }
        public BitmapImage? Thumbnail { get; init; }
    }

    /// <summary>A track row that can show a now-playing marker.</summary>
    private sealed class TrackRow : INotifyPropertyChanged
    {
        private bool _isPlaying;

        public required LibraryTrack Track { get; init; }

        public int TrackNumber => Track.TrackNumber;
        public string Title => Track.Title;
        public string Artist => Track.Artist;
        public string DurationText => Track.DurationText;

        /// <summary>A lit marker beside whichever track is sounding.</summary>
        public string NowPlayingMark => _isPlaying ? "▶" : string.Empty;

        public bool IsPlaying
        {
            get => _isPlaying;
            set
            {
                if (_isPlaying == value) return;
                _isPlaying = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NowPlayingMark)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly AppSettings _settings;
    private readonly LibraryScanner _scanner = new();
    private readonly ObservableCollection<AlbumRow> _albums = new();
    private readonly ObservableCollection<TrackRow> _tracks = new();

    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _tick;

    private int _currentIndex = -1;
    private bool _isPlaying;
    private bool _isSeeking;

    public LibraryWindow(AppSettings settings)
    {
        InitializeComponent();

        _settings = settings;

        AlbumList.ItemsSource = _albums;
        TrackList.ItemsSource = _tracks;

        LibraryPathText.Text = settings.ResolveMusicFolder();
        _player.Volume = VolumeSlider.Value;
        _player.MediaEnded += (_, _) => PlayNext();

        // Drives the seek bar and clock. Stopped whenever nothing is playing, so
        // an idle window costs nothing.
        _tick = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += OnTick;

        Loaded += async (_, _) => await RescanAsync();
        Closed += (_, _) => _player.Close();
    }

    // ================= Scanning =================

    private async void RescanButton_Click(object sender, RoutedEventArgs e) => await RescanAsync();

    private async Task RescanAsync()
    {
        var root = _settings.ResolveMusicFolder();
        LibraryPathText.Text = root;

        UiState.Current.IsWorking = true;

        try
        {
            // Scanning reads tags from every file, so it goes off the UI thread -
            // a large library would otherwise freeze the window exactly the way
            // the ripper used to.
            var found = await Task.Run(() => _scanner.Scan(root));

            _albums.Clear();
            foreach (var album in found)
            {
                _albums.Add(new AlbumRow
                {
                    Album = album,
                    Title = album.Title,
                    Subtitle = album.SubtitleText,
                    Summary = album.SummaryText,
                    Thumbnail = LoadBitmap(album.CoverArt, 116)
                });
            }

            EmptyLibraryText.Visibility = _albums.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_albums.Count > 0) AlbumList.SelectedIndex = 0;
        }
        finally
        {
            UiState.Current.IsWorking = false;
        }
    }

    private void AlbumList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _tracks.Clear();
        if (AlbumList.SelectedItem is not AlbumRow row) return;

        foreach (var track in row.Album.Tracks)
        {
            _tracks.Add(new TrackRow { Track = track });
        }

        MarkNowPlaying();
    }

    // ================= Playback =================

    private void TrackList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TrackList.SelectedIndex >= 0) PlayIndex(TrackList.SelectedIndex);
    }

    private void PlayIndex(int index)
    {
        if (index < 0 || index >= _tracks.Count) return;

        var track = _tracks[index].Track;
        if (!File.Exists(track.FilePath))
        {
            NowPlayingText.Text = $"Missing: {Path.GetFileName(track.FilePath)}";
            return;
        }

        _currentIndex = index;
        _player.Open(new Uri(track.FilePath));
        _player.Play();
        _isPlaying = true;

        PlayPauseButton.Content = "❚❚";
        PlayPauseButton.SetValue(AutomationProperties.NameProperty, "Pause");
        NowPlayingText.Text = $"{track.Title}";

        if (AlbumList.SelectedItem is AlbumRow row)
        {
            NowPlayingArt.Source = LoadBitmap(row.Album.CoverArt, 128);
        }

        MarkNowPlaying();
        _tick.Start();
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0)
        {
            // Nothing cued: start at whatever is selected, or the top of the album.
            PlayIndex(TrackList.SelectedIndex >= 0 ? TrackList.SelectedIndex : 0);
            return;
        }

        if (_isPlaying)
        {
            _player.Pause();
            _isPlaying = false;
            PlayPauseButton.Content = "▶";
            PlayPauseButton.SetValue(AutomationProperties.NameProperty, "Play");
            _tick.Stop();
        }
        else
        {
            _player.Play();
            _isPlaying = true;
            PlayPauseButton.Content = "❚❚";
            PlayPauseButton.SetValue(AutomationProperties.NameProperty, "Pause");
            _tick.Start();
        }
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e)
    {
        // Restart the current track first, as every music player does, and only
        // step back when already near the start.
        if (_player.Position > TimeSpan.FromSeconds(3))
        {
            _player.Position = TimeSpan.Zero;
            return;
        }

        if (_currentIndex > 0) PlayIndex(_currentIndex - 1);
    }

    private void NextButton_Click(object sender, RoutedEventArgs e) => PlayNext();

    private void PlayNext()
    {
        if (_currentIndex + 1 < _tracks.Count) PlayIndex(_currentIndex + 1);
        else StopPlayback();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => StopPlayback();

    private void StopPlayback()
    {
        _player.Stop();
        _tick.Stop();
        _isPlaying = false;
        _currentIndex = -1;

        PlayPauseButton.Content = "▶";
        PlayPauseButton.SetValue(AutomationProperties.NameProperty, "Play");
        NowPlayingText.Text = "Nothing playing";
        TimeText.Text = string.Empty;
        SeekSlider.Value = 0;

        MarkNowPlaying();
    }

    private void MarkNowPlaying()
    {
        for (var i = 0; i < _tracks.Count; i++)
        {
            _tracks[i].IsPlaying = i == _currentIndex;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_player.NaturalDuration.HasTimeSpan) return;

        var total = _player.NaturalDuration.TimeSpan;
        var position = _player.Position;

        // Leave the slider alone while the user is dragging it.
        if (!_isSeeking && total > TimeSpan.Zero)
        {
            SeekSlider.Value = position.TotalSeconds / total.TotalSeconds;
        }

        TimeText.Text = $"{Format(position)} / {Format(total)}";
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void SeekSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _isSeeking = true;

    private void SeekSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isSeeking = false;

        if (_player.NaturalDuration.HasTimeSpan)
        {
            var total = _player.NaturalDuration.TimeSpan;
            _player.Position = TimeSpan.FromSeconds(SeekSlider.Value * total.TotalSeconds);
        }
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return;
        _player.Volume = e.NewValue;
    }

    /// <summary>
    /// Decodes cover art for display. <paramref name="decodeWidth"/> keeps a wall
    /// of 3000x3000 masters from being decoded at full size for a 58px thumbnail.
    /// </summary>
    private static BitmapImage? LoadBitmap(byte[]? data, int decodeWidth)
    {
        if (data is not { Length: > 0 }) return null;

        try
        {
            var bitmap = new BitmapImage();
            using var stream = new MemoryStream(data);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = decodeWidth;
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

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
