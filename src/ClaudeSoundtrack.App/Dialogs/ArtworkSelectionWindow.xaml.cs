using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using ClaudeSoundtrack.Core.Models;

namespace ClaudeSoundtrack.App.Dialogs;

/// <summary>
/// Asks the user to confirm a cover before anything is written to the files.
///
/// Confirmation is required rather than advisory: automatic art for a limited
/// soundtrack pressing is frequently the wrong edition's cover, and once it is
/// embedded across eighty tracks, undoing it means re-tagging the lot.
/// </summary>
public partial class ArtworkSelectionWindow : PanelWindow
{
    /// <summary>A candidate wrapped for display, with its thumbnail decoded.</summary>
    private sealed class CandidateRow
    {
        public required ArtworkCandidate Candidate { get; init; }
        public required string Source { get; init; }
        public required string Resolution { get; init; }
        public required string Quality { get; init; }
        public BitmapImage? Thumbnail { get; init; }
    }

    private readonly List<CandidateRow> _rows = new();

    /// <summary>The candidate the user accepted, or null if they rejected them all.</summary>
    public ArtworkCandidate? SelectedCandidate { get; private set; }

    public ArtworkSelectionWindow(IReadOnlyList<ArtworkCandidate> candidates, string albumTitle)
    {
        InitializeComponent();

        SubtitleText.Text = $"{candidates.Count} candidate(s) found for \"{albumTitle}\". " +
                            "Check the cover matches this edition, not the original release.";

        foreach (var candidate in candidates)
        {
            _rows.Add(new CandidateRow
            {
                Candidate = candidate,
                Source = candidate.Source,
                Resolution = $"{candidate.ResolutionText} px",
                Quality = DescribeQuality(candidate),
                Thumbnail = LoadBitmap(candidate.ImageData, decodeWidth: 132)
            });
        }

        CandidateList.ItemsSource = _rows;
        if (_rows.Count > 0) CandidateList.SelectedIndex = 0;
    }

    private static string DescribeQuality(ArtworkCandidate candidate)
    {
        var edge = Math.Min(candidate.Width, candidate.Height);
        return edge switch
        {
            >= 1400 => "Excellent",
            >= 1000 => "High resolution",
            >= 600 => "Acceptable",
            _ => "Low resolution"
        };
    }

    /// <summary>
    /// Decodes bytes into a bitmap.
    ///
    /// <paramref name="decodeWidth"/> keeps the list responsive: a search can
    /// return several 3000x3000 masters, and decoding all of them at full size
    /// costs far more memory than the thumbnails need.
    /// </summary>
    private static BitmapImage? LoadBitmap(byte[]? data, int decodeWidth = 0)
    {
        if (data is not { Length: > 0 }) return null;

        try
        {
            var bitmap = new BitmapImage();
            using var stream = new MemoryStream(data);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0) bitmap.DecodePixelWidth = decodeWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            // A candidate that will not decode is simply not shown as a preview.
            return null;
        }
    }

    private void CandidateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CandidateList.SelectedItem is not CandidateRow row)
        {
            AcceptButton.IsEnabled = false;
            return;
        }

        // Full size here: this preview is what the decision is made on.
        PreviewImage.Source = LoadBitmap(row.Candidate.ImageData);

        DetailSourceText.Text = row.Source.ToUpperInvariant();

        var release = row.Candidate.ReleaseTitle;
        var year = row.Candidate.ReleaseYear;
        DetailReleaseText.Text = string.IsNullOrWhiteSpace(release)
            ? "No release name supplied by this source."
            : string.IsNullOrWhiteSpace(year) ? release : $"{release}  ({year})";

        DetailQualityText.Text = $"{row.Resolution}  ·  {row.Quality}";

        AcceptButton.IsEnabled = true;
    }

    private void AcceptButton_Click(object sender, RoutedEventArgs e)
    {
        if (CandidateList.SelectedItem is not CandidateRow row) return;

        SelectedCandidate = row.Candidate;
        DialogResult = true;
    }

    private void RejectButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedCandidate = null;
        DialogResult = false;
    }
}
