using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OptiHub.Helpers;
using OptiHub.ViewModels;

namespace OptiHub.Views;

/// <summary>
/// Home grid: soft card stagger on load; press pulse on select before navigate.
/// </summary>
public sealed partial class DashboardPage : Page
{
    private CancellationTokenSource? _refreshCts;
    private bool _entrancePlayed;
    private bool _entranceRunning;
    private int _entranceGen;
    private bool _selecting;

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
        OptiMotion.EnsureVisible(PageRoot);
        _ = TryPlayEntranceAsync();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        OptiMotion.EnsureVisible(PageRoot);
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
        _selecting = false;
        base.OnNavigatedFrom(e);
    }

    private async Task TryPlayEntranceAsync()
    {
        if (_entrancePlayed || _entranceRunning) return;
        _entranceRunning = true;
        var gen = ++_entranceGen;
        try
        {
            List<UIElement> cards = [];
            for (var attempt = 0; attempt < 28; attempt++)
            {
                if (gen != _entranceGen) return;
                cards.Clear();
                CollectCardButtons(CardList, cards);
                if (cards.Count >= ViewModel.Cards.Count && cards.Count > 0)
                    break;
                await Task.Delay(16);
            }

            if (gen != _entranceGen || _entrancePlayed) return;
            _entrancePlayed = true;

            var sequence = new List<UIElement>();
            if (HeroTagline is not null)
                sequence.Add(HeroTagline);
            sequence.AddRange(cards);

            if (sequence.Count > 0)
                OptiMotion.PlayStagger(sequence, baseDelayMs: 20, stepMs: 48, fromY: 14f, fromScale: 1f);

            await Task.Delay(560);
            if (gen != _entranceGen) return;
            OptiMotion.EnsureVisible(PageRoot);
            if (HeroTagline is not null)
                OptiMotion.EnsureVisible(HeroTagline);
            foreach (var c in cards)
                OptiMotion.EnsureVisible(c);
        }
        finally
        {
            _entranceRunning = false;
        }
    }

    private static void CollectCardButtons(DependencyObject root, List<UIElement> into)
    {
        if (root is null) return;
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
        if (_selecting) return;
        if (sender is not Button btn) return;
        var id = btn.Tag as string;
        if (string.IsNullOrWhiteSpace(id)) return;
        var card = ViewModel.Cards.FirstOrDefault(c => c.Definition.Id == id);
        if (card is null || card.IsComingSoon || card.Definition.Status == Models.OptimizerStatus.ComingSoon)
            return;

        // Visible select pulse, then navigate.
        _selecting = true;
        OptiMotion.PlaySelect(btn, () =>
        {
            try
            {
                ViewModel.OpenOptimizerCommand.Execute(id);
            }
            finally
            {
                _selecting = false;
            }
        });
    }
}
