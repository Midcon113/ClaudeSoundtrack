using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClaudeSoundtrack.Core.Models;

namespace ClaudeSoundtrack.App;

/// <summary>
/// Live UI state that controls bind to, so a change in Settings takes effect on
/// screen immediately rather than at the next restart.
///
/// A singleton because the lamps are scattered across several windows and all of
/// them answer to the same glow setting; threading a settings object through
/// every control would buy nothing.
/// </summary>
public sealed class UiState : INotifyPropertyChanged
{
    public static UiState Current { get; } = new();

    private double _bloom = 1.0;
    private bool _animateLamps = true;
    private double _lampSpeed = 1.0;
    private bool _isWorking;

    private UiState() { }

    /// <summary>
    /// Glow multiplier, 0 to 2. 1 is the reference look; 0 removes the halo
    /// entirely, leaving the lamps as plain coloured glass.
    /// </summary>
    public double Bloom
    {
        get => _bloom;
        set => Set(ref _bloom, Math.Clamp(value, 0, 2));
    }

    /// <summary>Whether the header lamps animate while work is running.</summary>
    public bool AnimateLamps
    {
        get => _animateLamps;
        set => Set(ref _animateLamps, value);
    }

    /// <summary>Animation speed multiplier, 0.25 to 3.</summary>
    public double LampSpeed
    {
        get => _lampSpeed;
        set => Set(ref _lampSpeed, Math.Clamp(value, 0.25, 3));
    }

    /// <summary>
    /// True while a rip or other long job is running. The header lamps only run
    /// when something is actually happening, so an idle panel is still.
    /// </summary>
    public bool IsWorking
    {
        get => _isWorking;
        set => Set(ref _isWorking, value);
    }

    /// <summary>Copies the persisted settings into the live state.</summary>
    public void ApplyFrom(AppSettings settings)
    {
        Bloom = settings.BloomMultiplier;
        AnimateLamps = settings.AnimateLamps;
        LampSpeed = settings.LampSpeed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
