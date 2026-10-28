namespace SteamVault.Views;

public partial class FamilyVaultView : System.Windows.Controls.UserControl
{
    public FamilyVaultView()
    {
        InitializeComponent();
    }

    private void SearchResult_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement element && 
            element.DataContext is SteamVault.Services.SearchResult result &&
            DataContext is SteamVault.ViewModels.FamilyVaultViewModel vm)
        {
            vm.SelectSearchResultCommand.Execute(result);
        }
    }

    private void ScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is System.Windows.Controls.ScrollViewer scv)
        {
            scv.ScrollToVerticalOffset(scv.VerticalOffset - (e.Delta * 0.6));
            e.Handled = true;
        }
    }
}
