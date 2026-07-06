using SteamVault.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SteamVault.Views;

public partial class DashboardView : System.Windows.Controls.UserControl
{
    public DashboardView()
    {
        InitializeComponent();
    }

    private async void DashboardView_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is DashboardViewModel vm)
            await vm.LoadFeaturedGamesAsync();
    }

    private void SearchResult_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && 
            element.DataContext is Services.SearchResult result &&
            DataContext is DashboardViewModel vm)
        {
            vm.SelectSearchResultCommand.Execute(result);
        }
    }
}
