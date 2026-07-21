using Exo.Models;
using Exo.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Exo.Helpers;

/// <summary>
/// In-app update prompts styled like the Exo glass shell (dark card, white CTA, quiet secondary).
/// Used by launch auto-check and any native update confirm path.
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
        var body = new StackPanel { Spacing = 12, MaxWidth = 360 };

        // Version chip row (matches Settings “Exo 3.x” muted meta)
        body.Children.Add(new Border
        {
            Background = BrushOr("ExoQuietButtonFillBrush", Color(0x14FFFFFF)),
            BorderBrush = BrushOr("ExoCardStrokeBrush", Color(0x22FFFFFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 8, 12, 8),
            Child = new TextBlock
            {
                Text = $"v{localVersion}  →  v{remoteVersion}",
                FontFamily = FontOr("ExoUiFontSemiBold"),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = BrushOr("ExoSecondaryTextBrush", Color(0xB3FFFFFF)),
                TextWrapping = TextWrapping.Wrap
            }
        });

        body.Children.Add(new TextBlock
        {
            Text = "A newer build is ready. Install now and Exo will restart.",
            FontFamily = FontOr("ExoUiFont"),
            FontSize = 14,
            Foreground = BrushOr("ExoPrimaryTextBrush", Colors.White),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21
        });

        var tldr = string.IsNullOrWhiteSpace(releaseSummary)
            ? "Bug fixes and improvements."
            : releaseSummary.Trim();

        body.Children.Add(new TextBlock
        {
            Text = "What’s new",
            FontFamily = FontOr("ExoUiFontSemiBold"),
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 40,
            Foreground = BrushOr("ExoMutedTextBrush", Color(0x80FFFFFF)),
            Margin = new Thickness(0, 2, 0, 0)
        });

        body.Children.Add(new Border
        {
            Background = BrushOr("ExoQuietButtonFillBrush", Color(0x10FFFFFF)),
            BorderBrush = BrushOr("ExoCardStrokeBrush", Color(0x18FFFFFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 10, 12, 10),
            Child = new TextBlock
            {
                Text = tldr,
                FontFamily = FontOr("ExoUiFont"),
                FontSize = 13,
                Foreground = BrushOr("ExoSecondaryTextBrush", Color(0xB3FFFFFF)),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
                MaxHeight = 140,
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        });

        var dialog = CreateShell(
            title: "Update available",
            content: body,
            primaryText: "Update now",
            closeText: "Later",
            xamlRoot: xamlRoot);
        dialog.DefaultButton = ContentDialogButton.Primary;

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Modal install UI: percent + progress bar (glass card chrome).
    /// </summary>
    public static async Task<AppUpdateResult> InstallWithProgressAsync(
        XamlRoot xamlRoot,
        AppUpdateResult check,
        GitHubUpdateService updater,
        CancellationToken ct = default)
    {
        var statusTb = new TextBlock
        {
            Text = "Starting…",
            FontFamily = FontOr("ExoUiFontSemiBold"),
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = BrushOr("ExoPrimaryTextBrush", Colors.White),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 4)
        };

        var pctTb = new TextBlock
        {
            Text = "0%",
            FontFamily = FontOr("ExoUiFontSemiBold"),
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = BrushOr("ExoPrimaryTextBrush", Colors.White),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10)
        };

        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Foreground = new SolidColorBrush(Colors.White),
            Background = BrushOr("ExoQuietButtonFillBrush", Color(0x22FFFFFF)),
            IsIndeterminate = false
        };

        var body = new StackPanel
        {
            Spacing = 2,
            MaxWidth = 340,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children = { statusTb, pctTb, bar }
        };

        var dialog = CreateShell(
            title: $"Updating to v{check.RemoteVersion}",
            content: body,
            primaryText: null,
            closeText: null,
            xamlRoot: xamlRoot);

        AppUpdateResult? installResult = null;
        Exception? fault = null;

        dialog.Opened += async (_, _) =>
        {
            try
            {
                var progress = new Progress<AppUpdateProgress>(p =>
                {
                    if (!string.IsNullOrWhiteSpace(p.Status))
                        statusTb.Text = p.Status;
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
                    }
                });

                installResult = await updater.InstallAppUpdateAsync(check, status: null, progress, ct)
                    .ConfigureAwait(true);

                if (installResult.ShouldExit)
                {
                    bar.IsIndeterminate = false;
                    bar.Value = 100;
                    pctTb.Text = "100%";
                    statusTb.Text = "Closing so the installer can finish…";
                    await Task.Delay(500, ct).ConfigureAwait(true);
                }
                else
                {
                    bar.IsIndeterminate = false;
                    statusTb.Text = string.IsNullOrWhiteSpace(installResult.Message)
                        ? "Update did not complete."
                        : installResult.Message;
                    pctTb.Text = "—";
                    // Keep the dialog open long enough to read the error
                    await Task.Delay(2200, CancellationToken.None).ConfigureAwait(true);
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
        var body = new TextBlock
        {
            Text = message,
            FontFamily = FontOr("ExoUiFont"),
            FontSize = 13,
            Foreground = BrushOr("ExoSecondaryTextBrush", Color(0xB3FFFFFF)),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360,
            LineHeight = 20
        };
        var dialog = CreateShell(
            title: title,
            content: body,
            primaryText: null,
            closeText: "OK",
            xamlRoot: xamlRoot);
        dialog.DefaultButton = ContentDialogButton.Close;
        await dialog.ShowAsync();
    }

    private static ContentDialog CreateShell(
        string title,
        UIElement content,
        string? primaryText,
        string? closeText,
        XamlRoot xamlRoot)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            XamlRoot = xamlRoot,
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(22, 20, 22, 18),
            // Match web glass card feel (dark raised surface)
            Background = BrushOr("ExoCardFillBrush", Color(0xE6121216)),
            BorderBrush = BrushOr("ExoCardStrokeBrush", Color(0x28FFFFFF)),
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

    /// <summary>White filled CTA — same language as WebView “Apply” / “Check for updates”.</summary>
    private static Style CreateWhitePrimaryStyle()
    {
        if (Application.Current.Resources.TryGetValue("ExoPrimaryButton", out var s) && s is Style existing)
        {
            // Prefer white-on-black for this dialog if available
        }

        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Colors.White)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Colors.Black)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Colors.White)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(12)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(18, 10, 18, 10)));
        style.Setters.Add(new Setter(Control.FontWeightProperty, Microsoft.UI.Text.FontWeights.SemiBold));
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
        style.Setters.Add(new Setter(Control.FontWeightProperty, Microsoft.UI.Text.FontWeights.SemiBold));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
        return style;
    }

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
