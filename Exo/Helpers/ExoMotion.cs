using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Exo.Helpers;

/// <summary>
/// Safe open animations (XAML Storyboards only).
/// - NEVER touches hand-off composition visuals: writing Offset/Scale there
///   detaches elements from XAML layout — they pile at the parent origin —
///   and pre-first-frame pokes can fail fast with 0xC000027B on real GPUs
///   (the v2.6.0 black-flash launch crash).
/// - Cards / feature tiles: fade + light rise; select: quick press pulse before navigate.
/// </summary>
public static class ExoMotion
{
    /// <summary>
    /// Crash-loop safe mode: when the previous launch died before presenting a
    /// frame, all entrance motion collapses to instant EnsureVisible so a
    /// composition-animation failure cannot brick startup twice.
    /// </summary>
    public static bool MotionDisabled { get; set; }

    // Short, clean motion — no bouncy spring on content.
    public const int EntranceMs = 240;
    public const int FadeMs = 160;
    public const int StaggerStepMs = 22;
    public const int SelectMs = 0;
    /// <summary>Feature-tile list entrance stagger (module pages).</summary>
    public const int ListStaggerStepMs = 28;

    public static EasingFunctionBase Glide() =>
        new CubicEase { EasingMode = EasingMode.EaseOut };

    public static CompositeTransform EnsureTransform(UIElement el)
    {
        el.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        if (el.RenderTransform is CompositeTransform ct)
            return ct;
        ct = new CompositeTransform();
        el.RenderTransform = ct;
        return ct;
    }

    /// <summary>Hard identity + full opacity. Call after every open/close cycle.</summary>
    public static void EnsureVisible(UIElement el)
    {
        ResetVisual(el, show: true);
    }

    public static void ResetVisual(UIElement el, bool show = true)
    {
        el.Opacity = show ? 1 : 0;
        el.IsHitTestVisible = show;
        el.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        try
        {
            // Drop transforms entirely — a leftover scale/translate matrix is the
            // main reason text + logos stay soft after entrance/hover.
            el.RenderTransform = null;
        }
        catch { }
    }

    /// <summary>
    /// Card entrance: fade + short integer rise, then hard-clear transform.
    /// Rise uses whole pixels only; transform is dropped at end so type stays crisp.
    /// </summary>
    public static void PlayEnter(
        UIElement el,
        int delayMs = 0,
        float fromY = 10f,
        float fromScale = 1f, // ignored — scale softens logos
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

        // Snap rise to whole pixels so we never park mid-pixel.
        var rise = (float)Math.Round(Math.Clamp(fromY, 0f, 10f));
        var settle = Math.Clamp(toOpacity, 0.1, 1.0);
        ScheduleEnsureVisible(el, delayMs + EntranceMs + 48, settle);

        try
        {
            var tf = EnsureTransform(el);
            tf.TranslateX = 0;
            tf.TranslateY = rise;
            tf.ScaleX = 1;
            tf.ScaleY = 1;

            el.Opacity = 0;
            el.IsHitTestVisible = false;

            var sb = new Storyboard();
            sb.Children.Add(Fade(el, 0, settle, FadeMs, delayMs));
            if (rise > 0)
                sb.Children.Add(TranslateY(tf, rise, 0, EntranceMs, delayMs));

            sb.Completed += (_, _) =>
            {
                try
                {
                    el.Opacity = settle;
                    el.IsHitTestVisible = enableHit;
                    // Drop transform so layout owns pixels (no residual matrix blur).
                    el.RenderTransform = null;
                }
                catch { }
            };
            sb.Begin();

            if (enableHit)
            {
                var captured = el;
                var d = delayMs + 70;
                ScheduleOnUi(captured, d, () =>
                {
                    try { captured.IsHitTestVisible = true; }
                    catch { }
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
        int baseDelayMs = 16,
        int stepMs = StaggerStepMs,
        float fromY = 8f,
        float fromScale = 1f)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is null) continue;
            PlayEnter(items[i], baseDelayMs + i * stepMs, fromY, fromScale);
        }
    }

    /// <summary>
    /// Feature-list entrance for module pages: subtle staggered fade + short rise
    /// on the freshly realized tiles (same storyboard language as the dashboard
    /// PlayStagger). Waits a few frames for the ItemsRepeater to realize children,
    /// then animates each tile toward its current (data-bound) opacity so dimmed
    /// inactive tiles stay dimmed. Callers gate re-entry (first loaded-only).
    /// </summary>
    public static void PlayListEnter(FrameworkElement host, int expectedCount = 0)
    {
        if (host is null) return;
        if (MotionDisabled) return; // rows are data-bound visible; nothing to reveal
        _ = RunListEnterAsync(host, expectedCount);
    }

    private static async Task RunListEnterAsync(FrameworkElement host, int expectedCount)
    {
        try
        {
            // Wait (UI thread) until the repeater realized its items.
            for (var attempt = 0; attempt < 24; attempt++)
            {
                var count = VisualTreeHelper.GetChildrenCount(host);
                if (count > 0 && count >= expectedCount)
                    break;
                await Task.Delay(16);
            }

            var items = new List<(UIElement El, double Target)>();
            var childCount = VisualTreeHelper.GetChildrenCount(host);
            for (var i = 0; i < childCount; i++)
            {
                if (VisualTreeHelper.GetChild(host, i) is UIElement el)
                    items.Add((el, el.Opacity <= 0 ? 1.0 : el.Opacity));
            }
            if (items.Count == 0) return;

            for (var i = 0; i < items.Count; i++)
                PlayEnter(items[i].El, i * ListStaggerStepMs, fromY: 8f, toOpacity: items[i].Target);
        }
        catch { }
    }

    /// <summary>
    /// Selection navigation is immediate. The pressed visual state already provides
    /// feedback; adding another storyboard here only makes the app feel latent.
    /// </summary>
    public static void PlaySelect(UIElement el, Action? onDone = null)
    {
        EnsureVisible(el);
        onDone?.Invoke();
    }

    /// <summary>Module page soft fade-in.</summary>
    public static void PlayPageEnter(UIElement root)
    {
        if (MotionDisabled)
        {
            EnsureVisible(root);
            return;
        }

        ClearTransform(root);
        try
        {
            root.Opacity = 0.94;
            root.IsHitTestVisible = true;
            var sb = new Storyboard();
            sb.Children.Add(Fade(root, 0.94, 1, 140, 0));
            sb.Completed += (_, _) =>
            {
                try
                {
                    root.Opacity = 1;
                    ClearTransform(root);
                }
                catch { }
            };
            sb.Begin();
            ScheduleEnsureVisible(root, 190);
        }
        catch
        {
            EnsureVisible(root);
        }
    }

    private static void ClearTransform(UIElement el)
    {
        try
        {
            el.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            if (el.RenderTransform is CompositeTransform tf)
            {
                tf.TranslateX = 0;
                tf.TranslateY = 0;
                tf.ScaleX = 1;
                tf.ScaleY = 1;
                tf.Rotation = 0;
            }
            else
            {
                el.RenderTransform = null;
            }
        }
        catch { }
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

    /// <summary>
    /// Single-shot UI-queue fallback for storyboard completion. This avoids
    /// creating thread-pool work just to wait for a visual transition.
    /// </summary>
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
                finally
                {
                    timer = null;
                }
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
            EasingFunction = Glide(),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(a, target);
        Storyboard.SetTargetProperty(a, "Opacity");
        return a;
    }

    private static DoubleAnimation TranslateY(CompositeTransform tf, double from, double to, int ms, int delayMs)
    {
        var a = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            // EaseOut only — BackEase overshoot leaves subpixel soft frames.
            EasingFunction = Glide(),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(a, tf);
        Storyboard.SetTargetProperty(a, "TranslateY");
        return a;
    }
}
