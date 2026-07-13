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
    private HubSection _section = HubSection.Apps;

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

        ViewModel.ApplySection(_section);
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
        SyncNavChrome();
        PlayStaggerEntrance();
    }

    private void NavApps_Click(object sender, RoutedEventArgs e) => SwitchSection(HubSection.Apps);
    private void NavInternet_Click(object sender, RoutedEventArgs e) => SwitchSection(HubSection.Internet);
    private void NavGpu_Click(object sender, RoutedEventArgs e) => SwitchSection(HubSection.Gpu);

    private void SwitchSection(HubSection section)
    {
        if (_section == section) return;
        _section = section;
        _entrancePlayed = false;
        ViewModel.ApplySection(section);
        SyncNavChrome();
        PlayStaggerEntrance();
    }

    private void SyncNavChrome()
    {
        HighlightNav(NavApps, _section == HubSection.Apps);
        HighlightNav(NavInternet, _section == HubSection.Internet);
        HighlightNav(NavGpu, _section == HubSection.Gpu);
    }

    private void HighlightNav(Button btn, bool active)
    {
        if (active)
        {
            btn.Background = ThemeBrush("OptiAccentBrush")
                ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 250, 250, 250));
            btn.Foreground = ThemeBrush("OptiOnAccentBrush")
                ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0));
            btn.BorderThickness = new Thickness(0);
        }
        else
        {
            btn.ClearValue(Control.BackgroundProperty);
            btn.ClearValue(Control.ForegroundProperty);
            btn.ClearValue(Control.BorderThicknessProperty);
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
            AddFadeSlide(storyboard, HeroPanel, HeroTransform, delayMs: 0, fromY: 14);

        var cards = new List<UIElement>();
        if (CardList is not null)
            CollectCardButtons(CardList, cards);

        for (var i = 0; i < cards.Count; i++)
        {
            var el = cards[i];
            el.Opacity = 0;
            if (el.RenderTransform is not CompositeTransform ct)
            {
                ct = new CompositeTransform { TranslateY = 16 };
                el.RenderTransform = ct;
                el.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            }
            else
            {
                ct.TranslateY = 16;
            }

            AddFadeSlide(storyboard, el, ct, delayMs: 80 + i * 90, fromY: 16);
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
            Duration = TimeSpan.FromMilliseconds(420),
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
            Duration = TimeSpan.FromMilliseconds(480),
            BeginTime = delay,
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 }
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
