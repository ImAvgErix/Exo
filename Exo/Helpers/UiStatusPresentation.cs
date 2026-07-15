namespace Exo.Helpers;

/// <summary>
/// Pure UI tone/glyph mapping for status chips and result banners.
/// Driven by view models / smokes without WinUI dependencies.
/// </summary>
public static class UiStatusPresentation
{
    public enum Tone
    {
        Neutral,
        Success,
        Warning,
        Error,
        Busy
    }

    /// <summary>Map common optimizer UI flags to a single presentation tone.</summary>
    public static Tone FromFlags(bool isBusy, bool hasError, bool hasSuccess, bool isWarning = false)
    {
        if (isBusy) return Tone.Busy;
        if (hasError) return Tone.Error;
        if (isWarning) return Tone.Warning;
        if (hasSuccess) return Tone.Success;
        return Tone.Neutral;
    }

    /// <summary>Segoe MDL2 glyph for tone (matches app banners).</summary>
    public static string GlyphFor(Tone tone) => tone switch
    {
        Tone.Success => "\uE73E", // CheckMark
        Tone.Error => "\uE783",   // ErrorBadge
        Tone.Warning => "\uE7BA", // Warning
        Tone.Busy => "\uE895",    // Sync
        _ => "\uE946"             // Info
    };

    /// <summary>Brush resource key under ThemeResources (for documentation / hosts).</summary>
    public static string BrushKeyFor(Tone tone) => tone switch
    {
        Tone.Success => "ExoSuccessBrush",
        Tone.Error => "ExoErrorBrush",
        Tone.Warning => "ExoWarningBrush",
        Tone.Busy => "ExoMutedTextBrush",
        _ => "ExoPrimaryTextBrush"
    };

    /// <summary>Feature row opacity: intentional inactive slightly dimmed, never hidden.</summary>
    public static double FeatureOpacity(bool isActive) => isActive ? 1.0 : 0.78;

    /// <summary>Status rail opacity for applied vs open feature tiles.</summary>
    public static double FeatureRailOpacity(bool isActive) => isActive ? 1.0 : 0.28;

    /// <summary>Feature / policy row glyph: check when active, block when not.</summary>
    public static string FeatureGlyph(bool isActive) => isActive ? "\uE73E" : "\uE711";

    /// <summary>Message/result banner from success flag (Apply outcome).</summary>
    public static (string Glyph, string BrushKey) BannerForSuccess(bool success)
    {
        var tone = FromFlags(isBusy: false, hasError: !success, hasSuccess: success);
        return (GlyphFor(tone), BrushKeyFor(tone));
    }
}
