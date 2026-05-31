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
