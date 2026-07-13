using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using OptiHub.ViewModels;
using HubSection = OptiHub.Models.HubSection;

namespace OptiHub.Views;

public sealed partial class DashboardPage : Page
{
    private CancellationTokenSource? _refreshCts;
    private bool _entrancePlayed;
    private HubSection _section = HubSection.Home;

    public DashboardViewModel ViewModel { get; }

    public DashboardPage()
    {
        ViewModel = new DashboardViewModel(App.Services);
        InitializeComponent();
        DataContext = ViewModel;

        ViewModel.NavigateToOptimizer += (_, id) =>
        {
            if (App.MainAppWindow is not MainWindow mw) return;
            if (id == "discord") mw.NavigateToDiscord();
            else if (id == "steam") mw.NavigateToSteam();
            else if (id == "internet") mw.NavigateToInternet();
            else if (id == "nvidia") mw.NavigateToNvidia();
        };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is HubSection section)
            _section = section;
        else if (e.Parameter is string s && Enum.TryParse<HubSection>(s, true, out var parsed))
            _section = parsed;
        else
            _section = HubSection.Home;

        ViewModel.ApplySection(_section);
        ApplySidebarLayout(animate: false);
        SyncNavChrome();

        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        await ViewModel.RefreshStatesAsync(_refreshCts.Token);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, PlayStaggerEntrance);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;
        base.OnNavigatedFrom(e);
    }

    private void PageRoot_Loaded(object sender, RoutedEventArgs e)
    {
        ApplySidebarLayout(animate: false);
        SyncNavChrome();
        PlayStaggerEntrance();
    }

    private void NavHome_Click(object sender, RoutedEventArgs e) => SwitchSection(HubSection.Home);
    private void NavApps_Click(object sender, RoutedEventArgs e) => SwitchSection(HubSection.Apps);
    private void NavInternet_Click(object sender, RoutedEventArgs e) => SwitchSection(HubSection.Internet);
    private void NavGpu_Click(object sender, RoutedEventArgs e) => SwitchSection(HubSection.Gpu);

    private void Collapse_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleSidebarCommand.Execute(null);
        ApplySidebarLayout(animate: true);
        SyncNavChrome();
    }

    private async void RefreshStats_Click(object sender, RoutedEventArgs e)
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        await ViewModel.RefreshStatesAsync(_refreshCts.Token);
    }

    private void QuickInternet_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainAppWindow is MainWindow mw) mw.NavigateToInternet();
    }

    private void QuickNvidia_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainAppWindow is MainWindow mw) mw.NavigateToNvidia();
    }

    private void QuickDiscord_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainAppWindow is MainWindow mw) mw.NavigateToDiscord();
    }

    private void QuickSteam_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainAppWindow is MainWindow mw) mw.NavigateToSteam();
    }

    private async void SwitchSection(HubSection section)
    {
        if (_section == section) return;
        _section = section;
        _entrancePlayed = false;
        ViewModel.ApplySection(section);
        SyncNavChrome();

        if (section == HubSection.Home)
        {
            _refreshCts?.Cancel();
            _refreshCts?.Dispose();
            _refreshCts = new CancellationTokenSource();
            await ViewModel.RefreshStatesAsync(_refreshCts.Token);
        }

        PlayStaggerEntrance();
    }

    private void ApplySidebarLayout(bool animate)
    {
        var collapsed = ViewModel.SidebarCollapsed;
        var target = collapsed ? 64.0 : 208.0;

        if (BrandBlock is not null)
            BrandBlock.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        if (SidebarHint is not null)
            SidebarHint.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;

        SetLabel(NavHomeLabel, collapsed);
        SetLabel(NavAppsLabel, collapsed);
        SetLabel(NavInternetLabel, collapsed);
        SetLabel(NavGpuLabel, collapsed);

        if (CollapseIcon is not null)
            CollapseIcon.Glyph = collapsed ? "\uE76C" : "\uE76B";
        if (CollapseButton is not null)
            ToolTipService.SetToolTip(CollapseButton, collapsed ? "Expand sidebar" : "Collapse sidebar");

        // Center icon-only buttons when collapsed.
        foreach (var btn in new[] { NavHome, NavApps, NavInternet, NavGpu })
        {
            if (btn is null) continue;
            btn.HorizontalContentAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            btn.Padding = collapsed ? new Thickness(0) : new Thickness(10, 0, 10, 0);
        }

        if (SidebarColumn is not null)
            SidebarColumn.Width = new GridLength(target);
    }

    private static void SetLabel(TextBlock? label, bool collapsed)
    {
        if (label is null) return;
        label.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SyncNavChrome()
    {
        HighlightNav(NavHome, _section == HubSection.Home);
        HighlightNav(NavApps, _section == HubSection.Apps);
        HighlightNav(NavInternet, _section == HubSection.Internet);
        HighlightNav(NavGpu, _section == HubSection.Gpu);
    }

    private void HighlightNav(Button? btn, bool active)
    {
        if (btn is null) return;
        if (active)
        {
            btn.Background = ThemeBrush("OptiAccentBrush")
                ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 250, 250, 250));
            btn.Foreground = ThemeBrush("OptiOnAccentBrush")
                ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0));
            btn.BorderThickness = new Thickness(0);
            // Keep icons/labels readable on accent fill.
            if (btn.Content is StackPanel sp)
            {
                foreach (var child in sp.Children)
                {
                    if (child is FontIcon fi)
                        fi.Foreground = btn.Foreground;
                    else if (child is TextBlock tb)
                        tb.Foreground = btn.Foreground;
                }
            }
        }
        else
        {
            btn.ClearValue(Control.BackgroundProperty);
            btn.ClearValue(Control.ForegroundProperty);
            btn.ClearValue(Control.BorderThicknessProperty);
            var ink = ThemeBrush("OptiPrimaryTextBrush")
                ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 250, 250, 250));
            if (btn.Content is StackPanel sp)
            {
                foreach (var child in sp.Children)
                {
                    if (child is FontIcon fi)
                        fi.Foreground = ink;
                    else if (child is TextBlock tb)
                        tb.Foreground = ink;
                }
            }
        }
    }

    private Brush? ThemeBrush(string key)
    {
        try
        {
            var theme = ActualTheme == ElementTheme.Light ? "Light" : "Dark";
            if (Application.Current.Resources.ThemeDictionaries.TryGetValue(theme, out var raw)
                && raw is ResourceDictionary dict
                && dict.TryGetValue(key, out var val)
                && val is Brush brush)
                return brush;
            if (Application.Current.Resources.TryGetValue(key, out var fallback) && fallback is Brush b)
                return b;
        }
        catch { }
        return null;
    }

    private void PlayStaggerEntrance()
    {
        if (_entrancePlayed) return;

        var storyboard = new Storyboard();

        if (HeroPanel is not null && HeroTransform is not null)
            AddFadeSlide(storyboard, HeroPanel, HeroTransform, delayMs: 0, fromY: 12);

        var cards = new List<UIElement>();
        if (ViewModel.IsHome && StatList is not null)
            CollectContentControls(StatList, cards);
        else if (CardList is not null)
            CollectCardButtons(CardList, cards);

        for (var i = 0; i < cards.Count; i++)
        {
            var el = cards[i];
            el.Opacity = 0;
            if (el.RenderTransform is not CompositeTransform ct)
            {
                ct = new CompositeTransform { TranslateY = 14 };
                el.RenderTransform = ct;
                el.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            }
            else
            {
                ct.TranslateY = 14;
            }

            AddFadeSlide(storyboard, el, ct, delayMs: 60 + i * 55, fromY: 14);
        }

        if (storyboard.Children.Count == 0) return;
        _entrancePlayed = true;
        storyboard.Begin();
    }

    private static void AddFadeSlide(Storyboard board, UIElement target, CompositeTransform transform, int delayMs, double fromY)
    {
        var delay = TimeSpan.FromMilliseconds(delayMs);

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(380),
            BeginTime = delay,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fade, target);
        Storyboard.SetTargetProperty(fade, "Opacity");
        board.Children.Add(fade);

        transform.TranslateY = fromY;
        var slide = new DoubleAnimation
        {
            From = fromY,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(420),
            BeginTime = delay,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, "TranslateY");
        board.Children.Add(slide);
    }

    private static void CollectCardButtons(DependencyObject root, List<UIElement> into)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button { Tag: string } btn)
                into.Add(btn);
            CollectCardButtons(child, into);
        }
    }

    private static void CollectContentControls(DependencyObject root, List<UIElement> into)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ContentControl cc && cc.Style is not null)
                into.Add(cc);
            CollectContentControls(child, into);
        }
    }

    private void CardButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        var id = btn.Tag as string;
        if (string.IsNullOrWhiteSpace(id)) return;
        var card = ViewModel.Cards.FirstOrDefault(c => c.Definition.Id == id);
        if (card is null || card.IsComingSoon || card.Definition.Status == Models.OptimizerStatus.ComingSoon)
            return;
        ViewModel.OpenOptimizerCommand.Execute(id);
    }
}
