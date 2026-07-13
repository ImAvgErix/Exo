using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace OptiHub.Views.Controls;

/// <summary>
/// OptiHub orbit-bead loader — accent bead orbits a soft track with ghost trail
/// and a breathing core. Bind <see cref="IsActive"/> like a ProgressRing.
/// </summary>
public sealed partial class OptiLoader : UserControl
{
    private Storyboard? _orbit;
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
            // Parent often goes Collapsed→Visible with IsActive already true (Settings update card).
            EnsureStoryboard();
            if (IsActive) Start(force: true);
        };
        Unloaded += (_, _) => Stop();
        // When a collapsed parent shows us again, restart if still active.
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility == Visibility.Visible && IsActive)
                Start(force: true);
            else if (Visibility != Visibility.Visible)
                Stop();
        });
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not OptiLoader loader) return;
        if (e.NewValue is true)
        {
            // Defer one tick so first layout after Collapsed→Visible has real size.
            loader.DispatcherQueue?.TryEnqueue(() =>
            {
                if (loader.IsActive) loader.Start(force: true);
            });
            loader.Start(force: true);
        }
        else loader.Stop();
    }

    private void EnsureStoryboard()
    {
        if (_orbit is not null) return;

        _orbit = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

        // Lead bead: full 360° in ~1.05s (linear = constant orbital speed)
        AddSpin(_orbit, OrbitRotate, durationMs: 1050, from: 0, to: 360);

        // Trail + ghost lag behind (same period, start already offset via BeginTime + angle base)
        // Use slightly longer period so they drift relative to the lead bead
        AddSpin(_orbit, TrailRotate, durationMs: 1050, from: -42, to: 318);
        AddSpin(_orbit, GhostRotate, durationMs: 1050, from: -78, to: 282);

        // Core breath (X + Y)
        AddBreathScale(_orbit, CoreScale, "ScaleX");
        AddBreathScale(_orbit, CoreScale, "ScaleY");
        AddBreathOpacity(_orbit, Core);

        // Halo soft expand + fade
        AddHaloScale(_orbit, HaloScale, "ScaleX");
        AddHaloScale(_orbit, HaloScale, "ScaleY");
        AddHaloOpacity(_orbit, Halo);
    }

    private static void AddBreathScale(Storyboard board, ScaleTransform target, string prop)
    {
        var anim = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true
        };
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = 0.72,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)),
            Value = 1.12,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1040)),
            Value = 0.72,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, prop);
        board.Children.Add(anim);
    }

    private static void AddBreathOpacity(Storyboard board, UIElement target)
    {
        var anim = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true
        };
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = 0.55,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)),
            Value = 1.0,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1040)),
            Value = 0.55,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, "Opacity");
        board.Children.Add(anim);
    }

    private static void AddHaloScale(Storyboard board, ScaleTransform target, string prop)
    {
        var anim = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true
        };
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = 0.92,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900)),
            Value = 1.18,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
        });
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(901)),
            Value = 0.92
        });
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1200)),
            Value = 0.92
        });
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, prop);
        board.Children.Add(anim);
    }

    private static void AddHaloOpacity(Storyboard board, UIElement target)
    {
        var anim = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true
        };
        anim.KeyFrames.Add(new LinearDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = 0.14
        });
        anim.KeyFrames.Add(new LinearDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900)),
            Value = 0
        });
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1200)),
            Value = 0
        });
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, "Opacity");
        board.Children.Add(anim);
    }

    private static void AddSpin(Storyboard board, RotateTransform target, int durationMs, double from, double to)
    {
        var spin = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true,
            // Linear — orbital motion should not ease (avoids “sticky” bead look)
            EasingFunction = null
        };
        Storyboard.SetTarget(spin, target);
        Storyboard.SetTargetProperty(spin, "Angle");
        board.Children.Add(spin);
    }

    private void Start(bool force = false)
    {
        if (_running && !force) return;
        if (!IsLoaded) return;
        EnsureStoryboard();
        // Don't force our own Visibility — parents (update card) own show/hide.
        try
        {
            if (_running) _orbit?.Stop();
        }
        catch { }
        _orbit?.Begin();
        _running = true;
    }

    private void Stop()
    {
        if (!_running && _orbit is null) return;
        try { _orbit?.Stop(); } catch { }
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
        _running = false;
    }
}
