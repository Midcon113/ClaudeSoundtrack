using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClaudeSoundtrack.App.Controls;

/// <summary>
/// A row of signal lamps that comes alive while the app is working.
///
/// This is the panel's activity indicator, and it exists because ripping a disc
/// takes forty minutes during which a progress bar barely moves. A bank of lamps
/// running through a pattern says "still alive" at a glance from across the room,
/// which a numeric percentage does not.
///
/// Idle, the lamps sit dark but still coloured, exactly as unlit glass would.
/// </summary>
public sealed class LampBank : Control
{
    /// <summary>
    /// Redraw rate. 20Hz is fast enough that the sweep reads as motion rather
    /// than stepping, and slow enough to be invisible next to the cost of the
    /// rip itself.
    /// </summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(50);

    private readonly DispatcherTimer _timer;
    private readonly List<SignalLamp> _lamps = new();
    private StackPanel? _host;
    private double _phase;

    public LampBank()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = Tick };
        _timer.Tick += OnTick;

        Loaded += (_, _) =>
        {
            UiState.Current.PropertyChanged += OnStateChanged;
            UpdateRunState();
        };

        Unloaded += (_, _) =>
        {
            UiState.Current.PropertyChanged -= OnStateChanged;
            _timer.Stop();
        };
    }

    /// <summary>How many lamps to show.</summary>
    public static readonly DependencyProperty LampCountProperty = DependencyProperty.Register(
        nameof(LampCount), typeof(int), typeof(LampBank),
        new PropertyMetadata(7, (d, _) => ((LampBank)d).Rebuild()));

    public int LampCount
    {
        get => (int)GetValue(LampCountProperty);
        set => SetValue(LampCountProperty, value);
    }

    /// <summary>Diameter of each lamp.</summary>
    public static readonly DependencyProperty LampSizeProperty = DependencyProperty.Register(
        nameof(LampSize), typeof(double), typeof(LampBank),
        new PropertyMetadata(10.0, (d, _) => ((LampBank)d).Rebuild()));

    public double LampSize
    {
        get => (double)GetValue(LampSizeProperty);
        set => SetValue(LampSizeProperty, value);
    }

    /// <summary>Lamp colour.</summary>
    public static readonly DependencyProperty LampColorProperty = DependencyProperty.Register(
        nameof(LampColor), typeof(Color), typeof(LampBank),
        new PropertyMetadata(Color.FromRgb(0xFF, 0xA6, 0x2B), (d, _) => ((LampBank)d).Rebuild()));

    public Color LampColor
    {
        get => (Color)GetValue(LampColorProperty);
        set => SetValue(LampColorProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        Rebuild();
    }

    protected override int VisualChildrenCount => _host is null ? 0 : 1;

    protected override Visual GetVisualChild(int index) =>
        _host ?? throw new ArgumentOutOfRangeException(nameof(index));

    protected override Size MeasureOverride(Size constraint)
    {
        EnsureHost();
        _host!.Measure(constraint);
        return _host.DesiredSize;
    }

    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        EnsureHost();
        _host!.Arrange(new Rect(arrangeBounds));
        return arrangeBounds;
    }

    private void EnsureHost()
    {
        if (_host is not null) return;

        _host = new StackPanel { Orientation = Orientation.Horizontal };
        AddVisualChild(_host);
        AddLogicalChild(_host);
        Rebuild();
    }

    /// <summary>Rebuilds the row after a property that changes its shape.</summary>
    private void Rebuild()
    {
        if (_host is null) return;

        _host.Children.Clear();
        _lamps.Clear();

        var count = Math.Clamp(LampCount, 1, 64);
        var gap = Math.Max(2, LampSize * 0.45);

        for (var i = 0; i < count; i++)
        {
            var lamp = new SignalLamp
            {
                Width = LampSize,
                Height = LampSize,
                LampColor = LampColor,
                IsLit = true,
                Intensity = 0,
                Margin = new Thickness(i == 0 ? 0 : gap, 0, 0, 0)
            };

            // Bind each lamp to the shared glow setting so the Settings slider
            // moves them live.
            lamp.SetBinding(SignalLamp.BloomProperty, new Binding(nameof(UiState.Bloom))
            {
                Source = UiState.Current
            });

            _lamps.Add(lamp);
            _host.Children.Add(lamp);
        }

        InvalidateMeasure();
        UpdateRunState();
    }

    private void OnStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UiState.IsWorking) or nameof(UiState.AnimateLamps))
        {
            UpdateRunState();
        }
    }

    /// <summary>Starts or stops the sweep, and darkens the row when it stops.</summary>
    private void UpdateRunState()
    {
        var shouldRun = IsLoaded && UiState.Current.IsWorking && UiState.Current.AnimateLamps;

        if (shouldRun)
        {
            if (!_timer.IsEnabled) _timer.Start();
            return;
        }

        _timer.Stop();

        // A steady, dim glow when idle: the panel still looks powered, but nothing
        // is moving to distract from the rest of the screen.
        var resting = UiState.Current.IsWorking ? 0.55 : 0.0;
        foreach (var lamp in _lamps) lamp.Intensity = resting;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _phase += 0.09 * Math.Clamp(UiState.Current.LampSpeed, 0.25, 3);

        for (var i = 0; i < _lamps.Count; i++)
        {
            // A travelling wave along the row. Raising the cosine to a power
            // tightens the bright band so it reads as a moving light rather than
            // the whole row breathing together.
            var offset = _phase - i * 0.55;
            var wave = (Math.Cos(offset) + 1) / 2;
            _lamps[i].Intensity = Math.Pow(wave, 3);
        }
    }
}
