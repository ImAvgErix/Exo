using Exo.Models;
using Exo.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;


namespace Exo.Helpers;

/// <summary>
/// Liquid-glass update prompts matching the WebView shell (opaque gradient card,
/// white CTA, quiet secondary, single status line — no double “Downloading”).
/// </summary>
public static class ExoUpdateDialog
{
    /// <summary>Confirm install of a known update. Returns true if user chose Install.</summary>
    public static async Task<bool> ConfirmInstallAsync(
        XamlRoot xamlRoot,
        string localVersion,
        string remoteVersion,
        string? releaseSummary = null)
    {
        var root = new StackPanel { Spacing = 14, MaxWidth = 380 };

        // Eyebrow
        root.Children.Add(new TextBlock
        {
            Text = "UPDATE",
            FontFamily = FontOr("ExoUiFontSemiBold"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            CharacterSpacing = 60,
            Foreground = BrushOr("ExoMutedTextBrush", Color(0x80FFFFFF))
        });

        // Title + version chip
        root.Children.Add(new TextBlock
        {
            Text = $"Exo {remoteVersion}",
            FontFamily = FontOr("ExoUiFontSemiBold"),
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushOr("ExoPrimaryTextBrush", Colors.White),
            Margin = new Thickness(0, -4, 0, 0)
        });

        root.Children.Add(Chip(
            $"v{TrimVer(localVersion)}  →  v{TrimVer(remoteVersion)}",
            muted: true));

        root.Children.Add(new TextBlock
        {
            Text = "Install now — Exo closes, replaces itself, then reopens.",
            FontFamily = FontOr("ExoUiFont"),
            FontSize = 13,
            Foreground = BrushOr("ExoSecondaryTextBrush", Color(0xB3FFFFFF)),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19
        });

        // What’s new card
        var bullets = FormatReleaseBullets(releaseSummary);
        var newsStack = new StackPanel { Spacing = 6 };
        newsStack.Children.Add(new TextBlock
        {
            Text = "What’s new",
            FontFamily = FontOr("ExoUiFontSemiBold"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            CharacterSpacing = 40,
            Foreground = BrushOr("ExoMutedTextBrush", Color(0x80FFFFFF))
        });
        newsStack.Children.Add(new TextBlock
        {
            Text = bullets,
            FontFamily = FontOr("ExoUiFont"),
            FontSize = 13,
            Foreground = BrushOr("ExoSecondaryTextBrush", Color(0xC4FFFFFF)),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19,
            MaxHeight = 120,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        root.Children.Add(new Border
        {
            Background = LinearCardFill(),
            BorderBrush = BrushOr("ExoCardStrokeBrush", Color(0x2E2E38)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 12, 14, 12),
            Child = newsStack
        });

        var dialog = CreateShell(
            title: null, // custom header inside body
            content: root,
            primaryText: "Update now",
            closeText: "Not now",
            xamlRoot: xamlRoot);
        dialog.DefaultButton = ContentDialogButton.Primary;

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Modal install UI: one phase label + percent + bar (never duplicates “Downloading”).
    /// </summary>
    public static async Task<AppUpdateResult> InstallWithProgressAsync(
        XamlRoot xamlRoot,
        AppUpdateResult check,
        GitHubUpdateService updater,
        CancellationToken ct = default)
    {
        var phaseTb = new TextBlock
        {
            Text = "Preparing",
            FontFamily = FontOr("ExoUiFontSemiBold"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            CharacterSpacing = 50,
            Foreground = BrushOr("ExoMutedTextBrush", Color(0x80FFFFFF)),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var pctTb = new TextBlock
        {
            Text = "0%",
            FontFamily = FontOr("ExoUiFontSemiBold"),
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushOr("ExoPrimaryTextBrush", Colors.White),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var hintTb = new TextBlock
        {
            Text = $"Getting Exo v{TrimVer(check.RemoteVersion)} ready…",
            FontFamily = FontOr("ExoUiFont"),
            FontSize = 12,
            Foreground = BrushOr("ExoSecondaryTextBrush", Color(0xB3FFFFFF)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };

        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Foreground = new SolidColorBrush(Colors.White),
            Background = new SolidColorBrush(Color(0x33000000)),
            IsIndeterminate = false
        };

        var card = new Border
        {
            Background = LinearCardFill(),
            BorderBrush = BrushOr("ExoCardStrokeBrush", Color(0x2E2E38)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 12, 14, 14),
            Child = new StackPanel
            {
                Spacing = 4,
                Children = { phaseTb, pctTb, hintTb, bar }
            }
        };

        var body = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 360,
            Children =
            {
                new TextBlock
                {
                    Text = "UPDATE",
                    FontFamily = FontOr("ExoUiFontSemiBold"),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    CharacterSpacing = 60,
                    Foreground = BrushOr("ExoMutedTextBrush", Color(0x80FFFFFF))
                },
                new TextBlock
                {
                    Text = $"Installing v{TrimVer(check.RemoteVersion)}",
                    FontFamily = FontOr("ExoUiFontSemiBold"),
                    FontSize = 18,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = BrushOr("ExoPrimaryTextBrush", Colors.White)
                },
                card
            }
        };

        var dialog = CreateShell(
            title: null,
            content: body,
            primaryText: null,
            closeText: null,
            xamlRoot: xamlRoot);

        AppUpdateResult? installResult = null;
        Exception? fault = null;
        var lastPhase = "";

        dialog.Opened += async (_, _) =>
        {
            try
            {
                var progress = new Progress<AppUpdateProgress>(p =>
                {
                    var phase = PhaseFromStatus(p.Status);
                    if (!string.IsNullOrEmpty(phase) && phase != lastPhase)
                    {
                        lastPhase = phase;
                        phaseTb.Text = phase.ToUpperInvariant();
                        hintTb.Text = HintForPhase(phase, check.RemoteVersion);
                    }

                    if (p.Percent >= 0)
                    {
                        var pct = Math.Clamp(p.Percent, 0, 100);
                        bar.IsIndeterminate = false;
                        bar.Value = pct;
                        pctTb.Text = $"{pct:0}%";
                    }
                    else
                    {
                        bar.IsIndeterminate = true;
                        pctTb.Text = "…";
                    }
                });

                installResult = await updater.InstallAppUpdateAsync(check, status: null, progress, ct)
                    .ConfigureAwait(true);

                if (installResult.ShouldExit)
                {
                    bar.IsIndeterminate = false;
                    bar.Value = 100;
                    pctTb.Text = "100%";
                    phaseTb.Text = "RESTARTING";
                    hintTb.Text = "Closing so the installer can finish…";
                    await Task.Delay(450, ct).ConfigureAwait(true);
                }
                else
                {
                    bar.IsIndeterminate = false;
                    phaseTb.Text = "FAILED";
                    hintTb.Text = string.IsNullOrWhiteSpace(installResult.Message)
                        ? "Update did not complete."
                        : installResult.Message;
                    pctTb.Text = "—";
                    await Task.Delay(2400, CancellationToken.None).ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                fault = null;
                installResult = new AppUpdateResult
                {
                    UpdateAvailable = true,
                    LocalVersion = check.LocalVersion,
                    RemoteVersion = check.RemoteVersion,
                    ReleaseSummary = check.ReleaseSummary,
                    Message = "Update cancelled."
                };
            }
            catch (Exception ex)
            {
                fault = ex;
                installResult = new AppUpdateResult
                {
                    UpdateAvailable = true,
                    LocalVersion = check.LocalVersion,
                    RemoteVersion = check.RemoteVersion,
                    ReleaseSummary = check.ReleaseSummary,
                    Message = ex.Message
                };
            }
            finally
            {
                try { dialog.Hide(); } catch { /* already closed */ }
            }
        };

        try { await dialog.ShowAsync(); }
        catch { /* dismiss race */ }

        if (fault is not null)
            throw fault;

        return installResult ?? new AppUpdateResult
        {
            UpdateAvailable = true,
            LocalVersion = check.LocalVersion,
            RemoteVersion = check.RemoteVersion,
            ReleaseSummary = check.ReleaseSummary,
            Message = "Update did not complete."
        };
    }

    /// <summary>Simple branded message (errors / info).</summary>
    public static async Task ShowMessageAsync(XamlRoot xamlRoot, string title, string message)
    {
        var body = new StackPanel
        {
            Spacing = 10,
            MaxWidth = 360,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontFamily = FontOr("ExoUiFontSemiBold"),
                    FontSize = 18,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = BrushOr("ExoPrimaryTextBrush", Colors.White)
                },
                new TextBlock
                {
                    Text = message,
                    FontFamily = FontOr("ExoUiFont"),
                    FontSize = 13,
                    Foreground = BrushOr("ExoSecondaryTextBrush", Color(0xB3FFFFFF)),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 19
                }
            }
        };
        var dialog = CreateShell(
            title: null,
            content: body,
            primaryText: null,
            closeText: "OK",
            xamlRoot: xamlRoot);
        dialog.DefaultButton = ContentDialogButton.Close;
        await dialog.ShowAsync();
    }

    private static ContentDialog CreateShell(
        string? title,
        UIElement content,
        string? primaryText,
        string? closeText,
        XamlRoot xamlRoot)
    {
        var dialog = new ContentDialog
        {
            // Prefer custom in-body titles so chrome stays clean glass
            Title = title,
            Content = content,
            XamlRoot = xamlRoot,
            CornerRadius = new CornerRadius(20),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(22, 20, 22, 18),
            Background = LiquidGlassFill(),
            BorderBrush = BrushOr("ExoCardStrokeBrush", Color(0x2E2E38)),
            Foreground = BrushOr("ExoPrimaryTextBrush", Colors.White),
            RequestedTheme = ElementTheme.Dark
        };

        if (!string.IsNullOrEmpty(primaryText))
        {
            dialog.PrimaryButtonText = primaryText;
            dialog.PrimaryButtonStyle = CreateWhitePrimaryStyle();
        }
        if (!string.IsNullOrEmpty(closeText))
        {
            dialog.CloseButtonText = closeText;
            if (Application.Current.Resources.TryGetValue("ExoQuietButton", out var quiet) && quiet is Style qs)
                dialog.CloseButtonStyle = qs;
            else
                dialog.CloseButtonStyle = CreateQuietStyle();
        }

        return dialog;
    }

    private static Style CreateWhitePrimaryStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Colors.White)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Colors.Black)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Colors.White)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(12)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(18, 10, 18, 10)));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
        style.Setters.Add(new Setter(Control.MinWidthProperty, 120.0));
        return style;
    }

    private static Style CreateQuietStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, BrushOr("ExoQuietButtonFillBrush", Color(0x18FFFFFF))));
        style.Setters.Add(new Setter(Control.ForegroundProperty, BrushOr("ExoPrimaryTextBrush", Colors.White)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, BrushOr("ExoCardStrokeBrush", Color(0x22FFFFFF))));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(12)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16, 10, 16, 10)));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
        return style;
    }

    private static Border Chip(string text, bool muted)
    {
        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = LinearChipFill(),
            BorderBrush = BrushOr("ExoCardStrokeBrush", Color(0x2C2C34)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 7, 12, 7),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = FontOr("ExoUiFontSemiBold"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = muted
                    ? BrushOr("ExoSecondaryTextBrush", Color(0xB3FFFFFF))
                    : BrushOr("ExoPrimaryTextBrush", Colors.White)
            }
        };
    }

    private static string FormatReleaseBullets(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return "· Bug fixes and improvements";

        var lines = summary
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Trim().TrimStart('-', '•', '*', '·').Trim())
            .Where(l => l.Length > 0)
            .Take(6)
            .Select(l => "· " + l)
            .ToArray();

        return lines.Length == 0
            ? "· " + summary.Trim()
            : string.Join("\n", lines);
    }

    /// <summary>Collapse host spam into one phase label.</summary>
    public static string PhaseFromStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return "";
        var t = status.ToLowerInvariant();
        if (t.Contains("restart") || t.Contains("closing") || t.Contains("reopen")) return "Restarting";
        if (t.Contains("apply") || t.Contains("install") || t.Contains("launch")) return "Installing";
        if (t.Contains("verify") || t.Contains("sha") || t.Contains("integrity")) return "Verifying";
        if (t.Contains("download")) return "Downloading";
        if (t.Contains("check") || t.Contains("github")) return "Checking";
        if (t.Contains("what") && t.Contains("new")) return "Update found";
        return "Working";
    }

    private static string HintForPhase(string phase, string? remote)
    {
        var v = TrimVer(remote ?? "");
        return phase switch
        {
            "Checking" => "Looking up the latest release…",
            "Update found" => "Preparing the download…",
            "Downloading" => string.IsNullOrEmpty(v) ? "Downloading installer…" : $"Downloading v{v}…",
            "Verifying" => "Checking file integrity…",
            "Installing" => "Starting the quiet installer…",
            "Restarting" => "Exo will close and reopen…",
            _ => "Working…"
        };
    }

    private static string TrimVer(string v) =>
        string.IsNullOrWhiteSpace(v) ? "?" : v.Trim().TrimStart('v', 'V');

    private static Brush LiquidGlassFill()
    {
        // Matches web .glass: linear-gradient(165deg, #1c1c24 → #141418 → #101014)
        return new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0.15, 0),
            EndPoint = new Windows.Foundation.Point(0.85, 1),
            GradientStops =
            {
                new GradientStop { Color = Color(0xFF1C1C24), Offset = 0 },
                new GradientStop { Color = Color(0xFF141418), Offset = 0.45 },
                new GradientStop { Color = Color(0xFF101014), Offset = 1 }
            }
        };
    }

    private static Brush LinearCardFill() =>
        new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0.5, 0),
            EndPoint = new Windows.Foundation.Point(0.5, 1),
            GradientStops =
            {
                new GradientStop { Color = Color(0xFF1A1A20), Offset = 0 },
                new GradientStop { Color = Color(0xFF151519), Offset = 1 }
            }
        };

    private static Brush LinearChipFill() => LinearCardFill();

    private static Brush BrushOr(string key, Windows.UI.Color fallback)
    {
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out var o) && o is Brush b)
                return b;
        }
        catch { /* ignore */ }
        return new SolidColorBrush(fallback);
    }

    private static FontFamily FontOr(string key)
    {
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out var o) && o is FontFamily f)
                return f;
        }
        catch { /* ignore */ }
        return new FontFamily("Segoe UI Variable");
    }

    private static Windows.UI.Color Color(uint argb) =>
        Windows.UI.Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF));
}
