using System.Windows;
using ClaudeSoundtrack.Core.Services;

namespace ClaudeSoundtrack.App.Dialogs;

/// <summary>
/// Shows what a repair will change and waits for a yes. Nothing is written here.
/// </summary>
public partial class RepairAlbumWindow : PanelWindow
{
    public RepairAlbumWindow(AlbumRepairPlan plan)
    {
        InitializeComponent();
        PreviewText.Text = string.Join("\n\n", plan.Lines);
        if (!plan.ChangesAnything)
        {
            PreviewText.Text = "These files already use the cleaned album title. Nothing to change.";
            ConfirmButton.IsEnabled = false;
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
