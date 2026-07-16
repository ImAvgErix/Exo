using Exo.Helpers;
using Exo.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Exo.Views;

/// <summary>
/// Home performance dashboard under the top bar. Soft entrance on first load;
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
    }

    private DispatcherTimer? _memoryTimer;

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        StabilizeHome();
        _ = TryPlayEntranceAsync();
        StartMemoryTimer();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        StopMemoryTimer();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        StabilizeHome();

        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        await ViewModel.RefreshStatesAsync(_refreshCts.Token);
        StartMemoryTimer();

        if (!_entrancePlayed)
            _ = TryPlayEntranceAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        StopMemoryTimer();
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;
        _entranceGen++;
        StabilizeHome();
        base.OnNavigatedFrom(e);
    }

    private void StartMemoryTimer()
    {
        StopMemoryTimer();
        _memoryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _memoryTimer.Tick += (_, _) =>
        {
            try { ViewModel.RefreshLiveMemory(); } catch { }
        };
        _memoryTimer.Start();
    }

    private void StopMemoryTimer()
    {
        if (_memoryTimer is null) return;
        try { _memoryTimer.Stop(); } catch { }
        _memoryTimer = null;
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
            if (FrameHero is not null)
                ExoMotion.EnsureVisible(FrameHero);
            if (StatRow is not null)
                ExoMotion.EnsureVisible(StatRow);
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
            if (HeroBlock is not null)
                sequence.Add(HeroBlock);
            if (FrameHero is not null)
                sequence.Add(FrameHero);
            if (StatRow is not null)
                sequence.Add(StatRow);
            if (SoonRow is not null)
                sequence.Add(SoonRow);

            // Semantic page chunks stagger ~90ms apart (matches preview cadence).
            if (sequence.Count > 0)
                ExoMotion.PlayStagger(sequence, baseDelayMs: 40, stepMs: 90, fromY: 10f, fromScale: 1f);

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
