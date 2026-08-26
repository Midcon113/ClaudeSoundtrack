using System.Windows;
using ClaudeSoundtrack.Core.Models;

namespace ClaudeSoundtrack.App.Dialogs;

/// <summary>
/// Tells the user the album passed and how to upload it.
///
/// The dialog deliberately offers a way out: the readiness check can only verify
/// what is mechanically checkable, and the user may still know the album is
/// wrong. Choosing "Not Ready" returns them to the tag editor rather than
/// opening the folder.
/// </summary>
public partial class UploadInstructionsWindow : Window
{
    public UploadInstructionsWindow(AlbumProject project)
    {
        InitializeComponent();

        var discs = project.RippedDiscs.Count;
        var discText = discs switch
        {
            0 or 1 => "single disc",
            var n => $"{n} discs flattened into one"
        };

        SummaryText.Text = $"{project.AlbumTitle} - {project.TotalTrackCount} tracks, {discText}.";
        FolderText.Text = project.OutputFolder ?? "(unknown)";
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void EditButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
