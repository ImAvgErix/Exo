using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Exo.Helpers;
using Exo.ViewModels;

namespace Exo.Views;

/// <summary>
/// Home grid: soft card stagger on first load; press pulse on select before navigate.
/// Cached so Back does not rebuild/re-stagger (avoids layout glitch / left shift).
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
        // Keep home alive across module round-trips — no recreate, no re-stagger jump.
        NavigationCacheMode = NavigationCacheMode.Enabled;

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
        StabilizeHome();
        _ = TryPlayEntranceAsync();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Returning from a module: clear any leftover select/entrance transforms so
        // centered cards do not sit a few pixels off (the "everything shifted left" glitch).
        StabilizeHome();

        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        await ViewModel.RefreshStatesAsync(_refreshCts.Token);

        // Only animate first paint — replaying stagger on every Back is what felt glitchy.
        if (!_entrancePlayed)
            _ = TryPlayEntranceAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;
        _selecting = false;
        // Keep _entrancePlayed so cached return stays solid (no second stagger).
        _entranceGen++;
        StabilizeHome();
        base.OnNavigatedFrom(e);
    }

    /// <summary>Identity transforms + full opacity on hero/cards — no residual offset.</summary>
    private void StabilizeHome()
    {
        try
        {
            if (PageRoot is not null)
                OptiMotion.EnsureVisible(PageRoot);
            if (HeroTagline is not null)
                OptiMotion.EnsureVisible(HeroTagline);
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
                OptiMotion.PlayStagger(sequence, baseDelayMs: 24, stepMs: 42, fromY: 10f, fromScale: 1f);

            await Task.Delay(520);
            if (gen != _entranceGen) return;
            StabilizeHome();
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
                // Clear pulse transform before leave so cached home returns clean.
                try { OptiMotion.EnsureVisible(btn); } catch { }
                ViewModel.OpenOptimizerCommand.Execute(id);
            }
            finally
            {
                _selecting = false;
            }
        });
    }
}
