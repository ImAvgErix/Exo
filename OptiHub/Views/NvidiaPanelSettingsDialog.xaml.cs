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
        PrimaryButtonClick += OnFixAllClick;
    }

    /// <summary>True if the user asked to apply (Fix all or any Fix).</summary>
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
                ? "All OptiHub NVIDIA policies are applied on the driver."
                : $"{missing} item(s) not applied. Fix applies OptiHub policy at the driver (NVAPI).";
            IsPrimaryButtonEnabled = missing > 0;
            PrimaryButtonText = missing > 0 ? "Fix all" : "Done";
        }
        catch (Exception ex)
        {
            FooterText.Text = $"Could not check driver state: {ex.Message}";
            IsPrimaryButtonEnabled = true;
            PrimaryButtonText = "Fix all";
        }
        finally
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            PolicyList.Visibility = Visibility.Visible;
        }
    }

    private async void FixRow_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not Button { Tag: string id }) return;
        await RunFixAsync(id);
    }

    private async void OnFixAllClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (PrimaryButtonText == "Done")
            return;

        // Defer close until apply finishes
        var deferral = args.GetDeferral();
        try
        {
            if (_busy)
            {
                args.Cancel = true;
                return;
            }

            var ok = await RunFixAsync(null);
            if (!ok)
                args.Cancel = true; // keep dialog open on failure
            else
                RequestedApply = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task<bool> RunFixAsync(string? singleId)
    {
        _busy = true;
        IsPrimaryButtonEnabled = false;
        FooterText.Text = singleId is null
            ? "Applying all OptiHub NVIDIA policies to the driver..."
            : "Applying fix...";

        try
        {
            // Always apply the OptiHub-correct defaults (not user toggles)
            var settings = NvidiaPanelSettings.CreateDefaults();
            _panel.Save(settings);

            var progress = new Progress<ScriptRunProgress>(p =>
            {
                if (!string.IsNullOrWhiteSpace(p.Status))
                    FooterText.Text = p.Status;
            });

            var (ok, message) = await _panel.ApplyDisplayPolicyAsync(settings, progress);
            RequestedApply = ok;
            await RefreshRowsAsync();

            FooterText.Text = ok
                ? message
                : message;

            IsPrimaryButtonEnabled = _rows.Any(r => !r.IsApplied);
            if (!_rows.Any(r => !r.IsApplied))
                PrimaryButtonText = "Done";

            return ok;
        }
        catch (Exception ex)
        {
            FooterText.Text = $"Fix failed: {ex.Message}";
            IsPrimaryButtonEnabled = true;
            return false;
        }
        finally
        {
            _busy = false;
        }
    }
}
