using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace OptiHub.Helpers;

/// <summary>
/// Kinetics-inspired motion (spring overshoot / glide / stagger).
/// Curves approximate https://kinetics.colorion.co spring(320,24) feel in WinUI Storyboards.
/// </summary>
public static class OptiMotion
{
    /// <summary>Spring settle ~cubic-bezier(0.34, 1.56, 0.64, 1).</summary>
    public static EasingFunctionBase Spring() =>
        new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };

    /// <summary>Glide settle ~cubic-bezier(0.16, 1, 0.3, 1).</summary>
    public static EasingFunctionBase Glide() =>
        new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>Fast press compress.</summary>
    public static EasingFunctionBase Press() =>
        new CubicEase { EasingMode = EasingMode.EaseOut };

    public static DoubleAnimation Fade(
        UIElement target, double from, double to, int ms, int delayMs = 0, EasingFunctionBase? ease = null)
    {
        var a = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = ease ?? Glide(),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(a, target);
        Storyboard.SetTargetProperty(a, "Opacity");
        return a;
    }

    public static DoubleAnimation SlideY(
        CompositeTransform transform, double from, double to, int ms, int delayMs = 0, EasingFunctionBase? ease = null)
    {
        var a = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = ease ?? Spring(),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(a, transform);
        Storyboard.SetTargetProperty(a, "TranslateY");
        return a;
    }

    public static DoubleAnimation Scale(
        CompositeTransform transform, string prop, double from, double to, int ms, int delayMs = 0, EasingFunctionBase? ease = null)
    {
        var a = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = ease ?? Spring(),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(a, transform);
        Storyboard.SetTargetProperty(a, prop);
        return a;
    }

    public static CompositeTransform EnsureTransform(UIElement el)
    {
        if (el.RenderTransform is CompositeTransform ct)
            return ct;
        ct = new CompositeTransform();
        el.RenderTransform = ct;
        el.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        return ct;
    }

    /// <summary>Overlay open: scrim fade + sheet spring up (toast overshoot).</summary>
    public static void PlayOverlayOpen(UIElement scrimHost, UIElement sheet)
    {
        var sb = new Storyboard();
        scrimHost.Opacity = 0;
        sheet.Opacity = 0;
        var tf = EnsureTransform(sheet);
        tf.TranslateY = 28;
        tf.ScaleX = 0.94;
        tf.ScaleY = 0.94;

        sb.Children.Add(Fade(scrimHost, 0, 1, 220, 0, Glide()));
        sb.Children.Add(Fade(sheet, 0, 1, 280, 40, Glide()));
        sb.Children.Add(SlideY(tf, 28, 0, 420, 30, Spring()));
        sb.Children.Add(Scale(tf, "ScaleX", 0.94, 1, 420, 30, Spring()));
        sb.Children.Add(Scale(tf, "ScaleY", 0.94, 1, 420, 30, Spring()));
        sb.Begin();
    }

    /// <summary>Stagger rows: fade + rise (Kinetics stagger entrance).</summary>
    public static void PlayStaggerIn(IEnumerable<UIElement> items, int baseDelayMs = 40, int stepMs = 55)
    {
        var list = items.Where(i => i is not null).ToList();
        if (list.Count == 0) return;
        var sb = new Storyboard();
        for (var i = 0; i < list.Count; i++)
        {
            var el = list[i];
            el.Opacity = 0;
            var tf = EnsureTransform(el);
            tf.TranslateY = 12;
            var d = baseDelayMs + i * stepMs;
            sb.Children.Add(Fade(el, 0, 1, 380, d, Glide()));
            sb.Children.Add(SlideY(tf, 12, 0, 420, d, Spring()));
        }
        sb.Begin();
    }
}
