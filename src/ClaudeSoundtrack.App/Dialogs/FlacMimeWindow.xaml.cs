using System.Windows;

namespace ClaudeSoundtrack.App.Dialogs;

/// <summary>
/// Offers to correct the <c>.flac</c> MIME registration.
///
/// Shown once, at startup, and only when the registration is actually wrong -
/// a warning that appears on a healthy machine trains people to dismiss it.
/// </summary>
public partial class FlacMimeWindow : PanelWindow
{
    /// <summary>True when the user asked not to be told again.</summary>
    public bool SuppressFutureChecks => DontAskCheck.IsChecked == true;

    /// <summary>True once the registration has been corrected.</summary>
    public bool WasFixed { get; private set; }

    public FlacMimeWindow(FlacMimeCheck.Report report)
    {
        InitializeComponent();

        SubtitleText.Text = report.Culprit is null
            ? "This stops uploads to YouTube Music from Firefox."
            : $"{report.Culprit} registered it, and it stops Firefox uploads to YouTube Music.";

        CurrentText.Text = report.State == FlacMimeCheck.State.Missing
            ? "(nothing registered)"
            : report.CurrentValue ?? "(unknown)";
    }

    private void FixButton_Click(object sender, RoutedEventArgs e)
    {
        var problem = FlacMimeCheck.Fix();

        if (problem is null)
        {
            WasFixed = true;
            StatusText.Text = "Fixed. Restart Firefox for it to take effect.";
            FixButton.IsEnabled = false;
            FixButton.Content = "Fixed";

            // Nothing left to warn about, so don't warn again.
            DontAskCheck.IsChecked = true;

            // Leave it on screen briefly so the confirmation is actually seen.
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            timer.Tick += (_, _) => { timer.Stop(); DialogResult = true; };
            timer.Start();
            return;
        }

        StatusText.Text = $"Could not change it: {problem}";
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
