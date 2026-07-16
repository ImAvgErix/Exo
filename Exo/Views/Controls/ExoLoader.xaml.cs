using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Exo.Views.Controls;

/// <summary>
/// Exo orbit-bead loader. Uses the Windows Composition API for rotation so
/// the animation keeps running inside ContentDialog / Opacity-toggled hosts
/// (DispatcherTimer + RotateTransform and XAML Storyboards both go stale there).
/// Composition Scale/Opacity writes are intentionally avoided (v2.6.0 crash class).
/// Crash-loop safe mode (ExoMotion.MotionDisabled) skips composition entirely.
/// </summary>
public sealed partial class ExoLoader : UserControl
{
    private bool _running;
    private ScalarKeyFrameAnimation? _spinAnim;
    private ScalarKeyFrameAnimation? _trailAnim;

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(
            nameof(IsActive),
            typeof(bool),
            typeof(ExoLoader),
            new PropertyMetadata(false, OnIsActiveChanged));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public ExoLoader()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (IsActive) Start(force: true);
        };
        Unloaded += (_, _) => Stop();
        SizeChanged += (_, _) =>
        {
            // Center points depend on laid-out size.
            if (IsActive && IsLoaded) Start(force: true);
        };
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility == Visibility.Visible && IsActive)
                Start(force: true);
            else if (Visibility != Visibility.Visible)
                Stop();
        });
        RegisterPropertyChangedCallback(OpacityProperty, (_, _) =>
        {
            if (Opacity > 0.01 && IsActive && IsLoaded)
                Start(force: false);
        });
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ExoLoader loader) return;
        if (e.NewValue is true)
        {
            // Defer so first layout (dialog / opacity host) has real size + visuals.
            loader.DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
            {
                if (loader.IsActive) loader.Start(force: true);
            });
            loader.Start(force: true);
        }
        else
        {
            loader.Stop();
        }
    }

    private void Start(bool force = false)
    {
        if (!IsLoaded) return;
        if (_running && !force) return;

        // Crash-loop safe mode: previous launch died before first frame.
        // Never poke composition visuals here — that path is what can brick
        // startup on real GPUs (same class as the v2.6.0 flash-close). Static
        // beads stay visible; ExoMotion.MotionDisabled owns the gate.
        if (Helpers.ExoMotion.MotionDisabled)
        {
            try { StopComposition(); } catch { }
            _running = false;
            return;
        }

        try
        {
            StopComposition();
            BeginComposition();
            _running = true;
        }
        catch
        {
            _running = false;
        }
    }

    private void Stop()
    {
        StopComposition();
        _running = false;
        try
        {
            if (OrbitRotate is not null) OrbitRotate.Angle = 0;
            if (TrailRotate is not null) TrailRotate.Angle = -42;
            if (GhostRotate is not null) GhostRotate.Angle = -78;
            if (CoreScale is not null) { CoreScale.ScaleX = 1; CoreScale.ScaleY = 1; }
            if (HaloScale is not null) { HaloScale.ScaleX = 1; HaloScale.ScaleY = 1; }
            if (Halo is not null) Halo.Opacity = 0.08;
            if (Core is not null) Core.Opacity = 0.9;
        }
        catch { }
    }

    private void BeginComposition()
    {
        // Prefer composition visuals on the orbit hosts — independent of XAML storyboard clocks.
        var orbitVis = ElementCompositionPreview.GetElementVisual(Orbit);
        var trailVis = ElementCompositionPreview.GetElementVisual(TrailOrbit);
        var compositor = orbitVis.Compositor;

        // Pivot at element center (orbit grids are 32×32).
        static void Center(Visual v, FrameworkElement el)
        {
            var w = (float)(el.ActualWidth > 0 ? el.ActualWidth : el.Width);
            var h = (float)(el.ActualHeight > 0 ? el.ActualHeight : el.Height);
            if (w <= 0) w = 32;
            if (h <= 0) h = 32;
            v.CenterPoint = new Vector3(w / 2f, h / 2f, 0);
        }

        Center(orbitVis, Orbit);
        Center(trailVis, TrailOrbit);

        // Clear leftover XAML RotateTransforms so composition owns the spin.
        if (OrbitRotate is not null) OrbitRotate.Angle = 0;
        if (TrailRotate is not null) TrailRotate.Angle = 0;
        if (GhostRotate is not null) GhostRotate.Angle = 0;

        _spinAnim = compositor.CreateScalarKeyFrameAnimation();
        _spinAnim.InsertKeyFrame(0f, 0f);
        _spinAnim.InsertKeyFrame(1f, 360f);
        _spinAnim.Duration = TimeSpan.FromMilliseconds(1000);
        _spinAnim.IterationBehavior = AnimationIterationBehavior.Forever;
        _spinAnim.Direction = AnimationDirection.Normal;

        _trailAnim = compositor.CreateScalarKeyFrameAnimation();
        _trailAnim.InsertKeyFrame(0f, -42f);
        _trailAnim.InsertKeyFrame(1f, 318f);
        _trailAnim.Duration = TimeSpan.FromMilliseconds(1000);
        _trailAnim.IterationBehavior = AnimationIterationBehavior.Forever;

        // One orbit language: primary bead + soft trail. Rotation only —
        // composition Scale/Opacity hand-off writes are the crash-prone class.
        orbitVis.StartAnimation("RotationAngleInDegrees", _spinAnim);
        trailVis.StartAnimation("RotationAngleInDegrees", _trailAnim);
    }

    private void StopComposition()
    {
        try
        {
            if (Orbit is not null)
            {
                var v = ElementCompositionPreview.GetElementVisual(Orbit);
                v.StopAnimation("RotationAngleInDegrees");
                v.RotationAngleInDegrees = 0;
            }
            if (TrailOrbit is not null)
            {
                var v = ElementCompositionPreview.GetElementVisual(TrailOrbit);
                v.StopAnimation("RotationAngleInDegrees");
                v.RotationAngleInDegrees = -42;
            }
            if (GhostOrbit is not null)
            {
                var v = ElementCompositionPreview.GetElementVisual(GhostOrbit);
                v.StopAnimation("RotationAngleInDegrees");
                v.RotationAngleInDegrees = -78;
            }
            if (Sweep is not null)
            {
                var v = ElementCompositionPreview.GetElementVisual(Sweep);
                v.StopAnimation("RotationAngleInDegrees");
                v.RotationAngleInDegrees = 0;
            }
            // Clear any leftover Scale/Opacity from older builds that still wrote them.
            if (Core is not null)
            {
                var v = ElementCompositionPreview.GetElementVisual(Core);
                v.StopAnimation("Scale.X");
                v.StopAnimation("Scale.Y");
                v.StopAnimation("Opacity");
                v.Scale = Vector3.One;
                v.Opacity = 0.9f;
            }
            if (Halo is not null)
            {
                var v = ElementCompositionPreview.GetElementVisual(Halo);
                v.StopAnimation("Scale.X");
                v.StopAnimation("Scale.Y");
                v.StopAnimation("Opacity");
                v.Scale = Vector3.One;
                v.Opacity = 0.08f;
            }
        }
        catch { }

        _spinAnim = null;
        _trailAnim = null;
    }
}
