using Exo.Helpers;
using Exo.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace Exo.Views.Controls;

/// <summary>Settings dropdown content (Flyout). No modal overlay.</summary>
public sealed partial class SettingsSheet : UserControl
{
    /// <summary>Matches gear crank so spin + menu read as one open motion.</summary>
    public const int OpenMs = 220;

    /// <summary>Mirrors OpenMs so the gear counter-crank + menu rise read as one close motion.</summary>
    public const int CloseMs = 220;

    private Storyboard? _openSb;
    private bool _openFinished;
    private Storyboard? _closeSb;
    private bool _closeFinished;
    private Action? _closeDone;

    public SettingsViewModel ViewModel { get; }

    public SettingsSheet()
    {
        ViewModel = new SettingsViewModel(App.Services);
        InitializeComponent();
        DataContext = ViewModel;
    }

    private void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ConfirmUpdateAsync = (local, remote) =>
            OptiUpdateDialog.ConfirmInstallAsync(XamlRoot, local, remote);
        ViewModel.InstallUpdateAsync = check =>
            OptiUpdateDialog.InstallWithProgressAsync(XamlRoot, check, App.Services.Updater);
    }

    /// <summary>
    /// Menu entrance timed with the gear crank: soft drop from the gear + fade.
    /// XAML storyboards only — ends at identity (no leftover transforms).
    /// </summary>
    public void PlayOpenAnimation()
    {
        try
        {
            _openSb?.Stop();
            _openSb = null;
            _openFinished = false;

            if (SheetRoot is null) return;

            // Soft drop from gear (integer px) + fade; settle to identity (no soft matrix left).
            SheetRoot.Opacity = 0;
            SheetRoot.IsHitTestVisible = false;
            if (SheetTransform is not null)
            {
                SheetTransform.TranslateX = 0;
                SheetTransform.TranslateY = -8;
                SheetTransform.ScaleX = 1;
                SheetTransform.ScaleY = 1;
            }
            SheetRoot.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0);

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var sb = new Storyboard();

            var fade = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(OpenMs),
                EasingFunction = ease,
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(fade, SheetRoot);
            Storyboard.SetTargetProperty(fade, "Opacity");
            sb.Children.Add(fade);

            if (SheetTransform is not null)
            {
                var drop = new DoubleAnimation
                {
                    From = -8,
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(OpenMs),
                    EasingFunction = ease,
                    EnableDependentAnimation = true
                };
                Storyboard.SetTarget(drop, SheetTransform);
                Storyboard.SetTargetProperty(drop, "TranslateY");
                sb.Children.Add(drop);
            }

            sb.Completed += (_, _) => FinishOpen();
            _openSb = sb;
            sb.Begin();

            // Safety: never leave the sheet half-open if Completed is skipped.
            var captured = this;
            _ = Task.Delay(OpenMs + 60).ContinueWith(_ =>
            {
                try { captured.DispatcherQueue?.TryEnqueue(captured.FinishOpen); }
                catch { }
            });
        }
        catch
        {
            FinishOpen();
        }
    }

    private void FinishOpen()
    {
        if (_openFinished) return;
        _openFinished = true;
        try
        {
            if (SheetRoot is not null)
            {
                SheetRoot.Opacity = 1;
                SheetRoot.IsHitTestVisible = true;
            }
            if (SheetTransform is not null)
            {
                SheetTransform.TranslateX = 0;
                SheetTransform.TranslateY = 0;
                SheetTransform.ScaleX = 1;
                SheetTransform.ScaleY = 1;
            }
        }
        catch { }
    }

    /// <summary>Reset for next open (flyout closed).</summary>
    public void ResetOpenVisual()
    {
        try { _openSb?.Stop(); } catch { }
        _openSb = null;
        _openFinished = false;
        try { _closeSb?.Stop(); } catch { }
        _closeSb = null;
        FinishOpen();
    }

    /// <summary>
    /// Mirrored close: fade out + rise back toward the gear, then onDone (the caller
    /// hides the flyout). Storyboard-only; onDone fires exactly once — Completed or
    /// the safety delay — so the flyout always actually closes. ResetOpenVisual runs
    /// after the hide, so a reopened sheet is never left at opacity 0.
    /// </summary>
    public void PlayCloseAnimation(Action? onDone)
    {
        _closeDone = onDone;
        _closeFinished = false;
        try
        {
            // Stop a still-running open so the two storyboards never fight.
            try { _openSb?.Stop(); } catch { }
            _openSb = null;
            _openFinished = true;

            if (SheetRoot is null)
            {
                FinishClose();
                return;
            }

            SheetRoot.IsHitTestVisible = false;
            if (SheetTransform is not null)
            {
                SheetTransform.TranslateX = 0;
                SheetTransform.ScaleX = 1;
                SheetTransform.ScaleY = 1;
            }
            SheetRoot.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0);

            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var sb = new Storyboard();

            var fade = new DoubleAnimation
            {
                From = Math.Clamp(SheetRoot.Opacity, 0, 1),
                To = 0,
                Duration = TimeSpan.FromMilliseconds(CloseMs),
                EasingFunction = ease,
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(fade, SheetRoot);
            Storyboard.SetTargetProperty(fade, "Opacity");
            sb.Children.Add(fade);

            if (SheetTransform is not null)
            {
                var rise = new DoubleAnimation
                {
                    From = SheetTransform.TranslateY,
                    To = -8,
                    Duration = TimeSpan.FromMilliseconds(CloseMs),
                    EasingFunction = ease,
                    EnableDependentAnimation = true
                };
                Storyboard.SetTarget(rise, SheetTransform);
                Storyboard.SetTargetProperty(rise, "TranslateY");
                sb.Children.Add(rise);
            }

            sb.Completed += (_, _) => FinishClose();
            _closeSb = sb;
            sb.Begin();

            // Safety: the flyout must always hide even if Completed is skipped.
            var captured = this;
            _ = Task.Delay(CloseMs + 80).ContinueWith(_ =>
            {
                try { captured.DispatcherQueue?.TryEnqueue(captured.FinishClose); }
                catch { }
            });
        }
        catch
        {
            FinishClose();
        }
    }

    private void FinishClose()
    {
        if (_closeFinished) return;
        _closeFinished = true;
        try { _closeSb?.Stop(); } catch { }
        _closeSb = null;

        var done = _closeDone;
        _closeDone = null;
        try { done?.Invoke(); } catch { }

        // Restore identity + full opacity for the next open (sheet is hidden now).
        ResetOpenVisual();
    }

    private void DarkMode_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsDarkMode)
            ViewModel.IsDarkMode = true;
    }

    private void LightMode_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsLightMode)
            ViewModel.IsLightMode = true;
    }
}
