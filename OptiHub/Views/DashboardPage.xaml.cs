using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using OptiHub.ViewModels;

namespace OptiHub.Views;

public sealed partial class DashboardPage : Page
{
    private CancellationTokenSource? _refreshCts;
    private bool _entrancePlayed;
    private double _cardWidth = 280;
    private double _cardHeight = 168;
    private int _columns = 3;

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

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width);
    }

    /// <summary>
    /// Fit columns + card width so the grid never needs horizontal clip.
    /// Vertical overflow scrolls via PageScroll.
    /// </summary>
    private void ApplyResponsiveLayout(double pageWidth)
    {
        if (pageWidth <= 0) return;

        // Page padding ~80 + card margins; leave room so  N cards fit.
        var pad = PageRoot?.Padding.Left + PageRoot?.Padding.Right ?? 80;
        var usable = Math.Max(240, pageWidth - pad);

        var cols = usable >= 920 ? 3 : usable >= 560 ? 2 : 1;
        // Card width: fill columns evenly, cap at 300.
        var gutters = cols * 16.0; // margin 8 each side
        var cardW = Math.Min(300, Math.Floor((usable - gutters) / cols));
        cardW = Math.Max(160, cardW);
        var cardH = Math.Max(140, Math.Round(cardW * 0.60));

        _columns = cols;
        _cardWidth = cardW;
        _cardHeight = cardH;

        if (HeroTitle is not null)
            HeroTitle.FontSize = usable < 520 ? 26 : 34;

        // ItemsWrapGrid is inside the template — find it and set column max.
        if (CardList is not null)
        {
            var wrap = FindDescendant<ItemsWrapGrid>(CardList);
            if (wrap is not null)
                wrap.MaximumRowsOrColumns = cols;

            var buttons = new List<UIElement>();
            CollectCardButtons(CardList, buttons);
            foreach (var el in buttons)
            {
                if (el is not Button btn) continue;
                btn.Width = cardW;
                btn.Height = cardH;
            }
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            var nested = FindDescendant<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        await ViewModel.RefreshStatesAsync(_refreshCts.Token);
        // Cards may bind after first layout — size them then run stagger once.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            ApplyResponsiveLayout(ActualWidth > 0 ? ActualWidth : 900);
            PlayStaggerEntrance();
        });
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
        ApplyResponsiveLayout(ActualWidth > 0 ? ActualWidth : 900);
        // After cards materialize, re-apply sizes then play entrance.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            ApplyResponsiveLayout(ActualWidth > 0 ? ActualWidth : 900);
            PlayStaggerEntrance();
        });
    }

    /// <summary>
    /// Soft entrance: hero fades in; cards only slide (never zero opacity —
    /// that fought status bindings and made first click feel dead).
    /// </summary>
    private void PlayStaggerEntrance()
    {
        if (_entrancePlayed) return;

        var storyboard = new Storyboard();

        if (HeroPanel is not null && HeroTransform is not null)
            AddFadeSlide(storyboard, HeroPanel, HeroTransform, delayMs: 0, fromY: 14, fade: true);

        var cards = new List<UIElement>();
        if (CardList is not null)
            CollectCardButtons(CardList, cards);

        for (var i = 0; i < cards.Count; i++)
        {
            var el = cards[i];
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

            // Slide only — keep opacity from IsComingSoon binding / full opacity.
            AddFadeSlide(storyboard, el, ct, delayMs: 60 + i * 70, fromY: 16, fade: false);
        }

        if (storyboard.Children.Count == 0) return;
        _entrancePlayed = true;
        storyboard.Begin();
    }

    private static void AddFadeSlide(
        Storyboard board, UIElement target, CompositeTransform transform, int delayMs, double fromY, bool fade)
    {
        var delay = TimeSpan.FromMilliseconds(delayMs);

        if (fade)
        {
            var fadeAnim = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(420),
                BeginTime = delay,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
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
