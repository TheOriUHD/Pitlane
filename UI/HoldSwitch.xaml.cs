using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using SimRigMode.Models;
using VirtualKey = Windows.System.VirtualKey;
using Windows.UI;

namespace SimRigMode.UI;

/// <summary>
/// The Desktop / Sim Rig switch. It only switches when held: the lit half stretches towards the
/// other side for three seconds, taking on that side's colour, then snaps over. Letting go
/// earlier springs it back and nothing happens.
///
/// Everything that moves is a composition animation driven by two numbers in <see cref="_state"/>:
/// T, how far the hold has got (0-1), and C, how far the snap to the other side has got (0-1).
/// The thumb's edges, colours, glow and the label opacities are expressions of those two, so they
/// stay in step by construction, and they stay smooth while the UI thread is busy with a switch.
/// </summary>
public sealed partial class HoldSwitch : UserControl
{
    public static readonly TimeSpan HoldDuration = TimeSpan.FromSeconds(3);

    private const float Inset = 5;

    /// <summary>Room round the switch for the glow; the shape visual would clip it otherwise.</summary>
    private const float Bleed = 30;
    private const string LeftEdge = "Lerp(Lerp(p.L0, p.LH, p.T), p.L1, p.C)";
    private const string RightEdge = "Lerp(Lerp(p.R0, p.RH, p.T), p.R1, p.C)";

    /// <summary>The glow: pills a little larger than the thumb, fainter the further out, in the
    /// thumb's colour. A composition drop shadow would blur properly, but its LayerVisual renders
    /// its surface at 1:1 pixels under display scaling and hid the thumb entirely.</summary>
    private static readonly (int Spread, byte Alpha)[] Halo = [(2, 0x30), (5, 0x1E), (9, 0x12), (14, 0x09), (20, 0x04)];

    private static readonly Palette DesktopColors = new(Rgb(59, 111, 245), Rgb(97, 80, 240), Rgb(77, 124, 255));
    private static readonly Palette RigColors = new(Rgb(240, 102, 31), Rgb(226, 61, 46), Rgb(255, 106, 42));

    private readonly Compositor _compositor;
    private readonly CompositionPropertySet _state;
    private readonly Visual _self;
    private readonly ShapeVisual _shapes;
    private readonly List<CompositionRoundedRectangleGeometry> _geometries = [];
    private readonly CompositionSpriteShape _sheen;
    private readonly CompositionLinearGradientBrush _sheenBrush;
    private readonly CompositionEasingFunction _holdEasing;
    private readonly DispatcherQueueTimer _timer;

    private WorkspaceMode _mode = WorkspaceMode.Desktop;
    private bool _holding, _committing, _busy;

    /// <summary>Bumped whenever an animation is superseded, so a stale completion is ignored.</summary>
    private int _generation;

    /// <summary>The hold began; the argument is the mode it would switch to.</summary>
    public event Action<WorkspaceMode>? HoldStarted;
    public event Action? HoldCancelled;
    /// <summary>Held for the full <see cref="HoldDuration"/>: switch to this mode.</summary>
    public event Action<WorkspaceMode>? Confirmed;

    public HoldSwitch()
    {
        InitializeComponent();
        _self = ElementCompositionPreview.GetElementVisual(this);
        _compositor = _self.Compositor;
        _holdEasing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.3f, 0f), new Vector2(0.6f, 1f));

        _state = _compositor.CreatePropertySet();
        foreach (var name in new[] { "T", "C", "L0", "R0", "LH", "RH", "L1", "R1", "K0", "K1", "DA", "DB", "DC", "RA", "RB", "RC" })
            _state.InsertScalar(name, 0);
        _state.InsertScalar("Top", Inset);
        _state.InsertScalar("H", 54);
        _state.InsertScalar("GX", 0);
        _state.InsertScalar("M", Bleed);
        foreach (var name in new[] { "S0", "S1", "G0", "G1", "SG", "GG", "SA", "GA", "SZ", "GZ" })
            _state.InsertColor(name, DesktopColors.Start);
        for (var i = 0; i < Halo.Length; i++) { _state.InsertColor($"SH{i}", DesktopColors.Glow); _state.InsertColor($"GH{i}", DesktopColors.Glow); }

        _shapes = _compositor.CreateShapeVisual();

        // The glow first, so it lies under the thumb. GX spreads it further for a moment when a
        // hold completes.
        for (var i = 0; i < Halo.Length; i++)
        {
            var spread = Halo[i].Spread;
            var geometry = _compositor.CreateRoundedRectangleGeometry();
            Bind(geometry, "Size", $"Vector2({RightEdge} - {LeftEdge} + 2 * ({spread} + p.GX), p.H + 2 * ({spread} + p.GX))");
            Bind(geometry, "CornerRadius", $"Vector2(p.H / 2 + {spread} + p.GX, p.H / 2 + {spread} + p.GX)");
            var halo = _compositor.CreateSpriteShape(geometry);
            var color = _compositor.CreateColorBrush();
            Bind(color, "Color", $"ColorLerpRGB(p.SH{i}, p.GH{i}, p.T)");
            halo.FillBrush = color;
            Bind(halo, "Offset", $"Vector2(p.M + {LeftEdge} - {spread} - p.GX, p.M + p.Top - {spread} - p.GX + 2)");
            _shapes.Shapes.Add(halo);
        }

        // The thumb: a pill whose edges and colours are expressions of T and C.
        var fill = _compositor.CreateLinearGradientBrush();
        fill.StartPoint = new Vector2(0, 0.5f);
        fill.EndPoint = new Vector2(1, 0.5f);
        var start = _compositor.CreateColorGradientStop(0, DesktopColors.Start);
        var end = _compositor.CreateColorGradientStop(1, DesktopColors.End);
        fill.ColorStops.Add(start);
        fill.ColorStops.Add(end);
        // The end that leads the way changes colour in the first half of the hold, the other in
        // the second half, so a held thumb runs from this mode's colour into the other's.
        Bind(start, "Color", "ColorLerpRGB(p.S0, p.G0, Clamp(p.T * 2 - p.K0, 0, 1))");
        Bind(end, "Color", "ColorLerpRGB(p.S1, p.G1, Clamp(p.T * 2 - p.K1, 0, 1))");
        var thumb = _compositor.CreateSpriteShape(Pill());
        thumb.FillBrush = fill;
        Follow(thumb);

        // A glassy top light and a hairline, so the thumb reads as a surface, not a flat fill.
        var gloss = _compositor.CreateSpriteShape(Pill());
        gloss.FillBrush = VerticalGradient((0, Color.FromArgb(0x30, 255, 255, 255)), (0.55f, Color.FromArgb(0, 255, 255, 255)));
        gloss.StrokeBrush = VerticalGradient((0, Color.FromArgb(0x60, 255, 255, 255)), (1, Color.FromArgb(0x10, 255, 255, 255)));
        gloss.StrokeThickness = 1;
        Follow(gloss);

        // While a switch runs, a band of light sweeps across the thumb.
        _sheenBrush = _compositor.CreateLinearGradientBrush();
        _sheenBrush.MappingMode = CompositionMappingMode.Absolute;
        _sheenBrush.StartPoint = new Vector2(0, 0);
        _sheenBrush.EndPoint = new Vector2(90, 30);
        _sheenBrush.ExtendMode = CompositionGradientExtendMode.Clamp;
        _sheenBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0, Color.FromArgb(0, 255, 255, 255)));
        _sheenBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0.5f, Color.FromArgb(0x48, 255, 255, 255)));
        _sheenBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1, Color.FromArgb(0, 255, 255, 255)));
        _sheen = _compositor.CreateSpriteShape(Pill());
        _sheen.FillBrush = _sheenBrush;
        Follow(_sheen);

        _shapes.Shapes.Add(thumb);
        _shapes.Shapes.Add(gloss);
        ElementCompositionPreview.SetElementChildVisual(ThumbHost, _shapes);

        Bind(ElementCompositionPreview.GetElementVisual(DesktopLabel), "Opacity", "Lerp(Lerp(p.DA, p.DB, p.T), p.DC, p.C)");
        Bind(ElementCompositionPreview.GetElementVisual(RigLabel), "Opacity", "Lerp(Lerp(p.RA, p.RB, p.T), p.RC, p.C)");

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = HoldDuration;
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => Complete();

        SizeChanged += (_, _) => ApplySize();
        Loaded += (_, _) => ApplySize();
        Rest(_mode);
    }

    /// <summary>The mode the switch shows at rest.</summary>
    public WorkspaceMode Mode => _mode;

    /// <summary>A switch is running: input is ignored and a band of light sweeps the thumb.</summary>
    public bool IsBusy
    {
        get => _busy;
        set
        {
            if (_busy == value) return;
            _busy = value;
            if (value)
            {
                CancelHold();
                if (!_shapes.Shapes.Contains(_sheen)) _shapes.Shapes.Add(_sheen);
                var sweep = _compositor.CreateVector2KeyFrameAnimation();
                sweep.InsertKeyFrame(0, new Vector2(-120, 0));
                sweep.InsertKeyFrame(0.7f, new Vector2((float)ActualWidth + 30, 0), _compositor.CreateLinearEasingFunction());
                sweep.InsertKeyFrame(1, new Vector2((float)ActualWidth + 30, 0));
                sweep.Duration = TimeSpan.FromMilliseconds(1700);
                sweep.IterationBehavior = AnimationIterationBehavior.Forever;
                _sheenBrush.StartAnimation("Offset", sweep);
            }
            else
            {
                _sheenBrush.StopAnimation("Offset");
                _shapes.Shapes.Remove(_sheen);
            }
        }
    }

    /// <summary>Shows <paramref name="mode"/>, sliding over to it unless <paramref name="animate"/>
    /// is false. Used when the mode changed some other way, or a switch did not happen.</summary>
    public void SetMode(WorkspaceMode mode, bool animate = true)
    {
        if (mode is not (WorkspaceMode.Desktop or WorkspaceMode.SimRig)) mode = WorkspaceMode.Desktop;
        if (mode == _mode && !_committing && !_holding) return;
        _timer.Stop();
        _holding = false;
        var generation = ++_generation;
        if (!animate || ActualWidth <= 0) { _committing = false; _mode = mode; Rest(mode); return; }

        _committing = true;
        var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        if (mode == _mode)
        {
            // Caught mid-snap on the way out: let T and C fall back to where it started.
            Animate("T", Spring(0, 0.8f, 55));
            Animate("C", Spring(0, 0.8f, 55));
        }
        else
        {
            Aim(_mode, mode);
            var slide = _compositor.CreateScalarKeyFrameAnimation();
            slide.InsertExpressionKeyFrame(0, "this.StartingValue");
            slide.InsertKeyFrame(1, 1, _compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0), new Vector2(0.2f, 1)));
            slide.Duration = TimeSpan.FromMilliseconds(260);
            var snap = Spring(1, 0.7f, 50);
            snap.DelayTime = TimeSpan.FromMilliseconds(150);
            Animate("T", slide);
            Animate("C", snap);
        }
        batch.End();
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (generation != _generation) return;
            _committing = false;
            _mode = mode;
            Rest(mode);
        });
    }

    /// <summary>A soft light at the top of <paramref name="host"/> in the colour of the lit
    /// thumb, following it while it is held.</summary>
    public void AttachAmbient(UIElement host)
    {
        var brush = _compositor.CreateRadialGradientBrush();
        brush.EllipseCenter = new Vector2(0.5f, 0f);
        brush.EllipseRadius = new Vector2(0.8f, 1f);
        var inner = _compositor.CreateColorGradientStop(0, Color.FromArgb(0, 0, 0, 0));
        var outer = _compositor.CreateColorGradientStop(1, Color.FromArgb(0, 0, 0, 0));
        brush.ColorStops.Add(inner);
        brush.ColorStops.Add(outer);
        Bind(inner, "Color", "ColorLerpRGB(p.SA, p.GA, p.T)");
        Bind(outer, "Color", "ColorLerpRGB(p.SZ, p.GZ, p.T)");
        var glow = _compositor.CreateSpriteVisual();
        glow.RelativeSizeAdjustment = Vector2.One;
        glow.Brush = brush;
        ElementCompositionPreview.SetElementChildVisual(host, glow);
    }

    // ===== input ======================================================================

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed) return;
        if (!CanHold) return;
        CapturePointer(e.Pointer);
        Focus(FocusState.Pointer);
        StartHold();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e) { base.OnPointerReleased(e); ReleasePointerCapture(e.Pointer); CancelHold(); }
    protected override void OnPointerCanceled(PointerRoutedEventArgs e) { base.OnPointerCanceled(e); CancelHold(); }
    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e) { base.OnPointerCaptureLost(e); CancelHold(); }

    /// <summary>
    /// Whether keyboard focus should be shown. The system focus rectangle showed every time the
    /// flyout opened, because WinUI reports a freshly activated window's focus as keyboard focus
    /// even when nothing was typed. So the owner turns this on once a navigation key is pressed,
    /// and the track's outline lights up only then.
    /// </summary>
    public bool FocusCues
    {
        get => _focusCues;
        set { _focusCues = value; Outline(value && FocusState != FocusState.Unfocused); }
    }
    private bool _focusCues;

    protected override void OnGotFocus(RoutedEventArgs e)
    {
        base.OnGotFocus(e);
        Outline(_focusCues);
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        Outline(false);
        CancelHold();
    }

    private Microsoft.UI.Xaml.Media.Brush? _trackStroke;

    private void Outline(bool focused)
    {
        _trackStroke ??= Track.BorderBrush;
        Track.BorderBrush = focused ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255)) : _trackStroke;
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is not (VirtualKey.Space or VirtualKey.Enter)) return;
        e.Handled = true;
        if (!e.KeyStatus.WasKeyDown && CanHold) StartHold();
    }

    protected override void OnKeyUp(KeyRoutedEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key is not (VirtualKey.Space or VirtualKey.Enter)) return;
        e.Handled = true;
        CancelHold();
    }

    private bool CanHold => IsEnabled && !_busy && !_holding && !_committing;

    // ===== the hold ===================================================================

    private void StartHold()
    {
        _holding = true;
        ++_generation;
        var charge = _compositor.CreateScalarKeyFrameAnimation();
        charge.InsertExpressionKeyFrame(0, "this.StartingValue");
        charge.InsertKeyFrame(1, 1, _holdEasing);
        charge.Duration = HoldDuration;
        Animate("T", charge);
        Press(0.975f, 0.72f);
        _timer.Start();
        HoldStarted?.Invoke(Other(_mode));
    }

    private void CancelHold()
    {
        if (!_holding) return;
        _holding = false;
        _timer.Stop();
        ++_generation;
        Animate("T", Spring(0, 0.78f, 55));
        Press(1f, 0.6f);
        HoldCancelled?.Invoke();
    }

    private void Complete()
    {
        if (!_holding) return;
        _holding = false;
        _committing = true;
        var generation = ++_generation;
        var target = Other(_mode);

        _state.StopAnimation("T");
        _state.InsertScalar("T", 1);
        var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        Animate("C", Spring(1, 0.62f, 48));
        Press(1f, 0.42f);
        // The glow flares out and settles again: the hold has registered.
        var pulse = _compositor.CreateScalarKeyFrameAnimation();
        pulse.InsertKeyFrame(0.25f, 7f);
        pulse.InsertKeyFrame(1, 0f);
        pulse.Duration = TimeSpan.FromMilliseconds(700);
        Animate("GX", pulse);
        batch.End();
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (generation != _generation) return;
            _committing = false;
            _mode = target;
            Rest(target);
        });
        Confirmed?.Invoke(target);
    }

    private void Press(float scale, float damping)
    {
        _self.CenterPoint = new Vector3((float)ActualWidth / 2, (float)ActualHeight / 2, 0);
        var spring = _compositor.CreateSpringVector3Animation();
        spring.FinalValue = new Vector3(scale, scale, 1);
        spring.DampingRatio = damping;
        spring.Period = TimeSpan.FromMilliseconds(50);
        _self.StartAnimation("Scale", spring);
    }

    // ===== state ======================================================================

    /// <summary>At rest in <paramref name="mode"/>, aimed at the other side, T and C at zero.</summary>
    private void Rest(WorkspaceMode mode)
    {
        _state.StopAnimation("T");
        _state.StopAnimation("C");
        Aim(mode, Other(mode));
        _state.InsertScalar("T", 0);
        _state.InsertScalar("C", 0);
        AutomationProperties.SetName(this, $"Hold to switch to {Title(Other(mode))}");
    }

    /// <summary>Sets up a move from <paramref name="from"/> to <paramref name="to"/>: where each
    /// edge starts, where it reaches while held, where it ends up, and the colours on the way.</summary>
    private void Aim(WorkspaceMode from, WorkspaceMode to)
    {
        var width = (float)Math.Max(ActualWidth, 2 * Inset + 2);
        var half = (width - 2 * Inset) / 2;
        (float Left, float Right) Span(WorkspaceMode mode) => mode == WorkspaceMode.SimRig ? (Inset + half, width - Inset) : (Inset, Inset + half);
        var (l0, r0) = Span(from);
        var (l1, r1) = Span(to);
        _state.InsertScalar("L0", l0); _state.InsertScalar("R0", r0);
        _state.InsertScalar("LH", Inset); _state.InsertScalar("RH", width - Inset);
        _state.InsertScalar("L1", l1); _state.InsertScalar("R1", r1);

        var rightward = to == WorkspaceMode.SimRig;
        _state.InsertScalar("K0", rightward ? 1 : 0);
        _state.InsertScalar("K1", rightward ? 0 : 1);

        var source = PaletteOf(from);
        var target = PaletteOf(to);
        _state.InsertColor("S0", source.Start); _state.InsertColor("S1", source.End); _state.InsertColor("SG", source.Glow);
        _state.InsertColor("G0", target.Start); _state.InsertColor("G1", target.End); _state.InsertColor("GG", target.Glow);
        _state.InsertColor("SA", WithAlpha(source.Glow, 0x3A)); _state.InsertColor("GA", WithAlpha(target.Glow, 0x3A));
        _state.InsertColor("SZ", WithAlpha(source.Glow, 0)); _state.InsertColor("GZ", WithAlpha(target.Glow, 0));
        for (var i = 0; i < Halo.Length; i++)
        {
            _state.InsertColor($"SH{i}", WithAlpha(source.Glow, Halo[i].Alpha));
            _state.InsertColor($"GH{i}", WithAlpha(target.Glow, Halo[i].Alpha));
        }

        // The label being left stays lit until the thumb has left it; the other one lights up
        // as the thumb reaches it.
        var (fromA, toA) = from == WorkspaceMode.Desktop ? ("D", "R") : ("R", "D");
        _state.InsertScalar(fromA + "A", 1); _state.InsertScalar(fromA + "B", 1); _state.InsertScalar(fromA + "C", 0.5f);
        _state.InsertScalar(toA + "A", 0.5f); _state.InsertScalar(toA + "B", 1); _state.InsertScalar(toA + "C", 1);
    }

    private void ApplySize()
    {
        var width = (float)ActualWidth;
        var height = (float)ActualHeight;
        if (width <= 0 || height <= 0) return;
        var thumb = height - 2 * Inset;
        _state.InsertScalar("H", thumb);
        foreach (var geometry in _geometries) geometry.CornerRadius = new Vector2(thumb / 2, thumb / 2);
        _shapes.Size = new Vector2(width + 2 * Bleed, height + 2 * Bleed);
        _shapes.Offset = new Vector3(-Bleed, -Bleed, 0);
        if (!_holding && !_committing) Rest(_mode);
    }

    // ===== helpers ====================================================================

    private CompositionRoundedRectangleGeometry Pill()
    {
        var geometry = _compositor.CreateRoundedRectangleGeometry();
        geometry.CornerRadius = new Vector2(27, 27);
        Bind(geometry, "Size", $"Vector2({RightEdge} - {LeftEdge}, p.H)");
        _geometries.Add(geometry);
        return geometry;
    }

    private void Follow(CompositionSpriteShape shape) => Bind(shape, "Offset", $"Vector2(p.M + {LeftEdge}, p.M + p.Top)");

    private void Bind(CompositionObject target, string property, string expression)
    {
        var animation = _compositor.CreateExpressionAnimation(expression);
        animation.SetReferenceParameter("p", _state);
        target.StartAnimation(property, animation);
    }

    private void Animate(string property, CompositionAnimation animation) => _state.StartAnimation(property, animation);

    private SpringScalarNaturalMotionAnimation Spring(float to, float damping, int periodMs)
    {
        var spring = _compositor.CreateSpringScalarAnimation();
        spring.FinalValue = to;
        spring.DampingRatio = damping;
        spring.Period = TimeSpan.FromMilliseconds(periodMs);
        return spring;
    }

    private CompositionLinearGradientBrush VerticalGradient(params (float Offset, Color Color)[] stops)
    {
        var brush = _compositor.CreateLinearGradientBrush();
        brush.StartPoint = new Vector2(0.5f, 0);
        brush.EndPoint = new Vector2(0.5f, 1);
        foreach (var (offset, color) in stops) brush.ColorStops.Add(_compositor.CreateColorGradientStop(offset, color));
        return brush;
    }

    private static WorkspaceMode Other(WorkspaceMode mode) => mode == WorkspaceMode.SimRig ? WorkspaceMode.Desktop : WorkspaceMode.SimRig;
    private static string Title(WorkspaceMode mode) => mode == WorkspaceMode.SimRig ? "Sim Rig" : "Desktop";
    private static Palette PaletteOf(WorkspaceMode mode) => mode == WorkspaceMode.SimRig ? RigColors : DesktopColors;
    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);
    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>The accent colour of a mode, for anything outside the switch that should match it.</summary>
    public static Color AccentOf(WorkspaceMode mode) => PaletteOf(mode).Glow;

    private sealed record Palette(Color Start, Color End, Color Glow);
}
