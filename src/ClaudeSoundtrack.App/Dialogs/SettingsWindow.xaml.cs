using System.IO;
using System.Windows;
using ClaudeSoundtrack.Core.Models;

namespace ClaudeSoundtrack.App.Dialogs;

/// <summary>
/// Settings, with the lighting controls previewing live.
///
/// The preview matters: "glow strength" is a number that means nothing until you
/// see it, so the slider drives the real lamps in the dialog rather than being
/// applied on OK. Cancel restores whatever was in force on entry, so dragging the
/// slider around and then backing out leaves no trace.
/// </summary>
public partial class SettingsWindow : PanelWindow
{
    private readonly AppSettings _settings;

    // What to restore if the user cancels after previewing.
    private readonly double _bloomOnEntry;
    private readonly bool _animateOnEntry;
    private readonly double _speedOnEntry;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();

        _settings = settings;

        _bloomOnEntry = UiState.Current.Bloom;
        _animateOnEntry = UiState.Current.AnimateLamps;
        _speedOnEntry = UiState.Current.LampSpeed;

        BloomSlider.Value = settings.BloomIntensity;
        AnimateCheck.IsChecked = settings.AnimateLamps;
        SpeedSlider.Value = settings.LampSpeed;
        MusicFolderBox.Text = settings.MusicFolderOverride ?? string.Empty;

        UpdateValueLabels();
        UpdateResolvedFolder();

        SettingsPathText.Text = settings.FilePath is null
            ? "Using defaults; settings will be saved beside the application."
            : $"Settings file: {settings.FilePath}";

        // The preview bank only animates while something is "working", so make the
        // dialog itself count as work for as long as it is open.
        Loaded += (_, _) => UiState.Current.IsWorking = true;
        Closed += (_, _) => UiState.Current.IsWorking = false;
    }

    private void UpdateValueLabels()
    {
        // Shown as a percentage of the reference look rather than a raw multiplier,
        // which is meaningless without knowing the scale.
        BloomValueText.Text = $"{BloomSlider.Value / 0.5:P0}".Replace(" ", "");
        SpeedValueText.Text = $"{SpeedSlider.Value:0.00}x";
    }

    private void UpdateResolvedFolder()
    {
        var probe = new AppSettings { MusicFolderOverride = NormalisedFolder() };
        ResolvedFolderText.Text = $"Albums will be written to: {probe.ResolveMusicFolder()}";
    }

    /// <summary>Empty or missing folders fall back to the default rather than being stored.</summary>
    private string? NormalisedFolder()
    {
        var text = MusicFolderBox.Text?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private void BloomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return;

        // Same doubling the renderer uses, so the preview is the real thing.
        UiState.Current.Bloom = Math.Clamp(e.NewValue, 0, 1) * 2;
        UpdateValueLabels();
    }

    private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return;

        UiState.Current.LampSpeed = e.NewValue;
        UpdateValueLabels();
    }

    private void AnimateCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;

        UiState.Current.AnimateLamps = AnimateCheck.IsChecked == true;
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the folder albums are written to",
            InitialDirectory = _settings.ResolveMusicFolder()
        };

        if (dialog.ShowDialog(this) == true)
        {
            MusicFolderBox.Text = dialog.FolderName;
            UpdateResolvedFolder();
        }
    }

    private void DefaultFolder_Click(object sender, RoutedEventArgs e)
    {
        MusicFolderBox.Text = string.Empty;
        UpdateResolvedFolder();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = NormalisedFolder();

        if (folder is not null && !Directory.Exists(folder))
        {
            var answer = MessageBox.Show(this,
                $"That folder does not exist:\n\n{folder}\n\nCreate it?",
                "ClaudeSoundtrack", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel) return;

            if (answer == MessageBoxResult.Yes)
            {
                try
                {
                    Directory.CreateDirectory(folder);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Could not create it:\n\n{ex.Message}",
                        "ClaudeSoundtrack", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            else
            {
                // Keep the default rather than storing a path that does not exist.
                folder = null;
            }
        }

        _settings.BloomIntensity = BloomSlider.Value;
        _settings.AnimateLamps = AnimateCheck.IsChecked == true;
        _settings.LampSpeed = SpeedSlider.Value;
        _settings.MusicFolderOverride = folder;

        if (!_settings.Save())
        {
            MessageBox.Show(this,
                "Settings could not be written to disk, so they will apply for this session only.",
                "ClaudeSoundtrack", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        UiState.Current.ApplyFrom(_settings);
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Cancel();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Cancel();

    /// <summary>Discards the live preview and closes.</summary>
    private void Cancel()
    {
        UiState.Current.Bloom = _bloomOnEntry;
        UiState.Current.AnimateLamps = _animateOnEntry;
        UiState.Current.LampSpeed = _speedOnEntry;

        DialogResult = false;
    }
}
