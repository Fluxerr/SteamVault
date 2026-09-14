using System.Windows;

namespace SteamVault.Views;

public partial class ModernDialogWindow : Window
{
    public ModernDialogWindow(string message, string title = "SteamVault", bool yesNo = true)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;

        if (!yesNo)
        {
            NoBtn.Visibility = Visibility.Collapsed;
            YesBtn.Content = "OK";
        }
    }

    private void YesBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void NoBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
