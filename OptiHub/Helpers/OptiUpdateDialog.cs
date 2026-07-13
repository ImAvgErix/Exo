using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OptiHub.Models;
using OptiHub.Services;

namespace OptiHub.Helpers;

/// <summary>
/// In-app update prompts styled like OptiHub (card chrome + percent progress bar).
/// Used by Settings "Check for updates" and launch auto-check.
/// </summary>
public static class OptiUpdateDialog
{
    /// <summary>Confirm install of a known update. Returns true if user chose Install.</summary>
    public static async Task<bool> ConfirmInstallAsync(
        XamlRoot xamlRoot,
        string localVersion,
        string remoteVersion)
    {
        var body = new StackPanel { Spacing = 12, MaxWidth = 380 };
        body.Children.Add(new TextBlock
        {
            Text = $"Version {remoteVersion} is ready.",
            FontFamily = (FontFamily)Application.Current.Resources["OptiUiFontSemiBold"],
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["OptiPrimaryTextBrush"],
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(new TextBlock
        {
            Text =
                $"You have v{localVersion}. This release includes matching Discord, Steam, and NVIDIA optimizers.\n\n" +
                "OptiHub will download, install in place, and reopen.",
            FontFamily = (FontFamily)Application.Current.Resources["OptiUiFont"],
            FontSize = 13,
            Foreground = (Brush)Application.Current.Resources["OptiSecondaryTextBrush"],
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.92,
            LineHeight = 20
        });

        var dialog = CreateShell(
            title: "Update available",
            content: body,
            primaryText: "Install",
            closeText: "Later",
            xamlRoot: xamlRoot);
        dialog.DefaultButton = ContentDialogButton.Primary;

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Modal install UI: percent label + progress bar only (no orbit loader / no MB sizes).
    /// </summary>
    public static async Task<AppUpdateResult> InstallWithProgressAsync(
        XamlRoot xamlRoot,
        AppUpdateResult check,
        GitHubUpdateService updater,
        CancellationToken ct = default)
    {
        var percentTb = new TextBlock
        {
            Text = "0%",
            FontFamily = (FontFamily)Application.Current.Resources["OptiUiFontSemiBold"],
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["OptiPrimaryTextBrush"],
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 4)
        };

        var statusTb = new TextBlock
        {
            Text = "Preparing…",
            FontFamily = (FontFamily)Application.Current.Resources["OptiUiFont"],
            FontSize = 13,
            Foreground = (Brush)Application.Current.Resources["OptiSecondaryTextBrush"],
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360
        };

        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Foreground = (Brush)Application.Current.Resources["OptiAccentBrush"],
            Background = (Brush)Application.Current.Resources["OptiCardStrokeBrush"],
            IsIndeterminate = false,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var body = new StackPanel
        {
            Spacing = 8,
            MaxWidth = 360,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children = { percentTb, statusTb, bar }
        };

        var dialog = CreateShell(
            title: $"Installing v{check.RemoteVersion}",
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
                        percentTb.Text = $"{pct:0}%";
                    }
                });

                installResult = await updater.InstallAppUpdateAsync(check, status: null, progress, ct)
                    .ConfigureAwait(true);

                if (installResult.ShouldExit)
                {
                    statusTb.Text = installResult.Message;
                    bar.IsIndeterminate = false;
                    bar.Value = 100;
                    percentTb.Text = "100%";
                    await Task.Delay(700, ct).ConfigureAwait(true);
                }
                else
                {
                    statusTb.Text = installResult.Message;
                    bar.IsIndeterminate = false;
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
            Message = "Update did not complete."
        };
    }

    /// <summary>Simple branded message (errors / info).</summary>
    public static async Task ShowMessageAsync(XamlRoot xamlRoot, string title, string message)
    {
        var body = new TextBlock
        {
            Text = message,
            FontFamily = (FontFamily)Application.Current.Resources["OptiUiFont"],
            FontSize = 13,
            Foreground = (Brush)Application.Current.Resources["OptiSecondaryTextBrush"],
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
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
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(24, 22, 24, 20)
        };

        if (!string.IsNullOrEmpty(primaryText))
            dialog.PrimaryButtonText = primaryText;
        if (!string.IsNullOrEmpty(closeText))
            dialog.CloseButtonText = closeText;

        try
        {
            dialog.Background = (Brush)Application.Current.Resources["OptiCardFillBrush"];
            dialog.BorderBrush = (Brush)Application.Current.Resources["OptiCardStrokeBrush"];
            dialog.Foreground = (Brush)Application.Current.Resources["OptiPrimaryTextBrush"];
            if (Application.Current.Resources.TryGetValue("OptiAccentBrush", out var accent) && accent is Brush ab)
                dialog.PrimaryButtonStyle = CreateDialogPrimaryStyle(ab);
            if (Application.Current.Resources.TryGetValue("OptiQuietButton", out var quiet) && quiet is Style qs)
                dialog.CloseButtonStyle = qs;
        }
        catch { /* fall back to system chrome */ }

        return dialog;
    }

    private static Style CreateDialogPrimaryStyle(Brush accent)
    {
        if (Application.Current.Resources.TryGetValue("OptiPrimaryButton", out var s) && s is Style existing)
            return existing;

        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, accent));
        style.Setters.Add(new Setter(Control.ForegroundProperty,
            Application.Current.Resources.TryGetValue("OptiOnAccentBrush", out var on) && on is Brush ob
                ? ob
                : new SolidColorBrush(Microsoft.UI.Colors.Black)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(12)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16, 10, 16, 10)));
        style.Setters.Add(new Setter(Control.FontWeightProperty, Microsoft.UI.Text.FontWeights.SemiBold));
        return style;
    }
}
