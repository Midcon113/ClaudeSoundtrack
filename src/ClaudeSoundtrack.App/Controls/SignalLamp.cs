using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClaudeSoundtrack.App.Controls;

/// <summary>
/// An indicator lamp: chrome bezel, coloured glass, and - when lit - a soft halo
/// that bleeds onto the plate behind it.
///
/// The halo is what makes a bank of these read as a live panel rather than a row
/// of coloured circles, and it is the part the Bloom setting scales. The halo
/// holds full strength out to the rim of the lamp and only falls off beyond it;
/// without that plateau the bright part of the gradient hides underneath the
/// glass and only the weak tail shows, which looks like fog rather than light.
///
/// Drawn rather than composed from shapes because the halo needs to overflow the
/// control's own bounds, and because a panel can hold a lot of these at once.
/// </summary>
public sealed class SignalLamp : Control
{
    static SignalLamp()
    {
        // Lamps never take focus or clicks; they are pure indicators.
        FocusableProperty.OverrideMetadata(typeof(SignalLamp), new FrameworkPropertyMetadata(false));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(SignalLamp), new FrameworkPropertyMetadata(false));
    }

    /// <summary>The lamp's colour when lit.</summary>
    public static readonly DependencyProperty LampColorProperty = DependencyProperty.Register(
        nameof(LampColor), typeof(Color), typeof(SignalLamp),
        new FrameworkPropertyMetadata(Color.FromRgb(0xFF, 0xA6, 0x2B), FrameworkPropertyMetadataOptions.AffectsRender));

    public Color LampColor
    {
        get => (Color)GetValue(LampColorProperty);
        set => SetValue(LampColorProperty, value);
    }

    /// <summary>Whether the lamp is lit.</summary>
    public static readonly DependencyProperty IsLitProperty = DependencyProperty.Register(
        nameof(IsLit), typeof(bool), typeof(SignalLamp),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool IsLit
    {
        get => (bool)GetValue(IsLitProperty);
        set => SetValue(IsLitProperty, value);
    }

    /// <summary>
    /// How brightly this particular lamp burns, 0 to 1, on top of the global
    /// Bloom. The animated header row uses it to fade lamps in and out.
    /// </summary>
    public static readonly DependencyProperty IntensityProperty = DependencyProperty.Register(
        nameof(Intensity), typeof(double), typeof(SignalLamp),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Intensity
    {
        get => (double)GetValue(IntensityProperty);
        set => SetValue(IntensityProperty, value);
    }

    /// <summary>
    /// The global glow multiplier, 0 to 2. Bound to <see cref="UiState"/> so the
    /// Settings slider moves every lamp in the app at once.
    /// </summary>
    public static readonly DependencyProperty BloomProperty = DependencyProperty.Register(
        nameof(Bloom), typeof(double), typeof(SignalLamp),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Bloom
    {
        get => (double)GetValue(BloomProperty);
        set => SetValue(BloomProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;

        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = size / 2;

        var bloom = Math.Clamp(Bloom, 0, 2);
        var intensity = Math.Clamp(Intensity, 0, 1);
        var colour = LampColor;

        if (IsLit && intensity > 0 && bloom > 0.01)
        {
            DrawHalo(dc, centre, radius, colour, bloom, intensity);
        }

        // Bezel: a machined ring, lit from the upper left like everything else here.
        var bezel = new LinearGradientBrush(
            Color.FromRgb(0xA8, 0xA0, 0x90),
            Color.FromRgb(0x3A, 0x36, 0x30),
            new Point(0.2, 0), new Point(0.8, 1));
        bezel.Freeze();
        dc.DrawEllipse(bezel, null, centre, radius, radius);

        // Glass. An unlit lamp keeps some of its colour, so a dark bank still
        // reads as red/green/amber glass rather than a row of empty holes.
        var glassRadius = radius * 0.83;
        var lit = IsLit && intensity > 0;

        var core = lit
            ? Blend(colour, Colors.White, 0.5 + 0.1 * Math.Min(bloom, 2))
            : Blend(colour, Colors.Black, 0.76);
        var rim = lit
            ? Blend(colour, Colors.Black, 0.20)
            : Blend(colour, Colors.Black, 0.90);

        var glass = new RadialGradientBrush(core, rim)
        {
            GradientOrigin = new Point(0.38, 0.34),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        glass.Freeze();
        dc.DrawEllipse(glass, null, centre, glassRadius, glassRadius);

        // Specular highlight - present lit or dark, because the glass is glass either way.
        var specBrush = new SolidColorBrush(Color.FromArgb((byte)(lit ? 130 : 70), 255, 255, 255));
        specBrush.Freeze();
        dc.DrawEllipse(
            specBrush, null,
            new Point(centre.X - glassRadius * 0.22, centre.Y - glassRadius * 0.30),
            glassRadius * 0.34, glassRadius * 0.24);
    }

    /// <summary>
    /// Paints the halo that bleeds onto the plate around the lamp.
    ///
    /// The plateau stop keeps the gradient at full strength as far as the lamp's
    /// own rim, so the visible falloff starts where the glass ends rather than
    /// being hidden beneath it.
    /// </summary>
    private static void DrawHalo(DrawingContext dc, Point centre, double radius, Color colour, double bloom, double intensity)
    {
        var reach = radius * (1 + 1.1 * Math.Min(bloom, 2));
        var plateau = Math.Clamp(radius / reach, 0.05, 0.9);
        var alpha = (byte)Math.Clamp(150 * intensity * bloom, 0, 255);

        var halo = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(alpha, colour.R, colour.G, colour.B), 0),
                new GradientStop(Color.FromArgb(alpha, colour.R, colour.G, colour.B), plateau),
                new GradientStop(Color.FromArgb(0, colour.R, colour.G, colour.B), 1)
            }
        };
        halo.Freeze();

        dc.DrawEllipse(halo, null, centre, reach, reach);
    }

    /// <summary>Mixes <paramref name="amount"/> of <paramref name="b"/> into <paramref name="a"/>.</summary>
    private static Color Blend(Color a, Color b, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * amount),
            (byte)(a.G + (b.G - a.G) * amount),
            (byte)(a.B + (b.B - a.B) * amount));
    }
}
