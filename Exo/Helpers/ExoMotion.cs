using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Exo.Helpers;

/// <summary>
/// Full-reign motion via XAML Storyboards only.
/// Never writes Composition Offset/Scale (that detaches layout and caused the
/// v2.6.0 black-flash crash). Crash-loop still forces MotionDisabled = true.
/// </summary>
public static class ExoMotion
{
    /// <summary>
    /// Crash-loop safe mode: previous launch died before first frame —
    /// collapse all entrance motion to instant visibility.
    /// </summary>
    public static bool MotionDisabled { get; set; }

    /// <summary>
    /// Rich profile: deeper staggers, soft scale on plates, press squish,
    /// result pops. Always on unless <see cref="MotionDisabled"/>.
    /// </summary>
    public static bool RichMotion { get; set; } = true;

    public const int EntranceMs = 380;
    public const int FadeMs = 240;
    public const int StaggerStepMs = 52;
    public const int SelectMs = 110;
    public const int ListStaggerStepMs = 36;

    public static EasingFunctionBase Glide() =>
        new CubicEase { EasingMode = EasingMode.EaseOut };

    public static EasingFunctionBase GlideDeep() =>
        new QuinticEase { EasingMode = EasingMode.EaseOut };

    public static EasingFunctionBase GlideIn() =>
        new CubicEase { EasingMode = EasingMode.EaseIn };

    public static CompositeTransform EnsureTransform(UIElement el)
    {
        el.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        if (el.RenderTransform is CompositeTransform ct)
            return ct;
        ct = new CompositeTransform();
        el.RenderTransform = ct;
        return ct;
    }

    public static void EnsureVisible(UIElement el) => ResetVisual(el, show: true);

    public static void ResetVisual(UIElement el, bool show = true)
    {
        el.Opacity = show ? 1 : 0;
        el.IsHitTestVisible = show;
        el.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        try { el.RenderTransform = null; } catch { }
    }

    /// <summary>
    /// Plate / card entrance: fade + rise (+ optional micro-scale in rich mode).
    /// Transform is cleared at end so type stays crisp.
    /// </summary>
    public static void PlayEnter(
        UIElement el,
        int delayMs = 0,
        float fromY = 12f,
        float fromScale = 1f,
        bool enableHit = true,
        double toOpacity = 1.0)
    {
        if (MotionDisabled)
        {
            EnsureVisible(el);
            if (toOpacity < 1.0)
                el.Opacity = Math.Clamp(toOpacity, 0.1, 1.0);
            return;
        }

        var rise = (float)Math.Round(Math.Clamp(fromY, 0f, 18f));
        var settle = Math.Clamp(toOpacity, 0.1, 1.0);
        var useScale = RichMotion && fromScale > 0f && fromScale < 0.999f;
        var startScale = useScale ? Math.Clamp(fromScale, 0.92f, 0.99f) : 1f;
        ScheduleEnsureVisible(el, delayMs + EntranceMs + 60, settle);

        try
        {
            var tf = EnsureTransform(el);
            tf.TranslateX = 0;
            tf.TranslateY = rise;
            tf.ScaleX = startScale;
            tf.ScaleY = startScale;

            el.Opacity = 0;
            el.IsHitTestVisible = false;

            var sb = new Storyboard();
            sb.Children.Add(Fade(el, 0, settle, FadeMs + 50, delayMs));
            if (rise > 0)
                sb.Children.Add(TranslateYDeep(tf, rise, 0, EntranceMs, delayMs));
            if (useScale)
            {
                sb.Children.Add(Scale(tf, "ScaleX", startScale, 1.0, EntranceMs, delayMs));
                sb.Children.Add(Scale(tf, "ScaleY", startScale, 1.0, EntranceMs, delayMs));
            }

            sb.Completed += (_, _) =>
            {
                try
                {
                    el.Opacity = settle;
                    el.IsHitTestVisible = enableHit;
                    el.RenderTransform = null;
                }
                catch { }
            };
            sb.Begin();

            if (enableHit)
            {
                var captured = el;
                ScheduleOnUi(captured, delayMs + 80, () =>
                {
                    try { captured.IsHitTestVisible = true; } catch { }
                });
            }
        }
        catch
        {
            EnsureVisible(el);
        }
    }

    public static void PlayStagger(
        IReadOnlyList<UIElement> items,
        int baseDelayMs = 20,
        int stepMs = StaggerStepMs,
        float fromY = 12f,
        float fromScale = 0.97f)
    {
        var scale = RichMotion && !MotionDisabled ? fromScale : 1f;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is null) continue;
            PlayEnter(items[i], baseDelayMs + i * stepMs, fromY, scale);
        }
    }

    /// <summary>
    /// Feature grid: cascade each realized child (rich) or single host fade (safe).
    /// </summary>
    public static void PlayListEnter(FrameworkElement host, int expectedCount = 0)
    {
        if (host is null) return;
        if (MotionDisabled)
        {
            EnsureVisible(host);
            return;
        }
        _ = RunListEnterAsync(host, expectedCount);
    }

    private static async Task RunListEnterAsync(FrameworkElement host, int expectedCount)
    {
        try
        {
            for (var attempt = 0; attempt < 28; attempt++)
            {
                var count = VisualTreeHelper.GetChildrenCount(host);
                if (count > 0 && (expectedCount <= 0 || count >= expectedCount))
                    break;
                await Task.Delay(16);
            }

            var kids = new List<UIElement>();
            var childCount = VisualTreeHelper.GetChildrenCount(host);
            for (var i = 0; i < childCount; i++)
            {
                if (VisualTreeHelper.GetChild(host, i) is UIElement el)
                    kids.Add(el);
            }

            if (RichMotion && kids.Count > 0)
            {
                foreach (var el in kids)
                {
                    el.Opacity = 0;
                    el.IsHitTestVisible = false;
                }
                EnsureVisible(host);
                PlayStagger(kids, baseDelayMs: 24, stepMs: ListStaggerStepMs, fromY: 10f, fromScale: 0.96f);
            }
            else
            {
                foreach (var el in kids)
                    EnsureVisible(el);
                PlayEnter(host, delayMs: 0, fromY: 6f, fromScale: 1f, toOpacity: 1.0);
            }
        }
        catch
        {
            try { EnsureVisible(host); } catch { }
        }
    }

    /// <summary>Press squish then release — tactile, interruptible via storyboard restart.</summary>
    public static void PlaySelect(UIElement el, Action? onDone = null)
    {
        if (el is null)
        {
            onDone?.Invoke();
            return;
        }

        if (MotionDisabled || !RichMotion)
        {
            EnsureVisible(el);
            onDone?.Invoke();
            return;
        }

        try
        {
            var tf = EnsureTransform(el);
            var sb = new Storyboard();
            sb.Children.Add(Scale(tf, "ScaleX", 1.0, 0.96, 70, 0));
            sb.Children.Add(Scale(tf, "ScaleY", 1.0, 0.96, 70, 0));
            sb.Children.Add(Scale(tf, "ScaleX", 0.96, 1.0, 140, 70));
            sb.Children.Add(Scale(tf, "ScaleY", 0.96, 1.0, 140, 70));
            sb.Completed += (_, _) =>
            {
                try { el.RenderTransform = null; } catch { }
            };
            sb.Begin();
            // Navigation stays immediate; motion is visual only.
            onDone?.Invoke();
        }
        catch
        {
            EnsureVisible(el);
            onDone?.Invoke();
        }
    }

    /// <summary>Check/X settle: scale overshoot pop + fade.</summary>
    public static void PlayResultPop(UIElement el, int delayMs = 0)
    {
        if (el is null) return;
        if (MotionDisabled)
        {
            EnsureVisible(el);
            return;
        }

        try
        {
            var tf = EnsureTransform(el);
            tf.ScaleX = 0.82;
            tf.ScaleY = 0.82;
            el.Opacity = 0.2;
            el.IsHitTestVisible = true;

            var sb = new Storyboard();
            sb.Children.Add(Fade(el, 0.2, 1.0, 220, delayMs));
            sb.Children.Add(Scale(tf, "ScaleX", 0.82, 1.1, 160, delayMs));
            sb.Children.Add(Scale(tf, "ScaleY", 0.82, 1.1, 160, delayMs));
            sb.Children.Add(Scale(tf, "ScaleX", 1.1, 1.0, 130, delayMs + 150));
            sb.Children.Add(Scale(tf, "ScaleY", 1.1, 1.0, 130, delayMs + 150));
            sb.Completed += (_, _) =>
            {
                try
                {
                    el.Opacity = 1;
                    el.RenderTransform = null;
                }
                catch { }
            };
            sb.Begin();
            ScheduleEnsureVisible(el, delayMs + 360);
        }
        catch
        {
            EnsureVisible(el);
        }
    }

    /// <summary>Soft pulse loop for “checking” chrome (stop by clearing storyboard / opacity).</summary>
    public static Storyboard? PlayPulseOpacity(UIElement el, double low = 0.25, double high = 0.95, int periodMs = 700)
    {
        if (el is null || MotionDisabled) return null;
        try
        {
            var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            var a = new DoubleAnimation
            {
                From = low,
                To = high,
                Duration = TimeSpan.FromMilliseconds(periodMs / 2),
                AutoReverse = true,
                EasingFunction = Glide(),
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(a, el);
            Storyboard.SetTargetProperty(a, "Opacity");
            sb.Children.Add(a);
            sb.Begin();
            return sb;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Module page soft fade + optional micro rise.</summary>
    public static void PlayPageEnter(UIElement root)
    {
        if (MotionDisabled)
        {
            EnsureVisible(root);
            return;
        }

        try
        {
            var tf = EnsureTransform(root);
            tf.TranslateY = RichMotion ? 8 : 0;
            root.Opacity = 0;
            root.IsHitTestVisible = true;

            var sb = new Storyboard();
            sb.Children.Add(Fade(root, 0, 1, RichMotion ? 280 : 140, 0));
            if (RichMotion)
                sb.Children.Add(TranslateYDeep(tf, 8, 0, 320, 0));
            sb.Completed += (_, _) =>
            {
                try
                {
                    root.Opacity = 1;
                    root.RenderTransform = null;
                }
                catch { }
            };
            sb.Begin();
            ScheduleEnsureVisible(root, 360);
        }
        catch
        {
            EnsureVisible(root);
        }
    }

    /// <summary>Cascade optimizer rows: each row enters after the previous settles a beat.</summary>
    public static void PlayRowCascade(IReadOnlyList<UIElement> rows, int stepMs = 70)
    {
        if (rows is null || rows.Count == 0) return;
        if (MotionDisabled)
        {
            foreach (var r in rows) EnsureVisible(r);
            return;
        }
        PlayStagger(rows, baseDelayMs: 40, stepMs: stepMs, fromY: 10f, fromScale: 0.95f);
    }

    private static DoubleAnimation Scale(
        CompositeTransform tf,
        string property,
        double from,
        double to,
        int ms,
        int delayMs)
    {
        var a = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = GlideDeep(),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(a, tf);
        Storyboard.SetTargetProperty(a, property);
        return a;
    }

    private static void ScheduleEnsureVisible(UIElement el, int delayMs, double settleOpacity = 1.0)
    {
        var opacity = Math.Clamp(settleOpacity, 0.1, 1.0);
        ScheduleOnUi(el, delayMs, () =>
        {
            try
            {
                EnsureVisible(el);
                if (opacity < 1.0)
                    el.Opacity = opacity;
            }
            catch { }
        });
    }

    private static void ScheduleOnUi(UIElement el, int delayMs, Action action)
    {
        try
        {
            var queue = el.DispatcherQueue;
            if (queue is null) return;

            Microsoft.UI.Dispatching.DispatcherQueueTimer? timer = null;
            timer = queue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, delayMs));
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                try
                {
                    timer?.Stop();
                    action();
                }
                catch { }
                finally { timer = null; }
            };
            timer.Start();
        }
        catch { }
    }

    private static DoubleAnimation Fade(UIElement target, double from, double to, int ms, int delayMs)
    {
        var a = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = GlideDeep(),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(a, target);
        Storyboard.SetTargetProperty(a, "Opacity");
        return a;
    }

    private static DoubleAnimation TranslateYDeep(CompositeTransform tf, double from, double to, int ms, int delayMs)
    {
        var a = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = GlideDeep(),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(a, tf);
        Storyboard.SetTargetProperty(a, "TranslateY");
        return a;
    }
}
