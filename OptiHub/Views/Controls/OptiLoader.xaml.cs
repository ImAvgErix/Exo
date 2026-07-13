using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace OptiHub.Views.Controls;

/// <summary>
/// OptiHub signal-meter loader — four phase-shifted bars + a soft scan sweep.
/// Bind <see cref="IsActive"/> like a ProgressRing (not a stock spinner).
/// </summary>
public sealed partial class OptiLoader : UserControl
{
    private Storyboard? _wave;
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
            EnsureStoryboard();
            if (IsActive) Start();
        };
        Unloaded += (_, _) => Stop();
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not OptiLoader loader) return;
        if (e.NewValue is true) loader.Start();
        else loader.Stop();
    }

    private void EnsureStoryboard()
    {
        if (_wave is not null) return;

        _wave = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

        // Each bar: scaleY 1 → 3.4 → 1 (bottom-anchored “signal” bounce)
        AddBarWave(_wave, Bar0Scale, delayMs: 0);
        AddBarWave(_wave, Bar1Scale, delayMs: 110);
        AddBarWave(_wave, Bar2Scale, delayMs: 220);
        AddBarWave(_wave, Bar3Scale, delayMs: 330);

        // Soft opacity breathe per bar (slightly offset)
        AddBarOpacity(_wave, Bar0, 0);
        AddBarOpacity(_wave, Bar1, 110);
        AddBarOpacity(_wave, Bar2, 220);
        AddBarOpacity(_wave, Bar3, 330);

        // Scan tick: ease across the plate
        var scan = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true
        };
        scan.KeyFrames.Add(new DiscreteDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = 0
        });
        scan.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900)),
            Value = 30,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        });
        scan.KeyFrames.Add(new DiscreteDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1100)),
            Value = 0
        });
        Storyboard.SetTarget(scan, ScanX);
        Storyboard.SetTargetProperty(scan, "X");
        _wave.Children.Add(scan);

        var scanOp = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true
        };
        scanOp.KeyFrames.Add(new LinearDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = 0
        });
        scanOp.KeyFrames.Add(new LinearDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)),
            Value = 0.18
        });
        scanOp.KeyFrames.Add(new LinearDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(780)),
            Value = 0.18
        });
        scanOp.KeyFrames.Add(new LinearDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900)),
            Value = 0
        });
        scanOp.KeyFrames.Add(new LinearDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1100)),
            Value = 0
        });
        Storyboard.SetTarget(scanOp, Scan);
        Storyboard.SetTargetProperty(scanOp, "Opacity");
        _wave.Children.Add(scanOp);
    }

    private static void AddBarWave(Storyboard board, ScaleTransform scale, int delayMs)
    {
        var anim = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true,
            BeginTime = TimeSpan.FromMilliseconds(delayMs)
        };
        // Cycle ~720ms: rest → peak → rest
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = 1.0,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(280)),
            Value = 3.5,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(560)),
            Value = 1.15,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(720)),
            Value = 1.0,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        Storyboard.SetTarget(anim, scale);
        Storyboard.SetTargetProperty(anim, "ScaleY");
        board.Children.Add(anim);
    }

    private static void AddBarOpacity(Storyboard board, UIElement bar, int delayMs)
    {
        var anim = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true,
            BeginTime = TimeSpan.FromMilliseconds(delayMs)
        };
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = 0.4,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(280)),
            Value = 1.0,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(720)),
            Value = 0.4,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        Storyboard.SetTarget(anim, bar);
        Storyboard.SetTargetProperty(anim, "Opacity");
        board.Children.Add(anim);
    }

    private void Start()
    {
        if (_running) return;
        if (!IsLoaded) return;
        EnsureStoryboard();
        Visibility = Visibility.Visible;
        _wave?.Begin();
        _running = true;
    }

    private void Stop()
    {
        if (!_running && _wave is null) return;
        try { _wave?.Stop(); } catch { }
        // Reset transforms so next start is clean
        try
        {
            if (Bar0Scale is not null) Bar0Scale.ScaleY = 1;
            if (Bar1Scale is not null) Bar1Scale.ScaleY = 1;
            if (Bar2Scale is not null) Bar2Scale.ScaleY = 1;
            if (Bar3Scale is not null) Bar3Scale.ScaleY = 1;
            if (ScanX is not null) ScanX.X = 0;
        }
        catch { }
        _running = false;
    }
}
