using Exo.Helpers;
using Exo.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Exo.Views;

/// <summary>
/// Dense instrument home: machine strip, 2×2 meters, compact optimizer chips.
/// </summary>
public sealed partial class DashboardPage : Page
{
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _checkCts;
    private bool _entrancePlayed;
    private bool _entranceRunning;
    private int _entranceGen;
    private DispatcherTimer? _memoryTimer;
    private DispatcherTimer? _pulseTimer;
    private double _pulsePhase;

    public DashboardViewModel ViewModel { get; }

    public DashboardPage()
    {
        NavigationCacheMode = NavigationCacheMode.Enabled;
        ViewModel = new DashboardViewModel(App.Services);
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.CheckRowSettled += OnCheckRowSettled;
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        StabilizeHome();
        _ = TryPlayEntranceAsync();
        StartMemoryTimer();
        StartPulseTimer();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        StopMemoryTimer();
        StopPulseTimer();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        StabilizeHome();
        StartPulseTimer();

        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;

        try
        {
            await ViewModel.RefreshStatesAsync(ct);
            StartMemoryTimer();

            _checkCts?.Cancel();
            _checkCts?.Dispose();
            _checkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await ViewModel.PlayCheckSequenceAsync(_checkCts.Token);
        }
        catch (OperationCanceledException) { }

        if (!_entrancePlayed)
            _ = TryPlayEntranceAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        StopMemoryTimer();
        StopPulseTimer();
        _checkCts?.Cancel();
        _checkCts?.Dispose();
        _checkCts = null;
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;
        _entranceGen++;
        StabilizeHome();
        base.OnNavigatedFrom(e);
    }

    private void OnCheckRowSettled(OptimizerCheckRowViewModel row)
    {
        try
        {
            foreach (var btn in FindButtons(CheckList))
            {
                if (btn.Tag as string == row.ModuleId)
                {
                    ExoMotion.PlayResultPop(btn);
                    break;
                }
            }
        }
        catch { }
    }

    private static IEnumerable<Button> FindButtons(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button b)
                yield return b;
            foreach (var nested in FindButtons(child))
                yield return nested;
        }
    }

    private void StartMemoryTimer()
    {
        StopMemoryTimer();
        _memoryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _memoryTimer.Tick += (_, _) =>
        {
            try { ViewModel.RefreshLiveMemory(); } catch { }
        };
        _memoryTimer.Start();
        try { ViewModel.RefreshLiveMemory(); } catch { }
    }

    private void StopMemoryTimer()
    {
        if (_memoryTimer is null) return;
        try { _memoryTimer.Stop(); } catch { }
        _memoryTimer = null;
    }

    private void StartPulseTimer()
    {
        StopPulseTimer();
        _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _pulseTimer.Tick += (_, _) =>
        {
            try
            {
                _pulsePhase += 0.18;
                var wave = 0.35 + 0.55 * (0.5 + 0.5 * Math.Sin(_pulsePhase));
                foreach (var row in ViewModel.CheckRows)
                {
                    if (row.Phase == OptimizerCheckPhase.Checking)
                        row.PulseOpacity = wave;
                }
            }
            catch { }
        };
        _pulseTimer.Start();
    }

    private void StopPulseTimer()
    {
        if (_pulseTimer is null) return;
        try { _pulseTimer.Stop(); } catch { }
        _pulseTimer = null;
    }

    private void CheckRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        var id = fe.Tag as string ?? "";
        if (App.MainAppWindow is not MainWindow main) return;
        ExoMotion.PlaySelect(fe);
        switch (id.ToLowerInvariant())
        {
            case "discord": main.NavigateToDiscord(); break;
            case "steam": main.NavigateToSteam(); break;
            case "games": main.NavigateToGames(); break;
            case "internet": main.NavigateToInternet(); break;
            case "nvidia": main.NavigateToNvidia(); break;
            case "riot": main.NavigateToRiot(); break;
            case "epic": main.NavigateToEpic(); break;
        }
    }

    private void NextAction_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainAppWindow is not MainWindow main) return;
        switch (ViewModel.NextActionModule)
        {
            case "Discord": main.NavigateToDiscord(); break;
            case "Steam": main.NavigateToSteam(); break;
            case "Games": main.NavigateToGames(); break;
            case "Internet": main.NavigateToInternet(); break;
            case "NVIDIA": main.NavigateToNvidia(); break;
            case "Riot": main.NavigateToRiot(); break;
            case "Epic": main.NavigateToEpic(); break;
        }
    }

    private void StabilizeHome()
    {
        try
        {
            if (PageRoot is not null) ExoMotion.EnsureVisible(PageRoot);
            if (HeroBlock is not null) ExoMotion.EnsureVisible(HeroBlock);
            if (HeroBrand is not null) ExoMotion.EnsureVisible(HeroBrand);
            if (HeroTagline is not null) ExoMotion.EnsureVisible(HeroTagline);
            if (FrameHero is not null) ExoMotion.EnsureVisible(FrameHero);
            if (StatRow is not null) ExoMotion.EnsureVisible(StatRow);
            foreach (var tile in new UIElement?[] { TileRam, TileCpu, TileGpu, TileNet })
            {
                if (tile is not null) ExoMotion.EnsureVisible(tile);
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
            await Task.Delay(16);
            if (gen != _entranceGen || _entrancePlayed) return;
            _entrancePlayed = true;

            var sequence = new List<UIElement>();
            if (HeroBlock is not null) sequence.Add(HeroBlock);
            foreach (var tile in new UIElement?[] { TileRam, TileCpu, TileGpu, TileNet })
            {
                if (tile is not null) sequence.Add(tile);
            }
            if (FrameHero is not null) sequence.Add(FrameHero);

            if (sequence.Count > 0)
                ExoMotion.PlayStagger(sequence, baseDelayMs: 28, stepMs: 48, fromY: 12f, fromScale: 0.97f);

            await Task.Delay(520);
            if (gen != _entranceGen) return;
            StabilizeHome();
        }
        finally
        {
            _entranceRunning = false;
        }
    }
}
