using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.Numerics;

namespace OptiHub.Helpers;

/// <summary>
/// Safe open animations (XAML Storyboards only).
/// - Never animates Composition Opacity (blanks UI).
/// - Settings overlay: fade only (no scale/translate — those pin a centered host top-left).
/// - Cards: fade + light rise; select: quick press pulse before navigate.
/// </summary>
public static class OptiMotion
{
    public const int EntranceMs = 340;
    public const int FadeMs = 260;
    public const int StaggerStepMs = 40;
    public const int OverlayOpenMs = 260;
    public const int OverlayCloseMs = 140;
    public const int SelectMs = 140;

    public static EasingFunctionBase Glide() =>
        new CubicEase { EasingMode = EasingMode.EaseOut };

    public static EasingFunctionBase Spring() =>
        new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 };

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
            // Always clear transform if present (and don't leave scale/offset residue).
            if (el.RenderTransform is CompositeTransform tf)
            {
                tf.TranslateX = 0;
                tf.TranslateY = 0;
                tf.ScaleX = 1;
                tf.ScaleY = 1;
                tf.Rotation = 0;
            }
            else if (el.RenderTransform is not null)
            {
                // Unknown transform type — drop it so layout owns position again.
                el.RenderTransform = null;
            }
        }
        catch { }
    }

    /// <summary>
    /// Card entrance: XAML fade + slight rise only (no scale — scale drifts toward top-left).
    /// </summary>
    public static void PlayEnter(
        UIElement el,
        int delayMs = 0,
        float fromY = 12f,
        float fromScale = 1f, // ignored — kept for call-site compat
        bool enableHit = true)
    {
        ScheduleEnsureVisible(el, delayMs + EntranceMs + 80);

        try
        {
            ForceCompositionIdentity(el);
            var tf = EnsureTransform(el);
            tf.TranslateX = 0;
            tf.TranslateY = fromY;
            tf.ScaleX = 1;
            tf.ScaleY = 1;

            el.Opacity = 0;
            el.IsHitTestVisible = false;

            var sb = new Storyboard();
            sb.Children.Add(Fade(el, 0, 1, FadeMs, delayMs));
            sb.Children.Add(TranslateY(tf, fromY, 0, EntranceMs, delayMs));
            sb.Completed += (_, _) =>
            {
                try
                {
                    el.Opacity = 1;
                    el.IsHitTestVisible = enableHit;
                    tf.TranslateX = 0;
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
        float fromY = 12f,
        float fromScale = 1f)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is null) continue;
            PlayEnter(items[i], baseDelayMs + i * stepMs, fromY, fromScale);
        }
    }

    /// <summary>
    /// Card select / press: quick scale pulse, then callback (navigate).
    /// Visible feedback when tapping a home card.
    /// </summary>
    public static void PlaySelect(UIElement el, Action? onDone = null)
    {
        try
        {
            ForceCompositionIdentity(el);
            var tf = EnsureTransform(el);
            tf.ScaleX = 1;
            tf.ScaleY = 1;
            el.Opacity = 1;
            el.IsHitTestVisible = true;

            var sb = new Storyboard();
            // Press in
            sb.Children.Add(Scale(tf, "ScaleX", 1, 0.94, 70, 0));
            sb.Children.Add(Scale(tf, "ScaleY", 1, 0.94, 70, 0));
            // Spring out
            sb.Children.Add(Scale(tf, "ScaleX", 0.94, 1.02, 90, 70));
            sb.Children.Add(Scale(tf, "ScaleY", 0.94, 1.02, 90, 70));
            // Settle
            sb.Children.Add(Scale(tf, "ScaleX", 1.02, 1, 80, 160));
            sb.Children.Add(Scale(tf, "ScaleY", 1.02, 1, 80, 160));

            var finished = false;
            void Once()
            {
                if (finished) return;
                finished = true;
                try
                {
                    tf.ScaleX = 1;
                    tf.ScaleY = 1;
                    tf.TranslateY = 0;
                    el.Opacity = 1;
                }
                catch { }
                onDone?.Invoke();
            }
            sb.Completed += (_, _) => Once();
            sb.Begin();
            _ = Task.Delay(SelectMs + 80).ContinueWith(_ =>
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

    /// <summary>
    /// Settings scrim fade-in only. Do NOT pass the sheet — transforms/opacity on a
    /// centered host is what pinned settings top-left after remeasure (Check for updates).
    /// </summary>
    public static void PlayScrimFadeIn(UIElement scrim)
    {
        try
        {
            ForceCompositionIdentity(scrim);
            ClearTransform(scrim);
            scrim.Opacity = 0;
            var sb = new Storyboard();
            sb.Children.Add(Fade(scrim, 0, 1, 200, 0));
            sb.Completed += (_, _) =>
            {
                try { scrim.Opacity = 1; ClearTransform(scrim); } catch { }
            };
            sb.Begin();
        }
        catch
        {
            try { scrim.Opacity = 1; } catch { }
        }
    }

    /// <summary>Settings scrim fade-out only, then onDone.</summary>
    public static void PlayScrimFadeOut(UIElement scrim, Action? onDone = null)
    {
        try
        {
            ForceCompositionIdentity(scrim);
            ClearTransform(scrim);
            var sb = new Storyboard();
            sb.Children.Add(Fade(scrim, Math.Clamp(scrim.Opacity, 0, 1), 0, OverlayCloseMs, 0));
            var finished = false;
            void Once()
            {
                if (finished) return;
                finished = true;
                try { scrim.Opacity = 1; ClearTransform(scrim); } catch { }
                onDone?.Invoke();
            }
            sb.Completed += (_, _) => Once();
            sb.Begin();
            _ = Task.Delay(OverlayCloseMs + 40).ContinueWith(_ =>
            {
                try { scrim.DispatcherQueue?.TryEnqueue(Once); } catch { try { Once(); } catch { } }
            });
        }
        catch
        {
            try { scrim.Opacity = 1; } catch { }
            onDone?.Invoke();
        }
    }

    /// <summary>Back-compat names — scrim-only; sheet argument ignored on purpose.</summary>
    public static void PlayOverlayOpen(UIElement scrimHost, UIElement sheet)
    {
        // Sheet must stay layout-owned. Only fade scrim.
        try
        {
            sheet.Opacity = 1;
            sheet.RenderTransform = null;
            ClearCompositionOnly(sheet);
        }
        catch { }
        PlayScrimFadeIn(scrimHost);
    }

    public static void PlayOverlayClose(UIElement scrimHost, UIElement sheet, Action? onDone = null)
    {
        try
        {
            sheet.Opacity = 1;
            sheet.RenderTransform = null;
            ClearCompositionOnly(sheet);
        }
        catch { }
        PlayScrimFadeOut(scrimHost, onDone);
    }

    /// <summary>Clear composition layer only — does not touch XAML Opacity (safe mid-layout).</summary>
    public static void ClearCompositionOnly(UIElement el)
    {
        ForceCompositionIdentity(el);
        try
        {
            if (el.RenderTransform is not null)
                el.RenderTransform = null;
        }
        catch { }
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
