using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.Numerics;

namespace OptiHub.Helpers;

/// <summary>
/// Soft open animations via XAML Storyboards only.
/// Never animates Composition visual.Opacity (that overrode XAML and blanked UI).
/// Every entrance ends fully visible; a delayed EnsureVisible is the hard fail-safe.
/// </summary>
public static class OptiMotion
{
    public const int EntranceMs = 380;
    public const int FadeMs = 240;
    public const int StaggerStepMs = 45;
    public const int OverlayOpenMs = 320;
    public const int OverlayCloseMs = 160;

    public static EasingFunctionBase Glide() =>
        new CubicEase { EasingMode = EasingMode.EaseOut };

    public static EasingFunctionBase Spring() =>
        new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.22 };

    public static CompositeTransform EnsureTransform(UIElement el)
    {
        if (el.RenderTransform is CompositeTransform ct)
            return ct;
        ct = new CompositeTransform();
        el.RenderTransform = ct;
        el.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        return ct;
    }

    /// <summary>Hard-show at identity. Safe after any animation.</summary>
    public static void EnsureVisible(UIElement el)
    {
        ResetVisual(el, show: true);
    }

    /// <summary>
    /// Clear composition overrides (always opacity 1) + XAML rest state.
    /// Composition Opacity is never left at 0.
    /// </summary>
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
        try
        {
            if (el.RenderTransform is CompositeTransform tf)
            {
                tf.TranslateX = 0;
                tf.TranslateY = 0;
                tf.ScaleX = 1;
                tf.ScaleY = 1;
            }
        }
        catch { }
    }

    /// <summary>Card / row entrance: XAML fade + rise (composition opacity untouched).</summary>
    public static void PlayEnter(
        UIElement el,
        int delayMs = 0,
        float fromY = 14f,
        float fromScale = 0.97f,
        bool enableHit = true)
    {
        // Fail-safe: always fully visible after the animation window.
        ScheduleEnsureVisible(el, delayMs + EntranceMs + 100);

        try
        {
            // Force composition identity first so nothing overrides the XAML fade.
            try
            {
                var visual = ElementCompositionPreview.GetElementVisual(el);
                visual.StopAnimation("Opacity");
                visual.Opacity = 1f;
                visual.Offset = Vector3.Zero;
                visual.Scale = Vector3.One;
            }
            catch { }

            var tf = EnsureTransform(el);
            el.Opacity = 0;
            el.IsHitTestVisible = false;
            tf.TranslateY = fromY;
            tf.ScaleX = fromScale;
            tf.ScaleY = fromScale;

            var sb = new Storyboard();
            sb.Children.Add(Fade(el, 0, 1, FadeMs, delayMs));
            sb.Children.Add(TranslateY(tf, fromY, 0, EntranceMs, delayMs));
            sb.Children.Add(Scale(tf, "ScaleX", fromScale, 1, EntranceMs, delayMs));
            sb.Children.Add(Scale(tf, "ScaleY", fromScale, 1, EntranceMs, delayMs));
            sb.Completed += (_, _) =>
            {
                try
                {
                    el.Opacity = 1;
                    el.IsHitTestVisible = enableHit;
                    tf.TranslateY = 0;
                    tf.ScaleX = 1;
                    tf.ScaleY = 1;
                }
                catch { }
            };
            sb.Begin();

            if (enableHit)
            {
                var captured = el;
                var d = delayMs + 80;
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
        int baseDelayMs = 20,
        int stepMs = StaggerStepMs,
        float fromY = 12f,
        float fromScale = 0.97f)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is null) continue;
            PlayEnter(items[i], baseDelayMs + i * stepMs, fromY, fromScale);
        }
    }

    /// <summary>
    /// Settings open: scrim fades in, sheet fades + rises slightly.
    /// Uses XAML Opacity only. Layout keeps the sheet centered.
    /// </summary>
    public static void PlayOverlayOpen(UIElement scrimHost, UIElement sheet)
    {
        try
        {
            // Composition identity — never drive host opacity via composition.
            ForceCompositionIdentity(scrimHost);
            ForceCompositionIdentity(sheet);

            var tf = EnsureTransform(sheet);
            scrimHost.Opacity = 0;
            sheet.Opacity = 0;
            sheet.IsHitTestVisible = false;
            tf.TranslateY = 18;
            tf.ScaleX = 0.97;
            tf.ScaleY = 0.97;

            var sb = new Storyboard();
            sb.Children.Add(Fade(scrimHost, 0, 1, 200, 0));
            sb.Children.Add(Fade(sheet, 0, 1, OverlayOpenMs, 20));
            sb.Children.Add(TranslateY(tf, 18, 0, OverlayOpenMs, 20));
            sb.Children.Add(Scale(tf, "ScaleX", 0.97, 1, OverlayOpenMs, 20));
            sb.Children.Add(Scale(tf, "ScaleY", 0.97, 1, OverlayOpenMs, 20));
            sb.Completed += (_, _) =>
            {
                try
                {
                    scrimHost.Opacity = 1;
                    sheet.Opacity = 1;
                    sheet.IsHitTestVisible = true;
                    tf.TranslateY = 0;
                    tf.ScaleX = 1;
                    tf.ScaleY = 1;
                }
                catch { }
            };
            sb.Begin();

            ScheduleEnsureVisible(scrimHost, OverlayOpenMs + 80);
            ScheduleEnsureVisible(sheet, OverlayOpenMs + 80);
        }
        catch
        {
            EnsureVisible(scrimHost);
            EnsureVisible(sheet);
        }
    }

    /// <summary>Settings close: quick fade, then onDone (caller collapses Visibility).</summary>
    public static void PlayOverlayClose(UIElement scrimHost, UIElement sheet, Action? onDone = null)
    {
        try
        {
            ForceCompositionIdentity(scrimHost);
            ForceCompositionIdentity(sheet);

            var tf = EnsureTransform(sheet);
            var sb = new Storyboard();
            sb.Children.Add(Fade(scrimHost, scrimHost.Opacity, 0, OverlayCloseMs, 0));
            sb.Children.Add(Fade(sheet, sheet.Opacity, 0, OverlayCloseMs, 0));
            sb.Children.Add(TranslateY(tf, 0, 10, OverlayCloseMs, 0));
            var finished = false;
            void Once()
            {
                if (finished) return;
                finished = true;
                try
                {
                    // Rest identity so next open starts clean.
                    scrimHost.Opacity = 1;
                    sheet.Opacity = 1;
                    tf.TranslateY = 0;
                    tf.ScaleX = 1;
                    tf.ScaleY = 1;
                }
                catch { }
                onDone?.Invoke();
            }
            sb.Completed += (_, _) => Once();
            sb.Begin();
            _ = Task.Delay(OverlayCloseMs + 40).ContinueWith(_ =>
            {
                try { scrimHost.DispatcherQueue?.TryEnqueue(Once); } catch { try { Once(); } catch { } }
            });
        }
        catch
        {
            EnsureVisible(scrimHost);
            EnsureVisible(sheet);
            onDone?.Invoke();
        }
    }

    /// <summary>Module page soft fade-in (XAML opacity only).</summary>
    public static void PlayPageEnter(UIElement root)
    {
        ForceCompositionIdentity(root);
        try
        {
            root.Opacity = 0.88;
            root.IsHitTestVisible = true;
            var sb = new Storyboard();
            sb.Children.Add(Fade(root, 0.88, 1, 220, 0));
            sb.Completed += (_, _) => { try { root.Opacity = 1; } catch { } };
            sb.Begin();
            ScheduleEnsureVisible(root, 280);
        }
        catch
        {
            EnsureVisible(root);
        }
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
        }
        catch { }
    }

    private static void ScheduleEnsureVisible(UIElement el, int delayMs)
    {
        var captured = el;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Math.Max(0, delayMs));
                captured.DispatcherQueue?.TryEnqueue(() =>
                {
                    try { EnsureVisible(captured); } catch { }
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
            EasingFunction = Spring(),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(a, tf);
        Storyboard.SetTargetProperty(a, "TranslateY");
        return a;
    }

    private static DoubleAnimation Scale(CompositeTransform tf, string prop, double from, double to, int ms, int delayMs)
    {
        var a = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = Spring(),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(a, tf);
        Storyboard.SetTargetProperty(a, prop);
        return a;
    }
}
