using SteamVault.Models;
using SteamVault.ViewModels;
using System.Windows.Input;

namespace SteamVault.Views;

public partial class MyGamesView : System.Windows.Controls.UserControl
{
    public MyGamesView()
    {
        InitializeComponent();
    }

    private void GameCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.Border border && border.DataContext is LibraryEntry entry)
        {
            var vm = DataContext as MyGamesViewModel;
            if (vm != null)
            {
                if (vm.IsExportMode)
                {
                    entry.IsSelectedForExport = !entry.IsSelectedForExport;
                    vm.UpdateExportCount();
                }
                else
                {
                    vm.SelectGameCommand.Execute(entry);
                }
            }
        }
    }
}
