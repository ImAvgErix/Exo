using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OptiHub.Helpers;
using OptiHub.ViewModels;

namespace OptiHub.Views;

/// <summary>
/// Home grid for the fixed 1180×760 shell.
/// Cards start visible; soft stagger is optional cosmetics with hard fail-safe.
/// </summary>
public sealed partial class DashboardPage : Page
{
    private CancellationTokenSource? _refreshCts;
    private bool _entrancePlayed;
    private bool _entranceRunning;
    private int _entranceGen;

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

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        ForceHomeVisible();
        _ = TryPlayEntranceAsync();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Returning home: always show content first (never leave primed-hidden).
        ForceHomeVisible();
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
        _entrancePlayed = false;
        _entranceGen++;
        // Leaving home: leave nothing half-hidden for next visit.
        ForceHomeVisible();
        base.OnNavigatedFrom(e);
    }

    private void ForceHomeVisible()
    {
        try
        {
            OptiMotion.EnsureVisible(PageRoot);
            if (HeroPanel is not null)
                OptiMotion.EnsureVisible(HeroPanel);
            if (CardList is not null)
                OptiMotion.EnsureVisible(CardList);

            if (CardList is not null)
            {
                List<UIElement> cards = [];
                CollectCardButtons(CardList, cards);
                foreach (var c in cards)
                    OptiMotion.EnsureVisible(c);
            }
        }
        catch { }
    }

    private async Task TryPlayEntranceAsync()
    {
        if (_entrancePlayed || _entranceRunning) return;
        _entranceRunning = true;
        var gen = ++_entranceGen;
        try
        {
            // Always visible baseline.
            ForceHomeVisible();

            List<UIElement> cards = [];
            for (var attempt = 0; attempt < 24; attempt++)
            {
                if (gen != _entranceGen) return;
                cards.Clear();
                if (CardList is not null)
                    CollectCardButtons(CardList, cards);
                if (cards.Count >= ViewModel.Cards.Count && cards.Count > 0)
                    break;
                await Task.Delay(16);
            }

            if (gen != _entranceGen || _entrancePlayed) return;
            _entrancePlayed = true;

            // Soft stagger is optional; fail-safe forces full visibility after.
            var sequence = new List<UIElement>();
            if (HeroPanel is not null)
                sequence.Add(HeroPanel);
            sequence.AddRange(cards);
            if (sequence.Count > 0)
                OptiMotion.PlayStagger(sequence, baseDelayMs: 10, stepMs: 36, fromY: 10f, fromScale: 0.98f);

            await Task.Delay(520);
            if (gen != _entranceGen) return;
            ForceHomeVisible();
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
