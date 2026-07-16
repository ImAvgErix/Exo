using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace Exo.Views.Controls;

/// <summary>
/// Exo orbit-bead loader. Pure XAML Storyboards only — never writes hand-off
/// composition visuals (those caused the v2.6.0 0xC000027B flash-close and
/// forced crash-loop "safe mode").
/// </summary>
public sealed partial class ExoLoader : UserControl
{
    private Storyboard? _spinBoard;
    private bool _running;

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
            if (IsActive) Start();
        };
        Unloaded += (_, _) => Stop();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility == Visibility.Visible && IsActive)
                Start();
            else if (Visibility != Visibility.Visible)
                Stop();
        });
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ExoLoader loader) return;
        if (e.NewValue is true) loader.Start();
        else loader.Stop();
    }

    private void Start()
    {
        if (!IsLoaded) return;
        if (_running) return;
        try
        {
            StopStoryboard();
            BeginStoryboard();
            _running = true;
        }
        catch
        {
            _running = false;
        }
    }

    private void Stop()
    {
        StopStoryboard();
        _running = false;
        try
        {
            if (OrbitRotate is not null) OrbitRotate.Angle = 0;
            if (TrailRotate is not null) TrailRotate.Angle = -48;
            if (GhostRotate is not null) GhostRotate.Angle = -78;
            if (CoreScale is not null) { CoreScale.ScaleX = 1; CoreScale.ScaleY = 1; }
            if (HaloScale is not null) { HaloScale.ScaleX = 1; HaloScale.ScaleY = 1; }
            if (Halo is not null) Halo.Opacity = 0.08;
            if (Core is not null) Core.Opacity = 0.9;
        }
        catch { }
    }

    private void BeginStoryboard()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        _spinBoard = new Storyboard();

        var orbit = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromMilliseconds(1000)),
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(orbit, OrbitRotate);
        Storyboard.SetTargetProperty(orbit, "Angle");
        _spinBoard.Children.Add(orbit);

        var trail = new DoubleAnimation
        {
            From = -48,
            To = 312,
            Duration = new Duration(TimeSpan.FromMilliseconds(1000)),
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(trail, TrailRotate);
        Storyboard.SetTargetProperty(trail, "Angle");
        _spinBoard.Children.Add(trail);

        // Soft core pulse via XAML ScaleTransform (not composition Visual.Scale).
        var breathX = new DoubleAnimation
        {
            From = 0.85,
            To = 1.08,
            Duration = new Duration(TimeSpan.FromMilliseconds(900)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(breathX, CoreScale);
        Storyboard.SetTargetProperty(breathX, "ScaleX");
        _spinBoard.Children.Add(breathX);

        var breathY = new DoubleAnimation
        {
            From = 0.85,
            To = 1.08,
            Duration = new Duration(TimeSpan.FromMilliseconds(900)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(breathY, CoreScale);
        Storyboard.SetTargetProperty(breathY, "ScaleY");
        _spinBoard.Children.Add(breathY);

        _spinBoard.Begin();
    }

    private void StopStoryboard()
    {
        try { _spinBoard?.Stop(); } catch { }
        _spinBoard = null;
    }
}
