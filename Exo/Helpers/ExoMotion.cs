using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Exo.Helpers;

/// <summary>
/// Safe open animations (XAML Storyboards only).
/// - Never animates Composition Opacity (blanks UI).
/// - Cards / feature tiles: fade + light rise; select: quick press pulse before navigate.
/// </summary>
public static class ExoMotion
{
    // Short, clean motion — no bouncy spring on content.
    public const int EntranceMs = 240;
    public const int FadeMs = 180;
    public const int StaggerStepMs = 22;
    public const int SelectMs = 90;
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
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(el);
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Offset");
            visual.StopAnimation("Scale");
            visual.StopAnimation("Scale.X");
            visual.StopAnimation("Scale.Y");
            visual.Opacity = 1f;
            visual.Offset = Vector3.Zero;
            visual.Scale = Vector3.One;
            visual.CenterPoint = Vector3.Zero;
        }
        catch { }

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
        // Snap rise to whole pixels so we never park mid-pixel.
        var rise = (float)Math.Round(Math.Clamp(fromY, 0f, 10f));
        var settle = Math.Clamp(toOpacity, 0.1, 1.0);
        ScheduleEnsureVisible(el, delayMs + EntranceMs + 48, settle);

        try
        {
            ForceCompositionIdentity(el);
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
                    ForceCompositionIdentity(el);
                }
                catch { }
            };
            sb.Begin();

            if (enableHit)
            {
                var captured = el;
                var d = delayMs + 70;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(d);
                    try { captured.DispatcherQueue?.TryEnqueue(() => captured.IsHitTestVisible = true); }
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
    /// Card select: snappy dim pulse (no scale), then navigate.
    /// Clear "you picked this" without softening logos.
    /// </summary>
    public static void PlaySelect(UIElement el, Action? onDone = null)
    {
        try
        {
            ForceCompositionIdentity(el);
            el.RenderTransform = null;
            el.Opacity = 1;
            el.IsHitTestVisible = true;

            var sb = new Storyboard();
            // Soft dim pulse — no scale, no bounce.
            sb.Children.Add(Fade(el, 1, 0.86, 40, 0));
            sb.Children.Add(Fade(el, 0.86, 1, 70, 40));

            var finished = false;
            void Once()
            {
                if (finished) return;
                finished = true;
                try
                {
                    el.Opacity = 1;
                    el.RenderTransform = null;
                    ForceCompositionIdentity(el);
                }
                catch { }
                onDone?.Invoke();
            }
            sb.Completed += (_, _) => Once();
            sb.Begin();
            _ = Task.Delay(SelectMs + 50).ContinueWith(_ =>
            {
                try { el.DispatcherQueue?.TryEnqueue(Once); } catch { try { Once(); } catch { } }
            });
        }
        catch
        {
            EnsureVisible(el);
            onDone?.Invoke();
        }
    }

    /// <summary>Module page soft fade-in.</summary>
    public static void PlayPageEnter(UIElement root)
    {
        ForceCompositionIdentity(root);
        ClearTransform(root);
        try
        {
            root.Opacity = 0.9;
            root.IsHitTestVisible = true;
            var sb = new Storyboard();
            sb.Children.Add(Fade(root, 0.9, 1, 200, 0));
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
            ScheduleEnsureVisible(root, 260);
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

    private static void ForceCompositionIdentity(UIElement el)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(el);
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Offset");
            visual.StopAnimation("Scale");
            visual.Opacity = 1f;
            visual.Offset = Vector3.Zero;
            visual.Scale = Vector3.One;
            visual.CenterPoint = Vector3.Zero;
        }
        catch { }
    }

    private static void ScheduleEnsureVisible(UIElement el, int delayMs, double settleOpacity = 1.0)
    {
        var captured = el;
        var opacity = Math.Clamp(settleOpacity, 0.1, 1.0);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Math.Max(0, delayMs));
                captured.DispatcherQueue?.TryEnqueue(() =>
                {
                    try
                    {
                        EnsureVisible(captured);
                        if (opacity < 1.0)
                            captured.Opacity = opacity;
                    }
                    catch { }
                });
            }
            catch { }
        });
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
