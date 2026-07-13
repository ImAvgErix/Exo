using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace OptiHub.Views.Controls;

/// <summary>
/// OptiHub orbit-bead loader — accent bead orbits a soft track with ghost trail
/// and a breathing core. Uses a DispatcherTimer so motion keeps running even when
/// a parent briefly sits at Opacity 0 (WinUI freezes Storyboards in that case).
/// </summary>
public sealed partial class OptiLoader : UserControl
{
    private DispatcherTimer? _timer;
    private double _angle;
    private double _breathPhase;
    private double _haloPhase;
    private bool _running;

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
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility == Visibility.Visible && IsActive)
                Start(force: true);
            else if (Visibility != Visibility.Visible)
                Stop();
        });
        // Opacity 0 parents used to freeze Storyboards; timer still needs a kick
        // when we become fully opaque again.
        RegisterPropertyChangedCallback(OpacityProperty, (_, _) =>
        {
            if (Opacity > 0.05 && IsActive && IsLoaded)
                Start(force: false);
        });
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not OptiLoader loader) return;
        if (e.NewValue is true)
        {
            loader.DispatcherQueue?.TryEnqueue(() =>
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
        if (_running && !force) return;
        if (!IsLoaded) return;

        if (_timer is null)
        {
            _timer = new DispatcherTimer
            {
                // ~60fps — smooth orbit without Storyboard composition issues
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _timer.Tick += OnTick;
        }

        if (force || !_running)
        {
            _angle = 0;
            _breathPhase = 0;
            _haloPhase = 0;
            ApplyFrame();
        }

        if (!_timer.IsEnabled)
            _timer.Start();
        _running = true;
    }

    private void Stop()
    {
        try { _timer?.Stop(); } catch { }
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

    private void OnTick(object? sender, object e)
    {
        if (!IsActive || !IsLoaded)
        {
            Stop();
            return;
        }

        // Full orbit ~1.05s at 60fps → ~5.7° per tick
        _angle = (_angle + 5.7) % 360;
        // Breath ~1.04s period
        _breathPhase = (_breathPhase + 0.096) % (Math.PI * 2);
        // Halo pulse ~1.2s
        _haloPhase = (_haloPhase + 0.083) % (Math.PI * 2);

        ApplyFrame();
    }

    private void ApplyFrame()
    {
        try
        {
            if (OrbitRotate is not null) OrbitRotate.Angle = _angle;
            if (TrailRotate is not null) TrailRotate.Angle = _angle - 42;
            if (GhostRotate is not null) GhostRotate.Angle = _angle - 78;

            // 0.72 ↔ 1.12 breath
            var breath = 0.92 + 0.20 * Math.Sin(_breathPhase);
            if (CoreScale is not null)
            {
                CoreScale.ScaleX = breath;
                CoreScale.ScaleY = breath;
            }
            if (Core is not null)
                Core.Opacity = 0.55 + 0.45 * (0.5 + 0.5 * Math.Sin(_breathPhase));

            // Soft halo expand + fade
            var haloT = 0.5 + 0.5 * Math.Sin(_haloPhase);
            if (HaloScale is not null)
            {
                var hs = 0.92 + 0.26 * haloT;
                HaloScale.ScaleX = hs;
                HaloScale.ScaleY = hs;
            }
            if (Halo is not null)
                Halo.Opacity = 0.14 * (1.0 - haloT);
        }
        catch { }
    }
}
