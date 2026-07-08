using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OptiHub.Helpers;
using OptiHub.ViewModels;

namespace OptiHub.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardViewModel ViewModel { get; }

    public DashboardPage()
    {
        ViewModel = new DashboardViewModel(App.Services);
        Resources["BoolToOpacityConverter"] = new BoolToOpacityConverter();

        InitializeComponent();
        DataContext = ViewModel;

        ViewModel.NavigateToOptimizer += (_, id) =>
        {
            if (id == "discord" && App.MainAppWindow is MainWindow mw)
                mw.NavigateToDiscord();
        };
        ViewModel.NavigateToSettings += (_, _) =>
        {
            if (App.MainAppWindow is MainWindow mw)
                mw.NavigateToSettings();
        };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.RefreshStatesAsync();
    }

    private void OptimizerGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not OptimizerCardViewModel card) return;
        if (card.IsComingSoon || card.Definition.Status == Models.OptimizerStatus.ComingSoon)
            return;
        ViewModel.OpenOptimizerCommand.Execute(card.Definition.Id);
    }
}
