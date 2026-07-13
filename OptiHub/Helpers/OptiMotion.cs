using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace OptiHub.Helpers;

/// <summary>
/// Single motion language for OptiHub (Kinetics-inspired).
/// Prefer Composition for entrance (no first-frame flicker);
/// Storyboard for simple overlay chrome.
/// Curves: glide ~cubic-bezier(0.16,1,0.3,1), spring ~cubic-bezier(0.34,1.2,0.64,1).
/// </summary>
public static class OptiMotion
{
    public const int EntranceMs = 480;
    public const int FadeMs = 400;
    public const int StaggerStepMs = 52;
    public const int PressMs = 80;
    public const int SpringBackMs = 420;

    public static EasingFunctionBase Spring() =>
        new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.28 };

    public static EasingFunctionBase Glide() =>
        new CubicEase { EasingMode = EasingMode.EaseOut };

    public static CompositeTransform EnsureTransform(UIElement el)
    {
        if (el.RenderTransform is CompositeTransform ct)
            return ct;
        ct = new CompositeTransform();
        el.RenderTransform = ct;
        el.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        return ct;
    }

    /// <summary>
    /// Stop every composition animation and snap Offset/Scale/Opacity back to rest.
    /// Critical for overlays: leftover Offset/Scale after close pins the sheet in a corner.
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
            visual.Opacity = show ? 1f : 0f;
            visual.Offset = Vector3.Zero;
            visual.Scale = Vector3.One;
            ApplyCenterPoint(visual, el);
        }
        catch { /* compositor unavailable */ }

        el.Opacity = show ? 1 : 0;
        el.IsHitTestVisible = show;
        try
        {
            var tf = EnsureTransform(el);
            tf.TranslateX = 0;
            tf.TranslateY = 0;
            tf.ScaleX = 1;
            tf.ScaleY = 1;
        }
        catch { }
    }

    private static void ApplyCenterPoint(Visual visual, UIElement el, float fallbackW = 200f, float fallbackH = 100f)
    {
        if (el is not FrameworkElement fe) return;
        var w = (float)(fe.ActualWidth > 0 ? fe.ActualWidth : (double.IsNaN(fe.Width) || fe.Width <= 0 ? 0 : fe.Width));
        var h = (float)(fe.ActualHeight > 0 ? fe.ActualHeight : (double.IsNaN(fe.Height) || fe.Height <= 0 ? 0 : fe.Height));
        if (w <= 0) w = fallbackW;
        if (h <= 0) h = fallbackH;
        visual.CenterPoint = new Vector3(w / 2f, h / 2f, 0);
    }

    /// <summary>Prime element invisible + offset (call before first paint / host reveal).</summary>
    public static void PrimeHidden(UIElement el, float fromY = 18f, float fromScale = 0.96f)
    {
        el.Opacity = 0;
        el.IsHitTestVisible = false;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(el);
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Offset");
            visual.StopAnimation("Scale");
            visual.Opacity = 0;
            visual.Offset = new Vector3(0, fromY, 0);
            visual.Scale = new Vector3(fromScale, fromScale, 1);
            ApplyCenterPoint(visual, el);
        }
        catch
        {
            var tf = EnsureTransform(el);
            tf.TranslateY = fromY;
            tf.ScaleX = fromScale;
            tf.ScaleY = fromScale;
        }
    }

    /// <summary>Composition fade + rise + scale (Kinetics stagger / spring).</summary>
    public static void PlayEnter(
        UIElement el,
        int delayMs = 0,
        float fromY = 16f,
        float fromScale = 0.96f,
        bool enableHit = true)
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(el);
            var c = visual.Compositor;

            visual.StopAnimation("Opacity");
            visual.StopAnimation("Offset");
            visual.StopAnimation("Scale");
            ApplyCenterPoint(visual, el);

            visual.Opacity = 0;
            visual.Offset = new Vector3(0, fromY, 0);
            visual.Scale = new Vector3(fromScale, fromScale, 1);

            var delay = TimeSpan.FromMilliseconds(delayMs);
            var glide = c.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
            var spring = c.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1.15f), new Vector2(0.36f, 1f));

            var fade = c.CreateScalarKeyFrameAnimation();
            fade.DelayTime = delay;
            fade.Duration = TimeSpan.FromMilliseconds(FadeMs);
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f, glide);
            visual.StartAnimation("Opacity", fade);

            var rise = c.CreateVector3KeyFrameAnimation();
            rise.DelayTime = delay;
            rise.Duration = TimeSpan.FromMilliseconds(EntranceMs);
            rise.InsertKeyFrame(0f, new Vector3(0, fromY, 0));
            rise.InsertKeyFrame(1f, Vector3.Zero, spring);
            visual.StartAnimation("Offset", rise);

            var scale = c.CreateVector3KeyFrameAnimation();
            scale.DelayTime = delay;
            scale.Duration = TimeSpan.FromMilliseconds(EntranceMs + 20);
            scale.InsertKeyFrame(0f, new Vector3(fromScale, fromScale, 1));
            scale.InsertKeyFrame(1f, Vector3.One, spring);
            visual.StartAnimation("Scale", scale);

            // XAML opacity ends at 1 so later logic is consistent (composition drives the frame).
            el.Opacity = 1;
            if (enableHit)
            {
                var captured = el;
                var d = delayMs + 90;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(d);
                    try
                    {
                        captured.DispatcherQueue?.TryEnqueue(() => captured.IsHitTestVisible = true);
                    }
                    catch { }
                });
            }
        }
        catch
        {
            el.Opacity = 1;
            el.IsHitTestVisible = true;
            var tf = EnsureTransform(el);
            tf.TranslateY = 0;
            tf.ScaleX = 1;
            tf.ScaleY = 1;
        }
    }

    /// <summary>Stagger entrance for a list of elements.</summary>
    public static void PlayStagger(
        IReadOnlyList<UIElement> items,
        int baseDelayMs = 40,
        int stepMs = StaggerStepMs,
        float fromY = 16f,
        float fromScale = 0.96f)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is null) continue;
            PlayEnter(items[i], baseDelayMs + i * stepMs, fromY, fromScale);
        }
    }

    /// <summary>Settings / modal open: scrim fade + sheet spring (toast overshoot).</summary>
    public static void PlayOverlayOpen(UIElement scrimHost, UIElement sheet)
    {
        // Always clear leftover composition state from a previous close/open cycle.
        ResetVisual(scrimHost, show: false);
        ResetVisual(sheet, show: false);

        // Force layout so CenterPoint is real (scale from (0,0) pins the sheet in a corner).
        if (sheet is FrameworkElement sheetFe)
            sheetFe.UpdateLayout();

        scrimHost.Opacity = 0;
        sheet.Opacity = 0;
        try
        {
            var scrimVis = ElementCompositionPreview.GetElementVisual(scrimHost);
            var sheetVis = ElementCompositionPreview.GetElementVisual(sheet);
            var c = scrimVis.Compositor;

            scrimVis.StopAnimation("Opacity");
            sheetVis.StopAnimation("Opacity");
            sheetVis.StopAnimation("Offset");
            sheetVis.StopAnimation("Scale");

            ApplyCenterPoint(sheetVis, sheet, fallbackW: 420f, fallbackH: 400f);

            scrimVis.Opacity = 0;
            sheetVis.Opacity = 0;
            sheetVis.Offset = new Vector3(0, 22, 0);
            sheetVis.Scale = new Vector3(0.96f, 0.96f, 1);

            var glide = c.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
            var spring = c.CreateCubicBezierEasingFunction(new Vector2(0.34f, 1.2f), new Vector2(0.64f, 1f));

            var scrimFade = c.CreateScalarKeyFrameAnimation();
            scrimFade.Duration = TimeSpan.FromMilliseconds(220);
            scrimFade.InsertKeyFrame(0f, 0f);
            scrimFade.InsertKeyFrame(1f, 1f, glide);
            scrimVis.StartAnimation("Opacity", scrimFade);

            var sheetFade = c.CreateScalarKeyFrameAnimation();
            sheetFade.DelayTime = TimeSpan.FromMilliseconds(20);
            sheetFade.Duration = TimeSpan.FromMilliseconds(300);
            sheetFade.InsertKeyFrame(0f, 0f);
            sheetFade.InsertKeyFrame(1f, 1f, glide);
            sheetVis.StartAnimation("Opacity", sheetFade);

            var rise = c.CreateVector3KeyFrameAnimation();
            rise.DelayTime = TimeSpan.FromMilliseconds(16);
            rise.Duration = TimeSpan.FromMilliseconds(420);
            rise.InsertKeyFrame(0f, new Vector3(0, 22, 0));
            rise.InsertKeyFrame(1f, Vector3.Zero, spring);
            sheetVis.StartAnimation("Offset", rise);

            var scale = c.CreateVector3KeyFrameAnimation();
            scale.DelayTime = TimeSpan.FromMilliseconds(16);
            scale.Duration = TimeSpan.FromMilliseconds(420);
            scale.InsertKeyFrame(0f, new Vector3(0.96f, 0.96f, 1));
            scale.InsertKeyFrame(1f, Vector3.One, spring);
            sheetVis.StartAnimation("Scale", scale);

            // XAML props end at rest so logic after animation is consistent.
            scrimHost.Opacity = 1;
            sheet.Opacity = 1;
            sheet.IsHitTestVisible = true;
            scrimHost.IsHitTestVisible = true;

            // After spring ends, hard-snap composition to identity (no residual offset).
            _ = Task.Delay(460).ContinueWith(_ =>
            {
                try
                {
                    sheet.DispatcherQueue?.TryEnqueue(() =>
                    {
                        try
                        {
                            var v = ElementCompositionPreview.GetElementVisual(sheet);
                            // Only snap if still open (opacity > 0) so close isn't interrupted.
                            if (sheet.Opacity > 0.5)
                            {
                                v.StopAnimation("Offset");
                                v.StopAnimation("Scale");
                                v.Offset = Vector3.Zero;
                                v.Scale = Vector3.One;
                                v.Opacity = 1;
                                ApplyCenterPoint(v, sheet);
                            }
                        }
                        catch { }
                    });
                }
                catch { }
            });
        }
        catch
        {
            // Storyboard fallback
            var sb = new Storyboard();
            var tf = EnsureTransform(sheet);
            tf.TranslateY = 20;
            tf.ScaleX = 0.96;
            tf.ScaleY = 0.96;
            sb.Children.Add(FadeSb(scrimHost, 0, 1, 220, 0));
            sb.Children.Add(FadeSb(sheet, 0, 1, 280, 40));
            sb.Children.Add(SlideYSb(tf, 20, 0, 400, 24));
            sb.Children.Add(ScaleSb(tf, "ScaleX", 0.96, 1, 400, 24));
            sb.Children.Add(ScaleSb(tf, "ScaleY", 0.96, 1, 400, 24));
            sb.Begin();
            scrimHost.Opacity = 1;
            sheet.Opacity = 1;
            sheet.IsHitTestVisible = true;
        }
    }

    /// <summary>Quick fade-out + drop for overlay close. Always ResetVisual in onDone.</summary>
    public static void PlayOverlayClose(UIElement scrimHost, UIElement sheet, Action? onDone = null)
    {
        try
        {
            var scrimVis = ElementCompositionPreview.GetElementVisual(scrimHost);
            var sheetVis = ElementCompositionPreview.GetElementVisual(sheet);
            var c = scrimVis.Compositor;
            var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));

            // Start from a known rest so close doesn't inherit a mid-spring offset.
            sheetVis.StopAnimation("Offset");
            sheetVis.StopAnimation("Scale");
            sheetVis.Offset = Vector3.Zero;
            sheetVis.Scale = Vector3.One;
            ApplyCenterPoint(sheetVis, sheet);

            using var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
            var finished = false;
            void Once()
            {
                if (finished) return;
                finished = true;
                // Snap fully to rest BEFORE caller collapses Visibility.
                ResetVisual(scrimHost, show: true);
                ResetVisual(sheet, show: true);
                onDone?.Invoke();
            }
            batch.Completed += (_, _) => Once();

            var scrimFade = c.CreateScalarKeyFrameAnimation();
            scrimFade.Duration = TimeSpan.FromMilliseconds(160);
            scrimFade.InsertKeyFrame(1f, 0f, ease);
            scrimVis.StartAnimation("Opacity", scrimFade);

            var sheetFade = c.CreateScalarKeyFrameAnimation();
            sheetFade.Duration = TimeSpan.FromMilliseconds(140);
            sheetFade.InsertKeyFrame(1f, 0f, ease);
            sheetVis.StartAnimation("Opacity", sheetFade);

            // Fade only — no Offset drop (Offset on a centered host is what pinned the corner).
            batch.End();

            _ = Task.Delay(200).ContinueWith(_ =>
            {
                try { Once(); } catch { }
            });
        }
        catch
        {
            ResetVisual(scrimHost, show: true);
            ResetVisual(sheet, show: true);
            onDone?.Invoke();
        }
    }

    /// <summary>Page root entrance (optimizer modules).</summary>
    public static void PlayPageEnter(UIElement root)
    {
        PrimeHidden(root, fromY: 14f, fromScale: 0.985f);
        PlayEnter(root, delayMs: 0, fromY: 14f, fromScale: 0.985f, enableHit: true);
    }

    private static DoubleAnimation FadeSb(UIElement target, double from, double to, int ms, int delayMs)
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

    private static DoubleAnimation SlideYSb(CompositeTransform transform, double from, double to, int ms, int delayMs)
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
        Storyboard.SetTarget(a, transform);
        Storyboard.SetTargetProperty(a, "TranslateY");
        return a;
    }

    private static DoubleAnimation ScaleSb(CompositeTransform transform, string prop, double from, double to, int ms, int delayMs)
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
        Storyboard.SetTarget(a, transform);
        Storyboard.SetTargetProperty(a, prop);
        return a;
    }
}
