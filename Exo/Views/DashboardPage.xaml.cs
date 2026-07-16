using Exo.Helpers;
using Exo.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Exo.Views;

/// <summary>
/// Brand-only home under the top bar. Soft hero entrance on first load;
/// cached so Back does not rebuild/re-stagger.
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
        StabilizeHome();

        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        await ViewModel.RefreshStatesAsync(_refreshCts.Token);

        if (!_entrancePlayed)
            _ = TryPlayEntranceAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;
        _entranceGen++;
        StabilizeHome();
        base.OnNavigatedFrom(e);
    }

    private void StabilizeHome()
    {
        try
        {
            if (PageRoot is not null)
                ExoMotion.EnsureVisible(PageRoot);
            if (HeroBrand is not null)
                ExoMotion.EnsureVisible(HeroBrand);
            if (HeroTagline is not null)
                ExoMotion.EnsureVisible(HeroTagline);
            if (SoonRow is not null)
                ExoMotion.EnsureVisible(SoonRow);
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
            await Task.Delay(16);
            if (gen != _entranceGen || _entrancePlayed) return;
            _entrancePlayed = true;

            var sequence = new List<UIElement>();
            if (HeroBrand is not null)
                sequence.Add(HeroBrand);
            if (HeroTagline is not null)
                sequence.Add(HeroTagline);
            if (SoonRow is not null)
                sequence.Add(SoonRow);

            if (sequence.Count > 0)
                ExoMotion.PlayStagger(sequence, baseDelayMs: 24, stepMs: 42, fromY: 10f, fromScale: 1f);

            await Task.Delay(420);
            if (gen != _entranceGen) return;
            StabilizeHome();
        }
        finally
        {
            _entranceRunning = false;
        }
    }
}
