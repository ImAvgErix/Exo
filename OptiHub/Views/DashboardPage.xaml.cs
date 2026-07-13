using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OptiHub.Helpers;
using OptiHub.ViewModels;

namespace OptiHub.Views;

/// <summary>
/// Home grid for the fixed 1180×760 shell.
/// Entrance: Composition stagger (no first-frame flash).
/// </summary>
public sealed partial class DashboardPage : Page
{
    private CancellationTokenSource? _refreshCts;
    private bool _entrancePlayed;
    private bool _entranceRunning;

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

    private void Page_Loaded(object sender, RoutedEventArgs e) =>
        _ = TryPlayEntranceAsync();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Returning home: allow a soft re-entrance only if we left previously
        // (first load is handled by Loaded + flag).
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        await ViewModel.RefreshStatesAsync(_refreshCts.Token);
        _ = TryPlayEntranceAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;
        // Next time we land home, play a light re-entrance
        _entrancePlayed = false;
        base.OnNavigatedFrom(e);
    }

    private async Task TryPlayEntranceAsync()
    {
        if (_entrancePlayed || _entranceRunning) return;
        _entranceRunning = true;
        try
        {
            List<UIElement> cards = [];
            for (var attempt = 0; attempt < 28; attempt++)
            {
                cards.Clear();
                if (CardList is not null)
                    CollectCardButtons(CardList, cards);
                if (cards.Count >= ViewModel.Cards.Count && cards.Count > 0)
                    break;
                await Task.Delay(16);
            }

            if (_entrancePlayed) return;

            // Prime BEFORE host is fully visible — kills flicker.
            if (HeroPanel is not null)
                OptiMotion.PrimeHidden(HeroPanel, fromY: 14f, fromScale: 1f);

            foreach (var el in cards)
                OptiMotion.PrimeHidden(el, fromY: 18f, fromScale: 0.96f);

            if (CardList is not null)
                CardList.Opacity = 1;

            // Two frames so composition + layout settle with opacity 0.
            await Task.Delay(32);
            if (_entrancePlayed) return;
            _entrancePlayed = true;

            var sequence = new List<UIElement>();
            if (HeroPanel is not null)
                sequence.Add(HeroPanel);
            sequence.AddRange(cards);

            OptiMotion.PlayStagger(sequence, baseDelayMs: 20, stepMs: 50, fromY: 16f, fromScale: 0.97f);
        }
        finally
        {
            _entranceRunning = false;
        }
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
