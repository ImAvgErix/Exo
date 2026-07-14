using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace OptiHub.Helpers;

/// <summary>
/// Motion helpers for OptiHub. Reliability first:
/// - Overlay hosts: opacity only (layout owns center).
/// - EnsureVisible always snaps identity so open/back cannot leave half-hidden UI.
/// - Entrances never leave elements permanently primed-hidden.
/// </summary>
public static class OptiMotion
{
    public const int EntranceMs = 360;
    public const int FadeMs = 280;
    public const int StaggerStepMs = 40;

    /// <summary>0–1 multiplies entrance travel. Live-bound from Settings → MotionIntensity.</summary>
    public static double MotionStrength { get; set; } = 1.0;

    private static float Strength => (float)Math.Clamp(MotionStrength, 0.0, 1.0);

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
    /// Hard-show element at identity. Call on every navigate/open so residual
    /// composition never leaves content stuck invisible or offset.
    /// </summary>
    public static void EnsureVisible(UIElement el)
    {
        ResetVisual(el, show: true);
    }

    /// <summary>Stop animations; snap Offset/Scale/Opacity to rest.</summary>
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
            visual.CenterPoint = Vector3.Zero;
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

    /// <summary>Soft entrance for cards/rows. Always ends EnsureVisible (fail-safe).</summary>
    public static void PlayEnter(
        UIElement el,
        int delayMs = 0,
        float fromY = 10f,
        float fromScale = 0.98f,
        bool enableHit = true)
    {
        var y = fromY * Strength;
        var s = 1f - (1f - fromScale) * Strength;

        // Fail-safe: no matter what, element ends fully visible after max wait.
        ScheduleEnsureVisible(el, delayMs + EntranceMs + 80);

        if (Strength < 0.05f)
        {
            EnsureVisible(el);
            return;
        }

        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(el);
            var c = visual.Compositor;

            visual.StopAnimation("Opacity");
            visual.StopAnimation("Offset");
            visual.StopAnimation("Scale");
            visual.CenterPoint = Vector3.Zero;

            visual.Opacity = 0;
            visual.Offset = new Vector3(0, y, 0);
            visual.Scale = new Vector3(s, s, 1);
            el.Opacity = 0;
            el.IsHitTestVisible = false;

            var delay = TimeSpan.FromMilliseconds(delayMs);
            var glide = c.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
            var spring = c.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1.1f), new Vector2(0.36f, 1f));

            var fade = c.CreateScalarKeyFrameAnimation();
            fade.DelayTime = delay;
            fade.Duration = TimeSpan.FromMilliseconds(FadeMs);
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f, glide);
            visual.StartAnimation("Opacity", fade);

            var rise = c.CreateVector3KeyFrameAnimation();
            rise.DelayTime = delay;
            rise.Duration = TimeSpan.FromMilliseconds(EntranceMs);
            rise.InsertKeyFrame(0f, new Vector3(0, y, 0));
            rise.InsertKeyFrame(1f, Vector3.Zero, spring);
            visual.StartAnimation("Offset", rise);

            var scale = c.CreateVector3KeyFrameAnimation();
            scale.DelayTime = delay;
            scale.Duration = TimeSpan.FromMilliseconds(EntranceMs);
            scale.InsertKeyFrame(0f, new Vector3(s, s, 1));
            scale.InsertKeyFrame(1f, Vector3.One, spring);
            visual.StartAnimation("Scale", scale);

            // XAML props end at rest; composition drives the frames.
            el.Opacity = 1;
            if (enableHit)
            {
                var captured = el;
                var d = delayMs + 60;
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
        int baseDelayMs = 30,
        int stepMs = StaggerStepMs,
        float fromY = 10f,
        float fromScale = 0.98f)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is null) continue;
            PlayEnter(items[i], baseDelayMs + i * stepMs, fromY, fromScale);
        }
    }

    /// <summary>
    /// Modal open: opacity fade only. Host Offset/Scale stay identity forever.
    /// </summary>
    public static void PlayOverlayOpen(UIElement scrimHost, UIElement sheet)
    {
        ResetVisual(scrimHost, show: false);
        ResetVisual(sheet, show: false);

        try
        {
            var sheetVis = ElementCompositionPreview.GetElementVisual(sheet);
            sheetVis.Offset = Vector3.Zero;
            sheetVis.Scale = Vector3.One;
            sheetVis.CenterPoint = Vector3.Zero;
        }
        catch { }

        if (sheet is FrameworkElement sheetFe)
            sheetFe.UpdateLayout();

        // Always force-visible shortly after open (composition fade is cosmetic).
        ScheduleEnsureVisible(scrimHost, 320);
        ScheduleEnsureVisible(sheet, 320);

        try
        {
            var scrimVis = ElementCompositionPreview.GetElementVisual(scrimHost);
            var sheetVis = ElementCompositionPreview.GetElementVisual(sheet);
            var c = scrimVis.Compositor;
            var glide = c.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));

            scrimVis.StopAnimation("Opacity");
            sheetVis.StopAnimation("Opacity");
            sheetVis.Offset = Vector3.Zero;
            sheetVis.Scale = Vector3.One;

            scrimVis.Opacity = 0;
            sheetVis.Opacity = 0;

            var scrimFade = c.CreateScalarKeyFrameAnimation();
            scrimFade.Duration = TimeSpan.FromMilliseconds(180);
            scrimFade.InsertKeyFrame(0f, 0f);
            scrimFade.InsertKeyFrame(1f, 1f, glide);
            scrimVis.StartAnimation("Opacity", scrimFade);

            var sheetFade = c.CreateScalarKeyFrameAnimation();
            sheetFade.DelayTime = TimeSpan.FromMilliseconds(12);
            sheetFade.Duration = TimeSpan.FromMilliseconds(220);
            sheetFade.InsertKeyFrame(0f, 0f);
            sheetFade.InsertKeyFrame(1f, 1f, glide);
            sheetVis.StartAnimation("Opacity", sheetFade);

            scrimHost.Opacity = 1;
            sheet.Opacity = 1;
            sheet.IsHitTestVisible = true;
            scrimHost.IsHitTestVisible = true;
        }
        catch
        {
            EnsureVisible(scrimHost);
            EnsureVisible(sheet);
        }
    }

    /// <summary>Modal close: opacity fade, then onDone. Host never gets Offset.</summary>
    public static void PlayOverlayClose(UIElement scrimHost, UIElement sheet, Action? onDone = null)
    {
        try
        {
            var scrimVis = ElementCompositionPreview.GetElementVisual(scrimHost);
            var sheetVis = ElementCompositionPreview.GetElementVisual(sheet);
            var c = scrimVis.Compositor;
            var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));

            sheetVis.StopAnimation("Offset");
            sheetVis.StopAnimation("Scale");
            sheetVis.Offset = Vector3.Zero;
            sheetVis.Scale = Vector3.One;
            sheetVis.CenterPoint = Vector3.Zero;

            using var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
            var finished = false;
            void Once()
            {
                if (finished) return;
                finished = true;
                ResetVisual(scrimHost, show: true);
                ResetVisual(sheet, show: true);
                onDone?.Invoke();
            }
            batch.Completed += (_, _) => Once();

            var scrimFade = c.CreateScalarKeyFrameAnimation();
            scrimFade.Duration = TimeSpan.FromMilliseconds(140);
            scrimFade.InsertKeyFrame(1f, 0f, ease);
            scrimVis.StartAnimation("Opacity", scrimFade);

            var sheetFade = c.CreateScalarKeyFrameAnimation();
            sheetFade.Duration = TimeSpan.FromMilliseconds(120);
            sheetFade.InsertKeyFrame(1f, 0f, ease);
            sheetVis.StartAnimation("Opacity", sheetFade);

            batch.End();

            _ = Task.Delay(180).ContinueWith(_ =>
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

    /// <summary>
    /// Module page enter: ensure fully visible first, optional light fade.
    /// Never primes the whole page to opacity 0 (that is what "broke every open").
    /// </summary>
    public static void PlayPageEnter(UIElement root)
    {
        // Always show immediately — residual opacity-0 on page roots was unusable UI.
        EnsureVisible(root);
        if (Strength < 0.05f) return;

        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(root);
            var c = visual.Compositor;
            visual.StopAnimation("Opacity");
            visual.Offset = Vector3.Zero;
            visual.Scale = Vector3.One;
            visual.Opacity = 0.86f;

            var glide = c.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
            var fade = c.CreateScalarKeyFrameAnimation();
            fade.Duration = TimeSpan.FromMilliseconds(220);
            fade.InsertKeyFrame(0f, 0.86f);
            fade.InsertKeyFrame(1f, 1f, glide);
            visual.StartAnimation("Opacity", fade);
            root.Opacity = 1;
            root.IsHitTestVisible = true;
            ScheduleEnsureVisible(root, 280);
        }
        catch
        {
            EnsureVisible(root);
        }
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
}
