using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using OptiHub.ViewModels;

namespace OptiHub.Views;

/// <summary>
/// Home grid for the fixed 1180×760 shell — no responsive resize math.
/// </summary>
public sealed partial class DashboardPage : Page
{
    private CancellationTokenSource? _refreshCts;
    private bool _entrancePlayed;

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

    private void Page_Loaded(object sender, RoutedEventArgs e) => PlayStaggerEntrance();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
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

    private void PlayStaggerEntrance()
    {
        if (_entrancePlayed) return;

        var storyboard = new Storyboard();

        if (HeroPanel is not null && HeroTransform is not null)
            AddFadeSlide(storyboard, HeroPanel, HeroTransform, delayMs: 0, fromY: 12, fade: true);

        var cards = new List<UIElement>();
        if (CardList is not null)
            CollectCardButtons(CardList, cards);

        for (var i = 0; i < cards.Count; i++)
        {
            var el = cards[i];
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

            AddFadeSlide(storyboard, el, ct, delayMs: 40 + i * 55, fromY: 16, fade: true);
        }

        if (storyboard.Children.Count == 0) return;
        _entrancePlayed = true;
        storyboard.Begin();
    }

    private static void AddFadeSlide(
        Storyboard board, UIElement target, CompositeTransform transform, int delayMs, double fromY, bool fade)
    {
        var delay = TimeSpan.FromMilliseconds(delayMs);
        // Kinetics spring settle (overshoot) + glide opacity
        var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.32 };
        var glide = new CubicEase { EasingMode = EasingMode.EaseOut };

        if (fade)
        {
            var fadeAnim = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(420),
                BeginTime = delay,
                EasingFunction = glide,
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(fadeAnim, target);
            Storyboard.SetTargetProperty(fadeAnim, "Opacity");
            board.Children.Add(fadeAnim);
        }

        transform.TranslateY = fromY;
        var slide = new DoubleAnimation
        {
            From = fromY,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(480),
            BeginTime = delay,
            EasingFunction = spring,
            EnableDependentAnimation = true
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
