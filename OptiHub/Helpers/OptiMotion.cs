using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Numerics;

namespace OptiHub.Helpers;

/// <summary>
/// Motion helpers — reliability first.
/// WinUI Composition Opacity overrides XAML Opacity. Setting visual.Opacity=0
/// is what left pages/settings "broken" (invisible forever). We never do that.
/// Layout owns placement; open/close uses Visibility / XAML opacity only.
/// </summary>
public static class OptiMotion
{
    public const int EntranceMs = 0;
    public const int FadeMs = 0;
    public const int StaggerStepMs = 0;

    /// <summary>Kept for settings slider binding; motion is currently off for stability.</summary>
    public static double MotionStrength { get; set; } = 1.0;

    /// <summary>Hard-show at identity. Safe to call any time.</summary>
    public static void EnsureVisible(UIElement el)
    {
        ResetVisual(el, show: true);
    }

    /// <summary>
    /// Clear composition overrides (always opacity 1 on the visual) and snap transforms.
    /// Visibility of overlays is controlled by the caller via Visibility, not composition.
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
            // CRITICAL: composition opacity always 1 — never leave it at 0.
            visual.Opacity = 1f;
            visual.Offset = Vector3.Zero;
            visual.Scale = Vector3.One;
            visual.CenterPoint = Vector3.Zero;
        }
        catch { /* compositor unavailable */ }

        el.Opacity = show ? 1 : 0;
        el.IsHitTestVisible = show;

        // Only reset transform if one already exists (don't inject RenderTransform).
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

    /// <summary>No-op entrance — element stays fully visible (was blanking the UI).</summary>
    public static void PlayEnter(
        UIElement el,
        int delayMs = 0,
        float fromY = 10f,
        float fromScale = 0.98f,
        bool enableHit = true)
    {
        EnsureVisible(el);
    }

    public static void PlayStagger(
        IReadOnlyList<UIElement> items,
        int baseDelayMs = 30,
        int stepMs = StaggerStepMs,
        float fromY = 10f,
        float fromScale = 0.98f)
    {
        foreach (var el in items)
        {
            if (el is null) continue;
            EnsureVisible(el);
        }
    }

    /// <summary>Overlay open: show fully. No composition fade (that fought XAML).</summary>
    public static void PlayOverlayOpen(UIElement scrimHost, UIElement sheet)
    {
        EnsureVisible(scrimHost);
        EnsureVisible(sheet);
        if (sheet is FrameworkElement fe)
            fe.UpdateLayout();
    }

    /// <summary>Overlay close: invoke onDone immediately (caller collapses Visibility).</summary>
    public static void PlayOverlayClose(UIElement scrimHost, UIElement sheet, Action? onDone = null)
    {
        EnsureVisible(scrimHost);
        EnsureVisible(sheet);
        onDone?.Invoke();
    }

    /// <summary>Module page enter: force visible only.</summary>
    public static void PlayPageEnter(UIElement root) => EnsureVisible(root);
}
