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
    private double _lastLayoutW = -1;
    private double _lastLayoutH = -1;

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
        ApplyResponsiveLayout(e.NewSize.Width, e.NewSize.Height);
    }

    private void PageRoot_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyResponsiveLayout(force: true);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            ApplyResponsiveLayout(force: true);
            PlayStaggerEntrance();
        });
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        await ViewModel.RefreshStatesAsync(_refreshCts.Token);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            ApplyResponsiveLayout(force: true);
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

    /// <summary>
    /// Size the card grid to the real viewport — not a fixed 300px island in the middle.
    /// Uses width + height so maximize / restore / drag-resize stay stable.
    /// </summary>
    private void ApplyResponsiveLayout(double pageW = 0, double pageH = 0, bool force = false)
    {
        if (pageW <= 1) pageW = ActualWidth;
        if (pageH <= 1) pageH = ActualHeight;
        if (pageW <= 1 || pageH <= 1) return;

        // Ignore sub-pixel noise from maximize animations
        if (!force &&
            Math.Abs(pageW - _lastLayoutW) < 2 &&
            Math.Abs(pageH - _lastLayoutH) < 2)
            return;
        _lastLayoutW = pageW;
        _lastLayoutH = pageH;

        // Tighter padding on small windows; roomier on large
        var padX = pageW < 640 ? 16 : pageW < 1000 ? 28 : 40;
        var padTop = pageH < 700 ? 12 : 20;
        var padBottom = pageH < 700 ? 16 : 24;
        if (PageRoot is not null)
            PageRoot.Padding = new Thickness(padX, padTop, padX, padBottom);

        var heroH = HeroPanel?.ActualHeight > 1 ? HeroPanel.ActualHeight + 16 : 100;
        var usableW = Math.Max(200, pageW - padX * 2);
        var usableH = Math.Max(180, pageH - padTop - padBottom - heroH);

        var cardCount = Math.Max(1, ViewModel.Cards.Count);

        // Columns: use the width so cards aren't tiny islands on maximize
        int cols;
        if (usableW >= 1500) cols = 4;
        else if (usableW >= 960) cols = 3;
        else if (usableW >= 560) cols = 2;
        else cols = 1;

        // Prefer a balanced grid (e.g. 8 cards → 4×2 on wide, 3×3-ish on medium)
        var rows = (int)Math.Ceiling(cardCount / (double)cols);
        // If 3 cols leaves a lonely last row of 2 with lots of height free, still fine.

        const double margin = 8; // Button.Margin each side
        var cardW = Math.Floor((usableW - cols * margin * 2) / cols);
        cardW = Math.Clamp(cardW, 150, 520);

        // Height: fill the viewport when possible so maximize uses vertical space
        var cardH = Math.Floor((usableH - rows * margin * 2) / rows);
        var minH = Math.Max(120, cardW * 0.48);
        var maxH = Math.Min(320, cardW * 0.78);
        if (cardH < minH)
        {
            // Not enough vertical room — keep min aspect and let ScrollViewer work
            cardH = minH;
        }
        else
        {
            cardH = Math.Clamp(cardH, minH, maxH);
        }

        if (HeroTitle is not null)
        {
            HeroTitle.FontSize = usableW < 520 ? 24 : usableW < 900 ? 30 : 34;
            HeroTitle.LineHeight = HeroTitle.FontSize + 8;
        }

        var logoSize = Math.Clamp(Math.Min(cardW, cardH) * 0.38, 40, 96);

        if (CardList is null) return;

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
            btn.Margin = new Thickness(margin);

            // Clear leftover entrance TranslateY so resize never leaves cards offset
            if (btn.RenderTransform is CompositeTransform ct)
            {
                ct.TranslateY = 0;
                ct.TranslateX = 0;
                ct.ScaleX = 1;
                ct.ScaleY = 1;
            }

            // Scale logo with card
            var logo = FindDescendant<Image>(btn);
            if (logo is not null)
            {
                logo.Width = logoSize;
                logo.Height = logoSize;
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

            AddFadeSlide(storyboard, el, ct, delayMs: 50 + i * 55, fromY: 16, fade: false);
        }

        if (storyboard.Children.Count == 0) return;
        _entrancePlayed = true;
        storyboard.Completed += (_, _) =>
        {
            // After entrance, pin transforms at 0 so later layouts stay clean
            foreach (var el in cards)
            {
                if (el.RenderTransform is CompositeTransform c)
                    c.TranslateY = 0;
            }
            ApplyResponsiveLayout(force: true);
        };
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
                Duration = TimeSpan.FromMilliseconds(380),
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
            Duration = TimeSpan.FromMilliseconds(380),
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
