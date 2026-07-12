using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OptiHub.Models;
using OptiHub.Services;
using OptiHub.ViewModels;

namespace OptiHub.Views;

public sealed partial class NvidiaPanelSettingsDialog : ContentDialog
{
    private readonly NvidiaPanelSettingsService _panel;
    private readonly ObservableCollection<NvidiaPolicyRowViewModel> _rows = new();
    private bool _busy;

    public NvidiaPanelSettingsDialog(NvidiaPanelSettingsService panel)
    {
        _panel = panel;
        InitializeComponent();
        PolicyList.ItemsSource = _rows;
        Loaded += OnLoaded;
        PrimaryButtonClick += OnApplyAllClick;
    }

    /// <summary>True if the user successfully applied policy.</summary>
    public bool RequestedApply { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshRowsAsync();
    }

    private async Task RefreshRowsAsync()
    {
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        PolicyList.Visibility = Visibility.Collapsed;
        FooterText.Text = "Checking driver state...";

        try
        {
            var snapshot = await _panel.ProbePolicyAsync();
            _rows.Clear();
            foreach (var item in snapshot)
            {
                var row = new NvidiaPolicyRowViewModel
                {
                    Id = item.Id,
                    Title = item.Title
                };
                row.SetResult(item.IsApplied, item.Detail);
                _rows.Add(row);
            }

            var missing = _rows.Count(r => !r.IsApplied);
            FooterText.Text = missing == 0
                ? "All OptiHub NVIDIA policies are applied."
                : $"{missing} not applied. Apply sets OptiHub policy on the driver (primary max Hz, secondary 60 Hz).";
            IsPrimaryButtonEnabled = missing > 0;
            PrimaryButtonText = missing > 0 ? "Apply all" : "Done";
        }
        catch (Exception ex)
        {
            FooterText.Text = $"Could not check driver state: {ex.Message}";
            IsPrimaryButtonEnabled = true;
            PrimaryButtonText = "Apply all";
        }
        finally
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            PolicyList.Visibility = Visibility.Visible;
        }
    }

    private async void ApplyRow_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await RunApplyAsync();
    }

    private async void OnApplyAllClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (PrimaryButtonText == "Done")
            return;

        var deferral = args.GetDeferral();
        try
        {
            if (_busy)
            {
                args.Cancel = true;
                return;
            }

            var ok = await RunApplyAsync();
            if (!ok)
                args.Cancel = true;
            else
                RequestedApply = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task<bool> RunApplyAsync()
    {
        _busy = true;
        IsPrimaryButtonEnabled = false;
        FooterText.Text = "Applying OptiHub NVIDIA policy to the driver...";

        try
        {
            // Fixed OptiHub policy — no user toggles/dropdowns
            var settings = NvidiaPanelSettings.CreateDefaults();
            // Explicit refresh policy: primary max (gaming), secondary 60
            settings.PrimaryRefresh = "max";
            settings.SecondaryRefresh = "60";
            _panel.Save(settings);

            var progress = new Progress<ScriptRunProgress>(p =>
            {
                if (!string.IsNullOrWhiteSpace(p.Status))
                    FooterText.Text = p.Status;
            });

            var (ok, message) = await _panel.ApplyDisplayPolicyAsync(settings, progress);
            RequestedApply = ok;
            await RefreshRowsAsync();

            FooterText.Text = message;
            var stillMissing = _rows.Any(r => !r.IsApplied);
            IsPrimaryButtonEnabled = stillMissing;
            PrimaryButtonText = stillMissing ? "Apply all" : "Done";
            return ok;
        }
        catch (Exception ex)
        {
            FooterText.Text = $"Apply failed: {ex.Message}";
            IsPrimaryButtonEnabled = true;
            return false;
        }
        finally
        {
            _busy = false;
        }
    }
}
