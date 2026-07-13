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

    /// <summary>Prime element invisible + offset (call before first paint / host reveal).</summary>
    public static void PrimeHidden(UIElement el, float fromY = 18f, float fromScale = 0.96f)
    {
        el.Opacity = 0;
        el.IsHitTestVisible = false;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(el);
            visual.Opacity = 0;
            visual.Offset = new Vector3(0, fromY, 0);
            visual.Scale = new Vector3(fromScale, fromScale, 1);
            if (el is FrameworkElement fe)
            {
                var w = (float)(fe.ActualWidth > 0 ? fe.ActualWidth : fe.Width);
                var h = (float)(fe.ActualHeight > 0 ? fe.ActualHeight : fe.Height);
                if (w <= 0) w = 100;
                if (h <= 0) h = 40;
                visual.CenterPoint = new Vector3(w / 2f, h / 2f, 0);
            }
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

            if (el is FrameworkElement fe)
            {
                var w = (float)(fe.ActualWidth > 0 ? fe.ActualWidth : (fe.Width > 0 ? fe.Width : 170));
                var h = (float)(fe.ActualHeight > 0 ? fe.ActualHeight : (fe.Height > 0 ? fe.Height : 95));
                visual.CenterPoint = new Vector3(w / 2f, h / 2f, 0);
            }

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
        scrimHost.Opacity = 0;
        sheet.Opacity = 0;
        try
        {
            var scrimVis = ElementCompositionPreview.GetElementVisual(scrimHost);
            var sheetVis = ElementCompositionPreview.GetElementVisual(sheet);
            var c = scrimVis.Compositor;

            scrimVis.Opacity = 0;
            sheetVis.Opacity = 0;
            sheetVis.Offset = new Vector3(0, 26, 0);
            sheetVis.Scale = new Vector3(0.94f, 0.94f, 1);
            if (sheet is FrameworkElement fe)
            {
                var w = (float)(fe.ActualWidth > 0 ? fe.ActualWidth : 420);
                var h = (float)(fe.ActualHeight > 0 ? fe.ActualHeight : 360);
                sheetVis.CenterPoint = new Vector3(w / 2f, h / 2f, 0);
            }

            var glide = c.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
            var spring = c.CreateCubicBezierEasingFunction(new Vector2(0.34f, 1.25f), new Vector2(0.64f, 1f));

            var scrimFade = c.CreateScalarKeyFrameAnimation();
            scrimFade.Duration = TimeSpan.FromMilliseconds(240);
            scrimFade.InsertKeyFrame(0f, 0f);
            scrimFade.InsertKeyFrame(1f, 1f, glide);
            scrimVis.StartAnimation("Opacity", scrimFade);

            var sheetFade = c.CreateScalarKeyFrameAnimation();
            sheetFade.DelayTime = TimeSpan.FromMilliseconds(30);
            sheetFade.Duration = TimeSpan.FromMilliseconds(320);
            sheetFade.InsertKeyFrame(0f, 0f);
            sheetFade.InsertKeyFrame(1f, 1f, glide);
            sheetVis.StartAnimation("Opacity", sheetFade);

            var rise = c.CreateVector3KeyFrameAnimation();
            rise.DelayTime = TimeSpan.FromMilliseconds(20);
            rise.Duration = TimeSpan.FromMilliseconds(460);
            rise.InsertKeyFrame(0f, new Vector3(0, 26, 0));
            rise.InsertKeyFrame(1f, Vector3.Zero, spring);
            sheetVis.StartAnimation("Offset", rise);

            var scale = c.CreateVector3KeyFrameAnimation();
            scale.DelayTime = TimeSpan.FromMilliseconds(20);
            scale.Duration = TimeSpan.FromMilliseconds(460);
            scale.InsertKeyFrame(0f, new Vector3(0.94f, 0.94f, 1));
            scale.InsertKeyFrame(1f, Vector3.One, spring);
            sheetVis.StartAnimation("Scale", scale);

            scrimHost.Opacity = 1;
            sheet.Opacity = 1;
        }
        catch
        {
            // Storyboard fallback
            var sb = new Storyboard();
            var tf = EnsureTransform(sheet);
            tf.TranslateY = 24;
            tf.ScaleX = 0.95;
            tf.ScaleY = 0.95;
            sb.Children.Add(FadeSb(scrimHost, 0, 1, 220, 0));
            sb.Children.Add(FadeSb(sheet, 0, 1, 280, 40));
            sb.Children.Add(SlideYSb(tf, 24, 0, 420, 30));
            sb.Children.Add(ScaleSb(tf, "ScaleX", 0.95, 1, 420, 30));
            sb.Children.Add(ScaleSb(tf, "ScaleY", 0.95, 1, 420, 30));
            sb.Begin();
        }
    }

    /// <summary>Quick fade-out + drop for overlay close.</summary>
    public static void PlayOverlayClose(UIElement scrimHost, UIElement sheet, Action? onDone = null)
    {
        try
        {
            var scrimVis = ElementCompositionPreview.GetElementVisual(scrimHost);
            var sheetVis = ElementCompositionPreview.GetElementVisual(sheet);
            var c = scrimVis.Compositor;
            var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));

            using var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
            var finished = false;
            void Once()
            {
                if (finished) return;
                finished = true;
                onDone?.Invoke();
            }
            batch.Completed += (_, _) => Once();

            var scrimFade = c.CreateScalarKeyFrameAnimation();
            scrimFade.Duration = TimeSpan.FromMilliseconds(180);
            scrimFade.InsertKeyFrame(1f, 0f, ease);
            scrimVis.StartAnimation("Opacity", scrimFade);

            var sheetFade = c.CreateScalarKeyFrameAnimation();
            sheetFade.Duration = TimeSpan.FromMilliseconds(160);
            sheetFade.InsertKeyFrame(1f, 0f, ease);
            sheetVis.StartAnimation("Opacity", sheetFade);

            var drop = c.CreateVector3KeyFrameAnimation();
            drop.Duration = TimeSpan.FromMilliseconds(180);
            drop.InsertKeyFrame(1f, new Vector3(0, 12, 0), ease);
            sheetVis.StartAnimation("Offset", drop);

            batch.End();

            _ = Task.Delay(220).ContinueWith(_ =>
            {
                try { Once(); } catch { }
            });
        }
        catch
        {
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
