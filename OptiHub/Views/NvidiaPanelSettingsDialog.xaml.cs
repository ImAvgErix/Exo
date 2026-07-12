using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OptiHub.Models;
using OptiHub.Services;

namespace OptiHub.Views;

public sealed partial class NvidiaPanelSettingsDialog : ContentDialog
{
    private readonly NvidiaPanelSettingsService _panel;

    public NvidiaPanelSettingsDialog(NvidiaPanelSettingsService panel)
    {
        _panel = panel;
        InitializeComponent();
        // Fix ToggleSwitch with two OffContent in XAML - Force3d is always on for apply path
        Force3dToggle.IsOn = true;
        Force3dToggle.IsEnabled = false;
        Force3dToggle.OnContent = "Always on (full Apply)";
        Force3dToggle.OffContent = "Always on";

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadFromSettings(_panel.Load());
        try
        {
            LiveStatusText.Text = await _panel.GetLiveStatusSummaryAsync();
        }
        catch (Exception ex)
        {
            LiveStatusText.Text = $"Live status unavailable: {ex.Message}";
        }
    }

    public NvidiaPanelSettings CaptureSettings()
    {
        return new NvidiaPanelSettings
        {
            PrimaryRefresh = TagOf(PrimaryRefreshBox) ?? "max",
            SecondaryRefresh = TagOf(SecondaryRefreshBox) ?? "60",
            FullRgb = FullRgbToggle.IsOn,
            GpuNoScaling = GpuNoScalingToggle.IsOn,
            ScalingOverride = ScalingOverrideToggle.IsOn,
            VideoNvidiaColor = VideoColorToggle.IsOn,
            VideoNvidiaImage = VideoImageToggle.IsOn,
            Force3dProfiles = true,
            DeveloperCounters = DevCountersToggle.IsOn,
            StripAppAndControlPanel = StripClientsToggle.IsOn
        };
    }

    private void LoadFromSettings(NvidiaPanelSettings s)
    {
        SelectByTag(PrimaryRefreshBox, s.PrimaryRefresh);
        SelectByTag(SecondaryRefreshBox, s.SecondaryRefresh);
        FullRgbToggle.IsOn = s.FullRgb;
        GpuNoScalingToggle.IsOn = s.GpuNoScaling;
        ScalingOverrideToggle.IsOn = s.ScalingOverride;
        VideoColorToggle.IsOn = s.VideoNvidiaColor;
        VideoImageToggle.IsOn = s.VideoNvidiaImage;
        DevCountersToggle.IsOn = s.DeveloperCounters;
        StripClientsToggle.IsOn = s.StripAppAndControlPanel;
        Force3dToggle.IsOn = true;
    }

    private static void SelectByTag(ComboBox box, string tag)
    {
        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                return;
            }
        }
        if (box.Items.Count > 0)
            box.SelectedIndex = 0;
    }

    private static string? TagOf(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Tag as string;
}
