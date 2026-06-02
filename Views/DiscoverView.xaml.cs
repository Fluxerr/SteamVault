using SteamVault.Services;
using SteamVault.ViewModels;
using System.Windows.Controls;
using System.Windows.Input;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace SteamVault.Views;

public partial class DiscoverView : WpfUserControl
{
    public DiscoverView()
    {
        InitializeComponent();
    }

    private void DiscoverCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.DataContext is SearchResult result)
        {
            var vm = DataContext as DiscoverViewModel;
            vm?.SelectGameCommand.Execute(result);
        }
    }
}