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
/// </summary>
public sealed partial class OptiLoader : UserControl
{
    private bool _running;
    private ScalarKeyFrameAnimation? _spinAnim;
    private ScalarKeyFrameAnimation? _trailAnim;
    private ScalarKeyFrameAnimation? _ghostAnim;
    private ScalarKeyFrameAnimation? _breathAnim;
    private ScalarKeyFrameAnimation? _haloAnim;
    private ScalarKeyFrameAnimation? _haloOpacityAnim;
    private ScalarKeyFrameAnimation? _coreOpacityAnim;

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(
            nameof(IsActive),
            typeof(bool),
            typeof(OptiLoader),
            new PropertyMetadata(false, OnIsActiveChanged));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public OptiLoader()
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
        if (d is not OptiLoader loader) return;
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
        var ghostVis = ElementCompositionPreview.GetElementVisual(GhostOrbit);
        var coreVis = ElementCompositionPreview.GetElementVisual(Core);
        var haloVis = ElementCompositionPreview.GetElementVisual(Halo);
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
        Center(ghostVis, GhostOrbit);
        Center(coreVis, Core);
        Center(haloVis, Halo);

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

        _ghostAnim = compositor.CreateScalarKeyFrameAnimation();
        _ghostAnim.InsertKeyFrame(0f, -78f);
        _ghostAnim.InsertKeyFrame(1f, 282f);
        _ghostAnim.Duration = TimeSpan.FromMilliseconds(1000);
        _ghostAnim.IterationBehavior = AnimationIterationBehavior.Forever;

        // Core breath via scale
        _breathAnim = compositor.CreateScalarKeyFrameAnimation();
        _breathAnim.InsertKeyFrame(0f, 0.78f);
        _breathAnim.InsertKeyFrame(0.5f, 1.12f);
        _breathAnim.InsertKeyFrame(1f, 0.78f);
        _breathAnim.Duration = TimeSpan.FromMilliseconds(1040);
        _breathAnim.IterationBehavior = AnimationIterationBehavior.Forever;

        _coreOpacityAnim = compositor.CreateScalarKeyFrameAnimation();
        _coreOpacityAnim.InsertKeyFrame(0f, 0.55f);
        _coreOpacityAnim.InsertKeyFrame(0.5f, 1f);
        _coreOpacityAnim.InsertKeyFrame(1f, 0.55f);
        _coreOpacityAnim.Duration = TimeSpan.FromMilliseconds(1040);
        _coreOpacityAnim.IterationBehavior = AnimationIterationBehavior.Forever;

        _haloAnim = compositor.CreateScalarKeyFrameAnimation();
        _haloAnim.InsertKeyFrame(0f, 0.92f);
        _haloAnim.InsertKeyFrame(0.75f, 1.18f);
        _haloAnim.InsertKeyFrame(1f, 0.92f);
        _haloAnim.Duration = TimeSpan.FromMilliseconds(1200);
        _haloAnim.IterationBehavior = AnimationIterationBehavior.Forever;

        _haloOpacityAnim = compositor.CreateScalarKeyFrameAnimation();
        _haloOpacityAnim.InsertKeyFrame(0f, 0.14f);
        _haloOpacityAnim.InsertKeyFrame(0.75f, 0f);
        _haloOpacityAnim.InsertKeyFrame(1f, 0.14f);
        _haloOpacityAnim.Duration = TimeSpan.FromMilliseconds(1200);
        _haloOpacityAnim.IterationBehavior = AnimationIterationBehavior.Forever;

        // One orbit language: primary bead + soft trail (no ghost/sweep race).
        orbitVis.StartAnimation("RotationAngleInDegrees", _spinAnim);
        trailVis.StartAnimation("RotationAngleInDegrees", _trailAnim);

        // Soft core + halo breath only (same Kinetics family as page enter springs).
        coreVis.StartAnimation("Scale.X", _breathAnim);
        coreVis.StartAnimation("Scale.Y", _breathAnim);
        coreVis.StartAnimation("Opacity", _coreOpacityAnim);

        haloVis.StartAnimation("Scale.X", _haloAnim);
        haloVis.StartAnimation("Scale.Y", _haloAnim);
        haloVis.StartAnimation("Opacity", _haloOpacityAnim);
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
        _ghostAnim = null;
        _breathAnim = null;
        _haloAnim = null;
        _haloOpacityAnim = null;
        _coreOpacityAnim = null;
    }
}
