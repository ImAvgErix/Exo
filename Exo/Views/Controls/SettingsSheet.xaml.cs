using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Exo.Helpers;
using Exo.ViewModels;

namespace Exo.Views.Controls;

/// <summary>Settings dropdown content (Flyout). No modal overlay.</summary>
public sealed partial class SettingsSheet : UserControl
{
    /// <summary>Matches gear crank so spin + menu read as one open motion.</summary>
    public const int OpenMs = 220;

    private Storyboard? _openSb;
    private bool _openFinished;

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
        FinishOpen();
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
