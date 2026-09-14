using SteamVault.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

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

    private void HistoryItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && 
            element.DataContext is string historyText &&
            DataContext is DashboardViewModel vm)
        {
            vm.AppIdInput = historyText;
            vm.DownloadCommand.Execute(null);
        }
    }

    private void DropArea_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            e.Effects = System.Windows.DragDropEffects.Copy;
        else
            e.Effects = System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void DropArea_Drop(object sender, System.Windows.DragEventArgs e)
    {
        // Animate drop: quick scale bounce
        AnimateDropZoneExit();
        AnimateDropBounce();

        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
            if (DataContext is DashboardViewModel vm)
            {
                vm.ImportFilesCommand.Execute(files);
            }
        }
    }

    private void DropArea_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;

        if (DataContext is DashboardViewModel vm)
            vm.IsDragOver = true;

        // Animate the drop zone: scale up, glow, icon bounce
        var duration = new Duration(TimeSpan.FromMilliseconds(250));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // Scale up visibly
        var scaleXAnim = new DoubleAnimation(1.05, duration) { EasingFunction = ease };
        var scaleYAnim = new DoubleAnimation(1.05, duration) { EasingFunction = ease };
        DropZoneScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scaleXAnim);
        DropZoneScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scaleYAnim);

        // Glow effect
        var glowBlur = new DoubleAnimation(30, duration) { EasingFunction = ease };
        var glowOpacity = new DoubleAnimation(0.7, duration) { EasingFunction = ease };
        DropZoneGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, glowBlur);
        DropZoneGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, glowOpacity);

        // Border color to accent
        try
        {
            var accentColor = (System.Windows.Media.Color)FindResource("PrimaryAccentColor");
            var borderAnim = new ColorAnimation(accentColor, duration) { EasingFunction = ease };
            DropZoneBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, borderAnim);

            // Subtle background tint
            var accentMutedColor = (System.Windows.Media.Color)FindResource("PrimaryAccentMutedColor");
            var bgAnim = new ColorAnimation(accentMutedColor, duration) { EasingFunction = ease };
            DropZoneBg.BeginAnimation(SolidColorBrush.ColorProperty, bgAnim);
        }
        catch { }

        // Icon float animation
        var iconFloat = new DoubleAnimation(-6, new Duration(TimeSpan.FromMilliseconds(400)))
        {
            EasingFunction = ease,
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        DropZoneIconTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, iconFloat);

        // Update text
        DropZoneTitle.Text = "Release to Import";

        e.Handled = true;
    }

    private void DropArea_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        AnimateDropZoneExit();
        e.Handled = true;
    }

    private void AnimateDropZoneExit()
    {
        if (DataContext is DashboardViewModel vm)
            vm.IsDragOver = false;

        var duration = new Duration(TimeSpan.FromMilliseconds(200));
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };

        // Scale back
        var scaleXAnim = new DoubleAnimation(1.0, duration) { EasingFunction = ease };
        var scaleYAnim = new DoubleAnimation(1.0, duration) { EasingFunction = ease };
        DropZoneScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scaleXAnim);
        DropZoneScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scaleYAnim);

        // Remove glow
        var glowBlur = new DoubleAnimation(0, duration) { EasingFunction = ease };
        var glowOpacity = new DoubleAnimation(0, duration) { EasingFunction = ease };
        DropZoneGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, glowBlur);
        DropZoneGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, glowOpacity);

        // Restore border color
        try
        {
            var borderColor = (System.Windows.Media.Color)FindResource("BorderColor");
            var borderAnim = new ColorAnimation(borderColor, duration) { EasingFunction = ease };
            DropZoneBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, borderAnim);

            var surfaceColor = (System.Windows.Media.Color)FindResource("SurfaceRaisedColor");
            var bgAnim = new ColorAnimation(surfaceColor, duration) { EasingFunction = ease };
            DropZoneBg.BeginAnimation(SolidColorBrush.ColorProperty, bgAnim);
        }
        catch { }

        // Stop icon animation
        DropZoneIconTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);

        // Restore text
        DropZoneTitle.Text = "Drop Configs Here";
    }

    private void AnimateDropBounce()
    {
        var bounceEase = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut };

        var scaleXBounce = new DoubleAnimation(0.95, 1.0, new Duration(TimeSpan.FromMilliseconds(300)))
        {
            EasingFunction = bounceEase
        };
        var scaleYBounce = new DoubleAnimation(0.95, 1.0, new Duration(TimeSpan.FromMilliseconds(300)))
        {
            EasingFunction = bounceEase
        };

        DropZoneScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scaleXBounce);
        DropZoneScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scaleYBounce);
    }
}
