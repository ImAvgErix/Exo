using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace OptiHub.Helpers;

/// <summary>
/// Single motion language for OptiHub (Kinetics-inspired).
/// Overlay sheet hosts: opacity-only — layout owns centering (no Offset/Scale on hosts).
/// Row/card entrances: short rise + fade on leaf elements only.
/// </summary>
public static class OptiMotion
{
    public const int EntranceMs = 440;
    public const int FadeMs = 360;
    public const int StaggerStepMs = 48;
    public const int PressMs = 80;
    public const int SpringBackMs = 400;

    /// <summary>0–1 multiplies entrance travel. Live-bound from Settings → MotionIntensity.</summary>
    public static double MotionStrength { get; set; } = 1.0;

    private static float Strength => (float)Math.Clamp(MotionStrength, 0.0, 1.0);

    public static EasingFunctionBase Spring() =>
        new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.22 };

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
    /// Must run on every overlay open/close so nothing inherits a corner pin.
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
            // CenterPoint only matters if something scales; keep identity-safe.
            if (el is FrameworkElement fe && fe.ActualWidth > 0 && fe.ActualHeight > 0)
                visual.CenterPoint = new Vector3((float)fe.ActualWidth / 2f, (float)fe.ActualHeight / 2f, 0);
            else
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

    /// <summary>Prime element invisible + slight rise (leaf nodes only — never overlay hosts).</summary>
    public static void PrimeHidden(UIElement el, float fromY = 14f, float fromScale = 0.98f)
    {
        var y = fromY * Strength;
        var s = 1f - (1f - fromScale) * Strength;
        el.Opacity = 0;
        el.IsHitTestVisible = false;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(el);
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Offset");
            visual.StopAnimation("Scale");
            visual.Opacity = 0;
            visual.Offset = new Vector3(0, y, 0);
            visual.Scale = new Vector3(s, s, 1);
            if (el is FrameworkElement fe && fe.ActualWidth > 0 && fe.ActualHeight > 0)
                visual.CenterPoint = new Vector3((float)fe.ActualWidth / 2f, (float)fe.ActualHeight / 2f, 0);
        }
        catch
        {
            var tf = EnsureTransform(el);
            tf.TranslateY = y;
            tf.ScaleX = s;
            tf.ScaleY = s;
        }
    }

    /// <summary>Composition fade + rise + scale for cards/rows (not modal hosts).</summary>
    public static void PlayEnter(
        UIElement el,
        int delayMs = 0,
        float fromY = 14f,
        float fromScale = 0.98f,
        bool enableHit = true)
    {
        var y = fromY * Strength;
        var s = 1f - (1f - fromScale) * Strength;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(el);
            var c = visual.Compositor;

            visual.StopAnimation("Opacity");
            visual.StopAnimation("Offset");
            visual.StopAnimation("Scale");
            if (el is FrameworkElement fe && fe.ActualWidth > 0 && fe.ActualHeight > 0)
                visual.CenterPoint = new Vector3((float)fe.ActualWidth / 2f, (float)fe.ActualHeight / 2f, 0);

            visual.Opacity = 0;
            visual.Offset = new Vector3(0, y, 0);
            visual.Scale = new Vector3(s, s, 1);

            var delay = TimeSpan.FromMilliseconds(delayMs);
            var glide = c.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
            var spring = c.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1.12f), new Vector2(0.36f, 1f));

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

            el.Opacity = 1;
            if (enableHit)
            {
                var captured = el;
                var d = delayMs + 80;
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

            // After entrance, hard-reset to identity so nothing sticks mid-offset.
            var hold = delayMs + EntranceMs + 40;
            _ = Task.Run(async () =>
            {
                await Task.Delay(hold);
                try
                {
                    el.DispatcherQueue?.TryEnqueue(() =>
                    {
                        try
                        {
                            var v = ElementCompositionPreview.GetElementVisual(el);
                            if (el.Opacity < 0.5) return;
                            v.StopAnimation("Offset");
                            v.StopAnimation("Scale");
                            v.Offset = Vector3.Zero;
                            v.Scale = Vector3.One;
                            v.Opacity = 1f;
                        }
                        catch { }
                    });
                }
                catch { }
            });
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

    public static void PlayStagger(
        IReadOnlyList<UIElement> items,
        int baseDelayMs = 40,
        int stepMs = StaggerStepMs,
        float fromY = 12f,
        float fromScale = 0.98f)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is null) continue;
            PlayEnter(items[i], baseDelayMs + i * stepMs, fromY, fromScale);
        }
    }

    /// <summary>
    /// Modal open: scrim + sheet fade only.
    /// Layout (Center alignment) owns position — never Offset/Scale the sheet host.
    /// </summary>
    public static void PlayOverlayOpen(UIElement scrimHost, UIElement sheet)
    {
        // Kill any leftover host transform from prior builds/sessions of composition state.
        ResetVisual(scrimHost, show: false);
        ResetVisual(sheet, show: false);

        // Explicit identity on composition layer (ResetVisual already did this; belt + suspenders).
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
            // Identity forever on host
            sheetVis.Offset = Vector3.Zero;
            sheetVis.Scale = Vector3.One;
            sheetVis.CenterPoint = Vector3.Zero;

            scrimVis.Opacity = 0;
            sheetVis.Opacity = 0;

            var glide = c.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));

            var scrimFade = c.CreateScalarKeyFrameAnimation();
            scrimFade.Duration = TimeSpan.FromMilliseconds(200);
            scrimFade.InsertKeyFrame(0f, 0f);
            scrimFade.InsertKeyFrame(1f, 1f, glide);
            scrimVis.StartAnimation("Opacity", scrimFade);

            var sheetFade = c.CreateScalarKeyFrameAnimation();
            sheetFade.DelayTime = TimeSpan.FromMilliseconds(16);
            sheetFade.Duration = TimeSpan.FromMilliseconds(280);
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
            var sb = new Storyboard();
            sb.Children.Add(FadeSb(scrimHost, 0, 1, 200, 0));
            sb.Children.Add(FadeSb(sheet, 0, 1, 260, 20));
            sb.Begin();
            scrimHost.Opacity = 1;
            sheet.Opacity = 1;
            sheet.IsHitTestVisible = true;
        }
    }

    /// <summary>Modal close: opacity fade only, then hard ResetVisual via onDone path.</summary>
    public static void PlayOverlayClose(UIElement scrimHost, UIElement sheet, Action? onDone = null)
    {
        try
        {
            var scrimVis = ElementCompositionPreview.GetElementVisual(scrimHost);
            var sheetVis = ElementCompositionPreview.GetElementVisual(sheet);
            var c = scrimVis.Compositor;
            var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));

            // Never leave residual offset on host.
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
            scrimFade.Duration = TimeSpan.FromMilliseconds(150);
            scrimFade.InsertKeyFrame(1f, 0f, ease);
            scrimVis.StartAnimation("Opacity", scrimFade);

            var sheetFade = c.CreateScalarKeyFrameAnimation();
            sheetFade.Duration = TimeSpan.FromMilliseconds(130);
            sheetFade.InsertKeyFrame(1f, 0f, ease);
            sheetVis.StartAnimation("Opacity", sheetFade);

            batch.End();

            _ = Task.Delay(190).ContinueWith(_ =>
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

    public static void PlayPageEnter(UIElement root)
    {
        PrimeHidden(root, fromY: 10f, fromScale: 0.99f);
        PlayEnter(root, delayMs: 0, fromY: 10f, fromScale: 0.99f, enableHit: true);
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
}
