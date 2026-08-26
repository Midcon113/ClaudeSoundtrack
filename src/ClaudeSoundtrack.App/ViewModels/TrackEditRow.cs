using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using ClaudeSoundtrack.Core.Models;

namespace ClaudeSoundtrack.App.ViewModels;

/// <summary>
/// One editable row in the manual tag editor, wrapping a <see cref="SoundtrackTrack"/>.
///
/// The wrapper exists so edits raise change notifications and so the grid can
/// show read-only context - where the track came from, what file it is - beside
/// the fields being edited. Edits are pushed back onto the underlying track only
/// when the user saves.
/// </summary>
public sealed class TrackEditRow : INotifyPropertyChanged
{
    private string _title;
    private string _artist;

    public TrackEditRow(SoundtrackTrack track)
    {
        Track = track;
        _title = track.Title;
        _artist = track.Artist;
    }

    public SoundtrackTrack Track { get; }

    public int FlatTrackNumber => Track.FlatTrackNumber;

    public string Title
    {
        get => _title;
        set
        {
            if (_title == value) return;
            _title = value;
            OnPropertyChanged();
        }
    }

    public string Artist
    {
        get => _artist;
        set
        {
            if (_artist == value) return;
            _artist = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Where the track physically came from, e.g. "D2 T03".</summary>
    public string SourceText => $"D{Track.SourceDiscNumber} T{Track.SourceTrackNumber:D2}";

    public string DurationText => Track.Duration.TotalHours >= 1
        ? Track.Duration.ToString(@"h\:mm\:ss")
        : Track.Duration.ToString(@"m\:ss");

    public string FileNameText => string.IsNullOrEmpty(Track.FilePath)
        ? "(not ripped)"
        : Path.GetFileName(Track.FilePath);

    /// <summary>True when the user changed something that needs writing.</summary>
    public bool IsDirty => _title != Track.Title || _artist != Track.Artist;

    /// <summary>Copies the edited values back onto the underlying track.</summary>
    public void ApplyToTrack()
    {
        Track.Title = _title;
        Track.Artist = _artist;
    }

    /// <summary>Re-reads display-only values after a save changed the file name.</summary>
    public void RefreshFileName() => OnPropertyChanged(nameof(FileNameText));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
