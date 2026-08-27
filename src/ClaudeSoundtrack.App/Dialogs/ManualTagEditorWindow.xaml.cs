using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using ClaudeSoundtrack.App.ViewModels;
using ClaudeSoundtrack.Core.Models;
using ClaudeSoundtrack.Core.Services;

namespace ClaudeSoundtrack.App.Dialogs;

/// <summary>
/// Track-by-track tag editor: the fallback when the readiness check fails, or
/// when the user simply does not agree that the album is ready.
///
/// Everything here writes directly to the FLAC files, so the readiness check can
/// be re-run against disk afterwards and mean something.
/// </summary>
public partial class ManualTagEditorWindow : PanelWindow
{
    private readonly AlbumProject _project;
    private readonly TaggingService _tagger;
    private readonly ObservableCollection<TrackEditRow> _rows = new();

    public ManualTagEditorWindow(AlbumProject project, TaggingService tagger)
    {
        InitializeComponent();

        _project = project;
        _tagger = tagger;

        AlbumTitleBox.Text = project.AlbumTitle;
        AlbumArtistBox.Text = project.AlbumArtist;
        YearBox.Text = project.Year?.ToString() ?? string.Empty;
        GenreBox.Text = project.Genre;

        foreach (var track in project.Tracks.OrderBy(t => t.FlatTrackNumber))
        {
            _rows.Add(new TrackEditRow(track));
        }

        TrackGrid.ItemsSource = _rows;
        StatusText.Text = $"{_rows.Count} track(s) loaded from disk.";
    }

    /// <summary>
    /// Strips a leading track number from every title in one pass.
    ///
    /// Worth a dedicated button because it is the single most common cleanup:
    /// metadata sources hand back "01 - Main Titles" wholesale, and fixing eighty
    /// of those by hand is not reasonable.
    /// </summary>
    private void StripNumbersButton_Click(object sender, RoutedEventArgs e)
    {
        var changed = 0;

        foreach (var row in _rows)
        {
            var stripped = FileNaming.StripLeadingTrackNumber(row.Title);
            if (stripped != row.Title)
            {
                row.Title = stripped;
                changed++;
            }
        }

        StatusText.Text = changed == 0
            ? "No titles had a leading track number."
            : $"{changed} title(s) cleaned. Press Save All to write them.";
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // Commit any cell still in edit mode, or the last typed value is lost.
        TrackGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

        _project.AlbumTitle = AlbumTitleBox.Text.Trim();
        _project.AlbumArtist = AlbumArtistBox.Text.Trim();
        _project.Year = int.TryParse(YearBox.Text?.Trim(), out var year) && year > 0 ? year : null;
        _project.Genre = string.IsNullOrWhiteSpace(GenreBox.Text) ? "Soundtrack" : GenreBox.Text.Trim();

        foreach (var row in _rows) row.ApplyToTrack();

        SaveButton.IsEnabled = false;
        StatusText.Text = "Saving...";

        try
        {
            var renamed = await Task.Run(RenameFilesToMatchTitles);

            var progress = new Progress<(int Done, int Total)>(p =>
                StatusText.Text = $"Writing tags {p.Done} of {p.Total}...");

            var written = await Task.Run(() => _tagger.WriteAllTags(_project, progress));

            foreach (var row in _rows) row.RefreshFileName();

            StatusText.Text = renamed > 0
                ? $"{written} file(s) tagged, {renamed} renamed."
                : $"{written} file(s) tagged.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Save failed: {ex.Message}";
            MessageBox.Show(this, ex.Message, "ClaudeSoundtrack", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Renames files so they match their edited titles, still without a track
    /// number in the name.
    ///
    /// Uniqueness is computed across the whole album rather than per file: two
    /// tracks edited to the same title would otherwise collide, and the second
    /// rename would destroy the first file.
    /// </summary>
    /// <returns>How many files were renamed.</returns>
    private int RenameFilesToMatchTitles()
    {
        if (string.IsNullOrEmpty(_project.OutputFolder)) return 0;

        var ordered = _project.Tracks.OrderBy(t => t.FlatTrackNumber).ToList();
        var desiredNames = FileNaming.BuildUniqueFileNames(ordered.Select(t => t.Title));
        var renamed = 0;

        for (var i = 0; i < ordered.Count; i++)
        {
            var track = ordered[i];
            if (string.IsNullOrEmpty(track.FilePath) || !File.Exists(track.FilePath)) continue;

            var currentName = Path.GetFileName(track.FilePath);
            if (string.Equals(currentName, desiredNames[i], StringComparison.Ordinal)) continue;

            var target = Path.Combine(_project.OutputFolder, desiredNames[i]);

            try
            {
                // A same-name-different-case rename needs the two-step dance on
                // Windows, since File.Move treats the paths as already equal.
                if (string.Equals(track.FilePath, target, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(track.FilePath, target, StringComparison.Ordinal))
                {
                    var intermediate = target + ".rename";
                    File.Move(track.FilePath, intermediate);
                    File.Move(intermediate, target);
                }
                else
                {
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(track.FilePath, target);
                }

                track.FilePath = target;
                renamed++;
            }
            catch (IOException)
            {
                // Locked by a player or indexer. The tag write still succeeds,
                // and the readiness check will report the stale name.
            }
        }

        return renamed;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
