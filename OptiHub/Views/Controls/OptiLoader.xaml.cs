using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace OptiHub.Views.Controls;

/// <summary>
/// OptiHub triad spinner — three accent dots orbiting with a soft core pulse.
/// Bind <see cref="IsActive"/> like a ProgressRing.
/// </summary>
public sealed partial class OptiLoader : UserControl
{
    private Storyboard? _spin;
    private Storyboard? _pulse;
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
            EnsureStoryboards();
            if (IsActive) Start();
        };
        Unloaded += (_, _) => Stop();
        ActualThemeChanged += (_, _) => { /* brushes re-resolve via ThemeResource */ };
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not OptiLoader loader) return;
        if (e.NewValue is true) loader.Start();
        else loader.Stop();
    }

    private void EnsureStoryboards()
    {
        if (_spin is not null) return;

        // Continuous spin — ~1.1s per turn, linear (steady, not “busy AI spinner”)
        _spin = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var rotate = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromMilliseconds(1100),
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(rotate, OrbitRotate);
        Storyboard.SetTargetProperty(rotate, "Angle");
        _spin.Children.Add(rotate);

        // Staggered opacity wave on the three dots (chasing light)
        _pulse = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        AddDotPulse(_pulse, DotA, 0);
        AddDotPulse(_pulse, DotB, 200);
        AddDotPulse(_pulse, DotC, 400);

        // Core breath
        var core = new DoubleAnimation
        {
            From = 0.2,
            To = 0.75,
            Duration = TimeSpan.FromMilliseconds(700),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(core, Core);
        Storyboard.SetTargetProperty(core, "Opacity");
        _pulse.Children.Add(core);
    }

    private static void AddDotPulse(Storyboard board, UIElement target, int delayMs)
    {
        var anim = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true,
            BeginTime = TimeSpan.FromMilliseconds(delayMs)
        };
        // 0 → 1 → 0.25 over 900ms cycle
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = 0.25,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)),
            Value = 1.0,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900)),
            Value = 0.25,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, "Opacity");
        board.Children.Add(anim);
    }

    private void Start()
    {
        if (_running) return;
        if (!IsLoaded)
        {
            // Loaded handler will start once tree is ready
            return;
        }
        EnsureStoryboards();
        Visibility = Visibility.Visible;
        _spin?.Begin();
        _pulse?.Begin();
        _running = true;
    }

    private void Stop()
    {
        if (!_running && _spin is null) return;
        try { _spin?.Stop(); } catch { }
        try { _pulse?.Stop(); } catch { }
        _running = false;
    }
}
